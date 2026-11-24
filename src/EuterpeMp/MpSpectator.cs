using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace EuterpeMp;

/// <summary>
/// One live score record from the server's spectator stream.
/// The website decodes 31-byte binary frames (Euterpe web bundle, decoder `gC`):
///   u64 uid | u32 chartTickMs | u32 score | f32 accuracy(0..1) | u16 miss | u8 flags
///   | u16 pingMs | u16 early | u16 late | u16 great
/// flags bit0 = alive, bit1 = full combo.
/// </summary>
public sealed class MpLiveScore
{
    public int Uid;
    public long ChartTickMs;
    public long Score;
    public double AccuracyPercent;
    public int Miss;
    public bool Alive;
    public bool FullCombo;
    public int PingMs;
    public int Early;
    public int Late;
    public int Great;

    public static MpLiveScore Parse(ReadOnlySpan<byte> d)
    {
        var uid = (long)BinaryPrimitives.ReadUInt64LittleEndian(d);
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        var score = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
        var acc = BinaryPrimitives.ReadSingleLittleEndian(d[16..]);
        var miss = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);
        var flags = d[22];
        var ping = BinaryPrimitives.ReadUInt16LittleEndian(d[23..]);
        var early = BinaryPrimitives.ReadUInt16LittleEndian(d[25..]);
        var late = BinaryPrimitives.ReadUInt16LittleEndian(d[27..]);
        var great = BinaryPrimitives.ReadUInt16LittleEndian(d[29..]);
        return new MpLiveScore
        {
            Uid = (int)Math.Clamp(uid, 0, int.MaxValue),
            ChartTickMs = tick,
            Score = score,
            AccuracyPercent = Math.Clamp(acc, 0f, 1f) * 100.0,
            Miss = miss,
            Alive = (flags & 1) != 0,
            FullCombo = (flags & 2) != 0,
            PingMs = ping,
            Early = early,
            Late = late,
            Great = great,
        };
    }
}

/// <summary>
/// Read-only spectator client for the Euterpe multiplayer server.
///
/// This is the same mechanism the website uses on /multiplayer: a ticket from
/// POST /api/mp/ticket, a WS auth, then {t:"observe_room", d:{ref:room_id}}.
/// The server pushes:
///   * JSON: room_state / chat / chat_history / round_record / round_history
///   * binary 31-byte score frames: live accuracy + score of every entrant
///
/// Spectator connections are how the website shows the live accuracy; they do not
/// touch the game's own connection.
/// </summary>
public sealed class MpSpectator : IAsyncDisposable
{
    public sealed class Options
    {
        public string? SignpostUrl = "https://euterpe-org.com/mp/signpost.json";
        public string TicketUrl = "https://euterpe-org.com/api/mp/ticket";
        public string? RoomRef;
        public Action<string>? Log;
    }

    private readonly Options _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public string? WebSocketUrl { get; private set; }
    public int Protocol { get; private set; }
    public int SnapshotSeq { get; private set; }
    public MpRoomState? Room { get; private set; }
    public bool IsConnected => _ws is { State: WebSocketState.Open };
    public string? LastError { get; private set; }

    public event Action<MpRoomState>? RoomChanged;
    public event Action<MpLiveScore[]>? LiveScores;
    public event Action<MpRoundRecord>? RoundFinished;
    public event Action<JsonElement>? RoundHistory;
    public event Action<string>? Chat;
    public event Action<string>? Error;

    public MpSpectator(Options options) => _options = options;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var tokens = EuterpeSession.TryLoad(null, out var err);
        if (tokens == null) throw new MpAuthExpiredException("session.dat: " + err);
        if (tokens.LooksExpired) throw new MpAuthExpiredException("access token expired; launch Muse Dash once");

        var (wsUrls, protocol) = await FETCH_PlanAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        Protocol = protocol;
        var ticket = await FetchTicketAsync(tokens.AccessToken, ct).ConfigureAwait(false);

