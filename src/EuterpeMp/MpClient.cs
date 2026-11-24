using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace EuterpeMp;

/// <summary>How the request/response envelope carries the action or event name.</summary>
public enum MpEnvelopeStyle
{
    /// <summary>{"auth": {...}} — guessed from the serde enum strings observed in Euterpe.Hot.dll.</summary>
    ExternallyTagged,
    /// <summary>{"type":"auth", ...}</summary>
    TypeField,
    /// <summary>{"action":"auth", ...}</summary>
    ActionField,
    /// <summary>{"t":"auth","v":null,"d":{...}} — the wire envelope observed from the real server (2026-11-24).</summary>
    Tvd,
}

public sealed class MpOptions
{
    public string? GameRoot { get; set; }
    public string ApiBase { get; set; } = "https://euterpe-org.com";
    public string SignpostPath { get; set; } = "/mp/signpost.json";
    public string TicketPath { get; set; } = "/api/mp/ticket";
    public int Protocol { get; set; } = 8;
    public string ModVersion { get; set; } = "mp8";
    public string GameVersion { get; set; } = "6.8.0";
    public MpEnvelopeStyle Envelope { get; set; } = MpEnvelopeStyle.ExternallyTagged;
    /// <summary>Optional literal ws url; when null the signpost + /health probe picks a route.</summary>
    public string? WebSocketUrlOverride { get; set; }
    public bool SendKeepAlive { get; set; }
    public int ReceiveBufferSize { get; set; } = 1 << 20;
    public string? RawFrameLogPath { get; set; }
    public Action<string>? Log { get; set; }
}

public sealed class MpTicket
{
    public string Value = "";
    public string Raw = "";
    public int Protocol;
    public string Endpoint = "";
}

/// <summary>Thrown when the local Euterpe session token is stale; the game refreshes it on launch.</summary>
public sealed class MpAuthExpiredException : Exception
{
    public MpAuthExpiredException(string message) : base(message) { }
}

public sealed class MpProtocolException : Exception
{
    public string? Code { get; }
    public MpProtocolException(string message, string? code = null) : base(message) => Code = code;
}

/// <summary>
/// Minimal Euterpe multiplayer client:
///   session.dat (DPAPI JSON) -> /mp/signpost.json -> /api/mp/ticket -> wss://.../ws -> JSON events.
/// It only reads: this client never uploads scores and never writes to the Euterpe data files.
/// </summary>
public sealed class MpClient : IAsyncDisposable
{
    private readonly MpOptions _opt;
    private readonly HttpClient _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public EuterpeSession.Tokens? Tokens { get; private set; }
    public MpTicket? Ticket { get; private set; }
    public string? WebSocketUrl { get; private set; }
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public MpRoomState? Room { get; private set; }
    public string? ServerBuild { get; private set; }
    public int? ServerProtocol { get; private set; }
    public (int min, int max) PlayerLimits { get; private set; }

    public event Action<MpEvent>? EventReceived;
    public event Action<string>? RawFrameReceived;
    public event Action<MpRoomState>? RoomChanged;
    public event Action<string, string?>? ErrorReceived;
    public event Action<MpRoundRecord>? RoundRecordReceived;
    public event Action<int, int>? BlacklistReceived; // rev, entry count

    public MpClient(MpOptions? options = null)
    {
        _opt = options ?? new MpOptions();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AccLineChart-EuterpeMp/0.1");
    }

    public void Log(string message) => _opt.Log?.Invoke(message);

    // ------------------------------------------------------------------ HTTP

    public sealed class SignpostColour
    {
        public string Name = "";
        public int Protocol;
        public List<string> WebSocketUrls = new();
    }

