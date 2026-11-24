using System.Collections.Concurrent;
using System.Text.Json;
using EuterpeMp;

namespace EuterpeMpTap;

public sealed class MpTapOptions
{
    /// <summary>Optional debug file: every received frame is appended as one JSON line.</summary>
    public string? DumpPath;
    public Action<string>? Log;
    /// <summary>Also keep relations/blacklist/round_history payloads (raw clones).</summary>
    public bool KeepRelationPayloads = true;
    /// <summary>Log outgoing WS frames and WinHTTP requests (diagnostics; noisy).</summary>
    public bool NetworkDiagnostics;
}

/// <summary>
/// Parses the tapped Euterpe WebSocket frames into the already-verified wire model
/// (MpRoomState / MpRoundRecord) and delivers events on the Unity main thread.
///
/// No network, no session.dat, no extra connection: the frames come from the game's own
/// Euterpe client through <see cref="WinHttpWsTap"/>.
/// </summary>
public sealed class MpLiveFeed : IDisposable
{
    private readonly MpTapOptions _options;
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly List<MpRoundRecord> _history = new();
    private bool _started;
    private bool _disposed;

    public MpRoomState? LastRoom { get; private set; }
    public IReadOnlyList<MpRoundRecord> Rounds => _history;
    public int ServerProtocol { get; private set; }
    public string? ServerBuild { get; private set; }
    public (int min, int max) PlayerLimits { get; private set; }
    public JsonElement? Relations { get; private set; }
    public JsonElement? Blacklist { get; private set; }
    public string? LastError { get; private set; }

    public event Action<MpRoomState>? RoomChanged;
    public event Action<MpRoundRecord>? RoundFinished;
    public event Action<JsonElement>? RoundHistoryReceived;
    public event Action<JsonElement>? RelationsReceived;
    public event Action<int, int>? BlacklistReceived;
    public event Action<string>? LogLine;
    /// <summary>Live 31-byte score records received on the game's own WS connection.</summary>
    public event Action<MpLiveScore[]>? LiveScores;

    private MpLiveFeed(MpTapOptions options) => _options = options;

    public static MpLiveFeed? Start(MpTapOptions? options = null)
    {
        var feed = new MpLiveFeed(options ?? new MpTapOptions());
        feed.Log("starting in-process Euterpe tap (WinHTTP receive hook)");
        WinHttpWsTap.Log += feed.Log;
        WinHttpWsTap.TextMessageReceived += feed.OnTextMessage;
        WinHttpWsTap.BinaryMessageReceived += feed.OnBinaryMessage;
        if (options?.NetworkDiagnostics == true)
        {
            WinHttpWsTap.NetworkDiagnostics = true;
            WinHttpWsTap.OutgoingText += text => feed.Log("[out] " + Truncate(text, 600));
            WinHttpWsTap.HttpRequest += text => feed.Log("[http] " + Truncate(text, 400));
            WinHttpWsTap.HttpRead += text => feed.Log("[http] " + text);
        }
        if (!WinHttpWsTap.Start())
        {
            feed.Log("tap could not start: " + (WinHttpWsTap.LastError ?? "unknown"));
            return null;
        }
        feed._started = true;
        return feed;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Log(string message)
    {
        _options.Log?.Invoke(message);
        LogLine?.Invoke(message);
    }

    /// <summary>Call once per frame from the Unity main thread (e.g. MelonMod.OnUpdate).</summary>
    public void Pump()
    {
        while (_mainThread.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Log("event handler failed: " + ex.Message); }
        }
    }

    private void OnBinaryMessage(byte[] payload)
    {
        try
        {
            const int recordSize = 31;
            if (payload.Length < recordSize || payload.Length % recordSize != 0 || payload.Length > recordSize * 64)
            {
                if (Interlocked.Increment(ref _foreignBinary) <= 5)
                    Log($"[tap] non-score binary frame ignored: len={payload.Length} head={BitConverter.ToString(payload, 0, Math.Min(8, payload.Length))}");
                return;
            }

            var count = payload.Length / recordSize;
            var scores = new MpLiveScore[count];
            for (var i = 0; i < count; i++)
                scores[i] = MpLiveScore.Parse(payload.AsSpan(i * recordSize, recordSize));
            if (Interlocked.Increment(ref _binaryFrames) <= 10)
                Log($"[tap] binary frame: len={payload.Length} rows={count} first uid={scores[0].Uid} acc={scores[0].AccuracyPercent:0.00}%");
            _mainThread.Enqueue(() => LiveScores?.Invoke(scores));
        }
        catch (Exception ex)
        {
            Log("[tap] binary frame decode failed: " + ex.Message);
        }
    }