        Exception? last = null;
        foreach (var url in wsUrls)
        {
            try
            {
                WebSocketUrl = url;
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await _ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // Website spectator auth: only ticket + protocol (no mod_version/game_version),
                // so the server treats this connection as a web observer rather than a second
                // mod session (which would displace the game's own connection).
                await SendAsync(new { t = "auth", v = (int?)null, d = new { ticket, protocol } }, ct).ConfigureAwait(false);

                _pump = Task.Run(() => PumpAsync(_cts.Token));
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log($"spectator route failed {url}: {ex.Message}");
            }
        }

        throw last ?? new InvalidOperationException("no spectator route");
    }

    private async Task<(string[] urls, int protocol)> FETCH_PlanAsync(string token, CancellationToken ct)
    {
        var signpost = _options.SignpostUrl!;
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(signpost, ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var prod = MpJson.Str(root, "production") ?? "blue";
        string[] urls = Array.Empty<string>();
        var protocol = 8;
        if (MpJson.TryGet(root, out var colours, "colours") && MpJson.TryGet(colours, out var plan, prod))
        {
            protocol = (int)(MpJson.Num(plan, "protocol") ?? 8);
            if (MpJson.TryGet(plan, out var ws, "ws") && ws.ValueKind == JsonValueKind.Array)
                urls = ws.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).ToArray();
        }
        if (urls.Length == 0) throw new InvalidOperationException("no ws urls in signpost");
        return (urls, protocol);
    }

    private async Task<string> FetchTicketAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _options.TicketUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString());
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new MpAuthExpiredException($"ticket HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        using var doc = JsonDocument.Parse(body);
        var ticket = MpJson.Str(doc.RootElement, "ticket");
        if (string.IsNullOrEmpty(ticket)) throw new InvalidOperationException("ticket missing in response");
        return ticket;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
        {
            WebSocketReceiveResult result;
            ms.SetLength(0);
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = ms.ToArray();
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                DecodeScores(payload);
                continue;
            }

            HandleJson(Encoding.UTF8.GetString(payload));
        }
    }

    private void DecodeScores(byte[] payload)
    {
        const int recordSize = 31;
        var count = payload.Length / recordSize;
        if (count == 0) return;
        var scores = new MpLiveScore[count];
        for (var i = 0; i < count; i++)
            scores[i] = MpLiveScore.Parse(payload.AsSpan(i * recordSize, recordSize));
        LiveScores?.Invoke(scores);
    }

    private void HandleJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = MpJson.Str(root, "t");
            var seq = (int?)MpJson.Num(root, "v") ?? 0;
            var payload = MpJson.TryGet(root, out var d, "d") ? d : default;
            switch (kind)
            {
                case "auth_ok":
                    Protocol = (int?)MpJson.Num(payload, "server_protocol", "protocol") ?? Protocol;
                    Log($"[spectate] auth ok protocol={Protocol}");
                    if (!string.IsNullOrWhiteSpace(_options.RoomRef))
                    {
                        _ = SendAsync(new { t = "observe_room", v = (int?)null, d = new { @ref = _options.RoomRef } });
                        Log($"[spectate] observe_room ref={_options.RoomRef}");
                    }
                    break;
                case "room_state":
                    SnapshotSeq = seq;
                    Room = MpRoomState.Parse(payload, seq, json);
                    RoomChanged?.Invoke(Room);
                    break;
                case "round_record":
                    RoundFinished?.Invoke(MpRoundRecord.Parse(payload));
                    break;
                case "round_history":
                    RoundHistory?.Invoke(payload.Clone());
                    break;
                case "chat":
                    Chat?.Invoke(payload.GetRawText());
                    break;
                case "error":
                    var code = MpJson.Str(payload, "code") ?? "unknown";
                    LastError = code;
                    Log("[spectate] error " + code);
                    Error?.Invoke(code);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("[spectate] parse failed: " + ex.Message);
        }
    }

    private async Task SendAsync(object message, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_ws is { State: WebSocketState.Open })
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _ws?.Dispose();
        _http.Dispose();
        _cts?.Dispose();
        if (_pump != null)
        {
            try { await _pump.ConfigureAwait(false); } catch { }
        }
    }
}