    public async Task<(string production, List<SignpostColour> colours)> FetchSignpostAsync(CancellationToken ct = default)
    {
        var url = _opt.ApiBase + _opt.SignpostPath;
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var production = MpJson.Str(root, "production") ?? "";
        var colours = new List<SignpostColour>();
        if (MpJson.TryGet(root, out var coloursEl, "colours") && coloursEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in coloursEl.EnumerateObject())
            {
                var c = new SignpostColour { Name = prop.Name };
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    c.Protocol = (int)(MpJson.Num(prop.Value, "protocol") ?? 0);
                    if (MpJson.TryGet(prop.Value, out var wsEl, "ws", "websocket", "urls") && wsEl.ValueKind == JsonValueKind.Array)
                        foreach (var w in wsEl.EnumerateArray())
                            if (w.ValueKind == JsonValueKind.String && w.GetString() is { } s)
                                c.WebSocketUrls.Add(s);
                }
                colours.Add(c);
            }
        }
        Log($"signpost: production={production} colours={string.Join(",", colours.Select(c => $"{c.Name}:p{c.Protocol}"))}");
        return (production, colours);
    }

    /// <summary>GET /health per route host and return the lowest-latency ws url.</summary>
    public async Task<string> ProbeRoutesAsync(string production, List<SignpostColour> colours, CancellationToken ct = default)
    {
        if (_opt.WebSocketUrlOverride is { Length: > 0 } forced) return forced;
        var colour = colours.FirstOrDefault(c => string.Equals(c.Name, production, StringComparison.OrdinalIgnoreCase))
                     ?? colours.FirstOrDefault();
        if (colour == null || colour.WebSocketUrls.Count == 0)
            throw new MpProtocolException("signpost returned no websocket urls");
        if (colour.Protocol != 0 && colour.Protocol != _opt.Protocol)
            Log($"warning: signpost protocol {colour.Protocol} differs from configured {_opt.Protocol}");

        if (colour.WebSocketUrls.Count == 1) return colour.WebSocketUrls[0];
        var best = colour.WebSocketUrls[0];
        var bestMs = double.MaxValue;
        foreach (var ws in colour.WebSocketUrls)
        {
            var uri = new Uri(ws);
            var health = $"https://{uri.Host}/health";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await _http.GetAsync(health, ct).ConfigureAwait(false);
                sw.Stop();
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                ServerBuild = MpJson.Str(doc.RootElement, "build");
                ServerProtocol = (int?)MpJson.Num(doc.RootElement, "protocol");
                if (sw.Elapsed.TotalMilliseconds < bestMs)
                {
                    bestMs = sw.Elapsed.TotalMilliseconds;
                    best = ws;
                }
            }
            catch (Exception ex)
            {
                Log($"health probe {uri.Host} failed: {ex.Message}");
            }
        }
        Log($"route chosen: {new Uri(best).Host} ({bestMs:0}ms) build={ServerBuild} protocol={ServerProtocol}");
        return best;
    }

    /// <summary>POST /api/mp/ticket. The server requires an X-Request-Id header and a bearer access token.</summary>
    public async Task<MpTicket> FetchTicketAsync(string accessToken, CancellationToken ct = default)
    {
        var url = _opt.ApiBase + _opt.TicketPath;
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        req.Content = new StringContent("{\"protocol\":" + _opt.Protocol + "}", Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var code = ExtractErrorCode(body);
            if (code is "TOKEN_EXPIRED" or "TOKEN_INVALID" or "MISSING_TOKEN" or "UNAUTHORIZED")
                throw new MpAuthExpiredException($"Euterpe session token rejected ({code}); launch the game once so Euterpe refreshes session.dat");
            throw new MpProtocolException($"ticket request failed: HTTP {(int)resp.StatusCode} {body}", code);
        }
        var value = FindTicketValue(body);
        if (string.IsNullOrEmpty(value))
            throw new MpProtocolException("ticket response had no ticket value: " + body);
        Log($"ticket minted (len {value.Length})");
        return new MpTicket { Value = value, Raw = body, Protocol = _opt.Protocol, Endpoint = url };
    }

    private static string? ExtractErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var e in MpJson.Descendants(doc.RootElement))
            {
                var code = MpJson.Str(e, "code");
                if (!string.IsNullOrEmpty(code)) return code;
            }
        }
        catch
        {
            // not JSON
        }
        return null;
    }

    private static string? FindTicketValue(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var e in MpJson.Descendants(doc.RootElement))
            {
                if (!MpJson.TryGet(e, out var v, "ticket", "ticket_id", "ticketId", "value", "token")) continue;
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
            }
            if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString();
        }
        catch
        {
            // maybe a bare ticket string
            var trimmed = body.Trim();
            if (trimmed.Length is > 8 and < 4096 && !trimmed.StartsWith("{")) return trimmed;
        }
        return null;
    }

    // ------------------------------------------------------------------ WebSocket

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Tokens = EuterpeSession.TryLoad(_opt.GameRoot, out var error)
                 ?? throw new MpAuthExpiredException("cannot read Euterpe session: " + error);
        if (Tokens.LooksExpired)
            throw new MpAuthExpiredException(
                $"Euterpe access token expired at {Tokens.AccessTokenExpiresAt:O}; launch Muse Dash once so Euterpe refreshes session.dat, then retry");

        var (production, colours) = await FetchSignpostAsync(ct).ConfigureAwait(false);
        WebSocketUrl = await ProbeRoutesAsync(production, colours, ct).ConfigureAwait(false);
        Ticket = await FetchTicketAsync(Tokens.AccessToken, ct).ConfigureAwait(false);

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await ws.ConnectAsync(new Uri(WebSocketUrl), ct).ConfigureAwait(false);
        _ws = ws;
        Log($"websocket connected: {WebSocketUrl}");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        await SendActionAsync("auth", new Dictionary<string, object?>
        {
            ["ticket"] = Ticket.Value,
            ["protocol"] = _opt.Protocol,
            ["mod_version"] = _opt.ModVersion,
            ["game_version"] = _opt.GameVersion,
        }, ct).ConfigureAwait(false);
        Log("auth sent");
    }

    public async Task SendActionAsync(string action, object? payload, CancellationToken ct = default)
    {
        var json = BuildEnvelope(action, payload);
        await SendRawAsync(json, ct).ConfigureAwait(false);
    }

    public string BuildEnvelope(string action, object? payload)
    {
        var payloadJson = payload == null ? "{}" : JsonSerializer.Serialize(payload);
        return _opt.Envelope switch
        {
            MpEnvelopeStyle.ExternallyTagged => "{\"" + action + "\":" + payloadJson + "}",
            MpEnvelopeStyle.TypeField => MergeWithField("type", action, payloadJson),
            MpEnvelopeStyle.ActionField => MergeWithField("action", action, payloadJson),
            MpEnvelopeStyle.Tvd => "{\"t\":" + JsonSerializer.Serialize(action) + ",\"v\":null,\"d\":" + payloadJson + "}",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static string MergeWithField(string field, string action, string payloadJson)
    {
        var actionJson = JsonSerializer.Serialize(action);
        if (payloadJson == "{}" || string.IsNullOrWhiteSpace(payloadJson))
            return "{\"" + field + "\":" + actionJson + "}";
        return "{\"" + field + "\":" + actionJson + "," + payloadJson.TrimStart().TrimStart('{');
    }

    public async Task SendRawAsync(string json, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open }) throw new InvalidOperationException("websocket is not open");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        Log("ws -> " + Truncate(json, 400));
        _opt.RawFrameLogPath?.Let(p => AppendLine(p, "OUT " + json));
    }

    /// <summary>Join a room by its room_id (16-hex string, as listed by /rooms or room_state).</summary>
    public async Task JoinRoomByIdAsync(string roomId, CancellationToken ct = default) =>
        await SendActionAsync("join_room", new Dictionary<string, object?> { ["room_id"] = roomId }, ct);

    /// <summary>Join a private room by its 4-character code (server answers BAD_CODE when it is wrong).</summary>
    public async Task JoinRoomByCodeAsync(string code, CancellationToken ct = default) =>
        await SendActionAsync("join_room", new Dictionary<string, object?> { ["code"] = code }, ct);

    /// <summary>
    /// Create a room. Field set inferred from /rooms + room_state (all confirmed individually):
    /// visibility(public/private), goal(accuracy/score), max(int), chart_filter(object).
    /// </summary>
    public async Task CreateRoomAsync(string visibility, string goal, int max, MpChartFilter? filter = null, CancellationToken ct = default) =>
        await SendActionAsync("create_room", new Dictionary<string, object?>
        {
            ["visibility"] = visibility,
            ["goal"] = goal,
            ["max"] = max,
            ["chart_filter"] = filter ?? new MpChartFilter
            {
                DiffMin = 1.0, DiffMax = 12.9,
                AllowVanilla = true, AllowCustom = true, AllowHidden = true,
                AllowUnrated = true, AllowTouhou = true,
                CustomRatingSource = "charter",
            },
        }, ct);

    /// <summary>time_ping payload field is not captured yet; this helper sends the most likely shape.</summary>
    public async Task SendTimePingAsync(long clientSendMono, CancellationToken ct = default) =>
        await SendActionAsync("time_ping", new Dictionary<string, object?> { ["client_send_mono"] = clientSendMono }, ct);

    public async Task SetProfileAsync(int character, int elfin, int cosmeticCharacter = 33, CancellationToken ct = default) =>
        await SendActionAsync("set_profile", new Dictionary<string, object?>
        {
            ["character"] = character,
            ["elfin"] = elfin,
            ["cosmetic_character"] = cosmeticCharacter,
        }, ct);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > _opt.ReceiveBufferSize)
                {
                    Log("frame exceeded receive buffer; dropped");
                    message.SetLength(0);
                    continue;
                }
                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                RawFrameReceived?.Invoke(json);
                _opt.RawFrameLogPath?.Let(p => AppendLine(p, "IN  " + json));
                HandleFrame(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("receive loop failed: " + ex.Message);
            ErrorReceived?.Invoke("receive_failed", ex.Message);
        }
    }

    private void HandleFrame(string json)
    {
        MpEvent evt;
        int seq = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var (kind, payload) = Classify(root);
            seq = (int?)MpJson.Num(root, "v") ?? 0;
            evt = new MpEvent { Kind = kind, Payload = payload.Clone(), SnapshotSeq = seq, RawJson = json };
        }
        catch (Exception ex)
        {
            Log("frame is not JSON: " + ex.Message);
            return;
        }

        EventReceived?.Invoke(evt);
        switch (evt.Kind)
        {
            case "auth_ok":
                ServerProtocol = (int?)MpJson.Num(evt.Payload, "server_protocol", "protocol") ?? ServerProtocol;
                ServerBuild = MpJson.Str(evt.Payload, "build") ?? ServerBuild;
                PlayerLimits = ((int)(MpJson.Num(evt.Payload, "players_min") ?? 0), (int)(MpJson.Num(evt.Payload, "players_max") ?? 0));
                Log($"auth ok: protocol={ServerProtocol} build={ServerBuild} players={PlayerLimits.min}..{PlayerLimits.max}");
                break;
            case "server_protocol":
                ServerProtocol = (int?)MpJson.Num(evt.Payload, "protocol", "server_protocol") ?? ServerProtocol;
                break;
            case "room_state":
                Room = MpRoomState.Parse(evt.Payload, seq, json);
                RoomChanged?.Invoke(Room);
                Log($"room_state v={seq}: status={Room.Status} members={Room.Members.Count} observers={Room.Observers} " +
                    $"round={(Room.Round != null ? Room.Round.Seq.ToString() : "-")} last={(Room.LastRound != null ? Room.LastRound.Seq.ToString() : "-")}");
                break;
            case "round_record":
                var record = MpRoundRecord.Parse(evt.Payload);
                RoundRecordReceived?.Invoke(record);
                Log($"round_record seq={record.Seq} entries={record.Entries.Count} aborted={record.Aborted}");
                break;
            case "blacklist":
                var rev = (int)(MpJson.Num(evt.Payload, "rev") ?? 0);
                var count = 0;
                if (MpJson.TryGet(evt.Payload, out var entries, "entries") && entries.ValueKind == JsonValueKind.Array)
                    count = entries.GetArrayLength();
                BlacklistReceived?.Invoke(rev, count);
                Log($"blacklist rev={rev} entries={count}");
                break;
            case "relations":
            case "follows":
                Log($"{evt.Kind}: {Truncate(json, 160)}");
                break;
            case "error":
                var code = MpJson.Str(evt.Payload, "code", "error") ?? "unknown";
                var message = MpJson.Str(evt.Payload, "message") ?? "";
                Log($"server error: {code} {message} {Truncate(evt.RawJson, 240)}");
                ErrorReceived?.Invoke(code, message.Length > 0 ? message : json);
                break;
            default:
                Log($"{evt.Kind}: {Truncate(json, 160)}");
                break;
        }
    }

    private static (string kind, JsonElement payload) Classify(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            // Confirmed wire envelope: {"t":"room_state","v":<snapshot seq>,"d":{...}}
            var tag = MpJson.Str(root, "t");
            if (tag != null)
            {
                var payload = MpJson.TryGet(root, out var dEl, "d") ? dEl : root;
                return (tag, payload);
            }
            return ("unknown", root);
        }
        return ("unknown", root);
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_ws is { State: WebSocketState.Open })
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ws?.Dispose();
        _http.Dispose();
        _cts?.Dispose();
        _receiveLoop = null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void AppendLine(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // logging must never break the client
        }
    }
}

internal static class StringPathExtensions
{
    public static void Let(this string? value, Action<string> action)
    {
        if (value != null) action(value);
    }
}