    private static int _binaryFrames;
    private static int _foreignBinary;

    private void OnTextMessage(string json)
    {
        if (_disposed) return;
        try
        {
            if (_options.DumpPath != null)
            {
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(_options.DumpPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(_options.DumpPath, json + Environment.NewLine);
                }
                catch { /* dump only */ }
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var kind = MpJson.Str(root, "t");
            if (string.IsNullOrEmpty(kind)) return;
            var seq = (int?)MpJson.Num(root, "v") ?? 0;
            var payload = MpJson.TryGet(root, out var d, "d") ? d : default;
            var payloadClone = payload.ValueKind == JsonValueKind.Undefined ? default : payload.Clone();
            var raw = json;

            switch (kind)
            {
                case "auth_ok":
                    ServerProtocol = (int?)MpJson.Num(payload, "server_protocol", "protocol") ?? 0;
                    ServerBuild = MpJson.Str(payload, "build");
                    PlayerLimits = ((int)(MpJson.Num(payload, "players_min") ?? 0), (int)(MpJson.Num(payload, "players_max") ?? 0));
                    Log($"[tap] auth_ok protocol={ServerProtocol} build={ServerBuild}");
                    break;
                case "server_protocol":
                    ServerProtocol = (int?)MpJson.Num(payload, "protocol", "server_protocol") ?? ServerProtocol;
                    break;
                case "room_state":
                {
                    var room = MpRoomState.Parse(payload, seq, raw);
                    LastRoom = room;
                    _mainThread.Enqueue(() => RoomChanged?.Invoke(room));
                    break;
                }
                case "round_record":
                {
                    var record = MpRoundRecord.Parse(payload);
                    lock (_history) _history.Add(record);
                    _mainThread.Enqueue(() => RoundFinished?.Invoke(record));
                    break;
                }
                case "round_history":
                    if (_options.KeepRelationPayloads && payloadClone.ValueKind != JsonValueKind.Undefined)
                        _mainThread.Enqueue(() => RoundHistoryReceived?.Invoke(payloadClone));
                    break;
                case "relations":
                    if (_options.KeepRelationPayloads && payloadClone.ValueKind != JsonValueKind.Undefined)
                    {
                        Relations = payloadClone;
                        _mainThread.Enqueue(() => RelationsReceived?.Invoke(payloadClone));
                    }
                    break;
                case "blacklist":
                {
                    var rev = (int)(MpJson.Num(payload, "rev") ?? 0);
                    var count = 0;
                    if (MpJson.TryGet(payload, out var entries, "entries") && entries.ValueKind == JsonValueKind.Array)
                        count = entries.GetArrayLength();
                    Blacklist = payloadClone;
                    _mainThread.Enqueue(() => BlacklistReceived?.Invoke(rev, count));
                    break;
                }
                case "error":
                    LastError = MpJson.Str(payload, "code") ?? "unknown";
                    Log($"[tap] server error {LastError} {MpJson.Str(payload, "message")}");
                    break;
                case "rounds":
                case "messages":
                case "chat_history":
                case "follows":
                case "time_pong":
                default:
                    // Kept in the dump file; not needed for the accuracy chart yet.
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("[tap] frame parse failed: " + ex.Message);
        }
    }

    /// <summary>Helper: map a round_record entry slot back to a room member (using the round's entrants order).</summary>
    public MpMember? MemberForSlot(int slot)
    {
        var room = LastRoom;
        if (room == null) return null;
        var entrants = room.Round?.Entrants;
        if (entrants == null || slot < 0 || slot >= entrants.Count) return null;
        return room.Member(entrants[slot]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinHttpWsTap.TextMessageReceived -= OnTextMessage;
        WinHttpWsTap.Log -= Log;
        if (_started) WinHttpWsTap.Stop();
    }
}
