using System.Collections.Concurrent;

namespace EuterpeMp;

/// <summary>One chartable accuracy sample of one player.</summary>
public readonly struct MpAccuracySample
{
    public readonly int Uid;
    public readonly string Nickname;
    public readonly double AccuracyPercent;
    public readonly DateTimeOffset At;
    public readonly string Source; // "round_record" | "sync_state"

    public MpAccuracySample(int uid, string nickname, double acc, DateTimeOffset at, string source)
    {
        Uid = uid;
        Nickname = nickname;
        AccuracyPercent = acc;
        At = at;
        Source = source;
    }
}

public sealed class MpBridgeOptions
{
    public bool Enabled = true;
    public string? GameRoot;
    public string? CacheDirectory;
    /// <summary>Optional room to join right after auth (room_id / 16-hex string).</summary>
    public string? JoinRoomId;
    public string? JoinRoomCode;
    public TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    public Action<string>? Log;
}

/// <summary>
/// AccLineChart-facing facade over the Euterpe multiplayer service.
///
/// Confirmed data paths (protocol 8 / mp8, captured 2026-11-24):
///  - nickname / avatar / rating / character / elfin  -> room_state.d.members[]  (nick, avatar, ...)
///  - own + others' final accuracy for a round        -> round_record event entries {slot, acc, score, fc, fail, total}
///  - live in-round state per member                  -> room_state.d.members[].sync_state / sync_progress / loaded
///
/// IMPORTANT: the Euterpe server keeps one connection per account. A second client
/// logged in with the same account displaces the game's own connection (Euterpe then
/// reconnects). For a production feature either hook the in-game Euterpe connection or
/// observe with a separate (bot) account. See README.
/// </summary>
public sealed class MpBridge : IAsyncDisposable
{
    public static MpBridge? Current { get; private set; }

    private readonly MpBridgeOptions _options;
    private readonly MpOptions _clientOptions;
    private readonly MpAvatarCache _avatars;
    private readonly ConcurrentDictionary<int, List<MpAccuracySample>> _series = new();
    private readonly ConcurrentDictionary<int, byte[]?> _avatarCache = new();
    private readonly ConcurrentDictionary<int, double> _lastAcc = new();
    private MpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private List<int> _currentEntrants = new();

    public string? LastError { get; private set; }
    public bool IsConnected => _client?.IsConnected == true;
    public MpRoomState? Room => _client?.Room;
    public IReadOnlyDictionary<int, List<MpAccuracySample>> Series => _series;
    public (int min, int max) PlayerLimits => _client?.PlayerLimits ?? (0, 0);

    public event Action<MpRoomState>? RoomChanged;
    public event Action<MpAccuracySample>? SampleAdded;
    public event Action<MpRoundRecord>? RoundFinished;
    public event Action<string>? LogLine;

    private MpBridge(MpBridgeOptions options)
    {
        _options = options;
        _clientOptions = new MpOptions { GameRoot = options.GameRoot, Log = msg => Log(msg) };
        var cacheDir = options.CacheDirectory ?? Path.Combine(Path.GetTempPath(), "AccLineChart", "mp-avatars");
        _avatars = new MpAvatarCache(cacheDir);
    }

    public static MpBridge Create(MpBridgeOptions options)
    {
        Current = new MpBridge(options);
        return Current;
    }

    public void Log(string message)
    {
        LogLine?.Invoke(message);
        _options.Log?.Invoke(message);
    }

    public void StartInBackground()
    {
        if (!_options.Enabled || _pump != null) return;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_client != null) await _client.DisposeAsync().ConfigureAwait(false);
                _client = new MpClient(_clientOptions);
                _client.RoomChanged += OnRoom;
                _client.RoundRecordReceived += OnRoundRecord;
                _client.ErrorReceived += (code, msg) => { LastError = code; Log("[mp] error " + code + " " + msg); };
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_options.JoinRoomId))
                    await _client.JoinRoomByIdAsync(_options.JoinRoomId!, ct).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(_options.JoinRoomCode))
                    await _client.JoinRoomByCodeAsync(_options.JoinRoomCode!, ct).ConfigureAwait(false);

                LastError = null;
                Log("[mp] connected: " + (_client.WebSocketUrl ?? "?"));
                await SampleLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (MpAuthExpiredException ex)
            {
                LastError = ex.Message;
                Log("[mp] " + ex.Message + " — retry in 60s");
                await DelayAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                Log("[mp] " + LastError + " — retry in 20s");
                await DelayAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private void OnRoom(MpRoomState room)
    {
        if (room.Round is { Entrants.Count: > 0 }) _currentEntrants = room.Round.Entrants;
        RoomChanged?.Invoke(room);
        // Best-effort live accuracy from the per-member sync blob (shape may vary by client build).
        foreach (var m in room.Members)
        {
            if (m.SyncState is not { } sync) continue;
            var acc = MpJson.Num(sync, "acc", "accuracy", "acc_percent", "accuracy_percent");
            if (acc is { } a)
                Emit(m, a <= 1.5 ? a * 100.0 : a, "sync_state");
        }
    }

    private void OnRoundRecord(MpRoundRecord record)
    {
        RoundFinished?.Invoke(record);
        var room = Room;
        for (var slot = 0; slot < record.Entries.Count; slot++)
        {
            var entry = record.Entries[slot];
            var useSlot = entry.Slot ?? slot;
            var uid = useSlot >= 0 && useSlot < _currentEntrants.Count ? _currentEntrants[useSlot] : 0;
            if (uid == 0) continue;
            if (entry.Acc is not { } acc) continue;
            var member = room?.Member(uid);
            Emit(new MpMember { Uid = uid, Nick = member?.Nick ?? "" }, acc, "round_record");
        }
    }

    private void Emit(MpMember member, double acc, string source)
    {
        var uid = member.Uid;
        if (uid == 0) return;
        if (_lastAcc.TryGetValue(uid, out var prev) && Math.Abs(prev - acc) < 0.0001 && source == "sync_state") return;
        _lastAcc[uid] = acc;
        var list = _series.GetOrAdd(uid, _ => new List<MpAccuracySample>());
        var sample = new MpAccuracySample(uid, member.Nick, acc, DateTimeOffset.UtcNow, source);
        lock (list) list.Add(sample);
        SampleAdded?.Invoke(sample);
        Log($"[mp] acc {member.Nick}({uid}) = {acc:0.00}% ({source})");
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                // Room snapshots are pushed by the server; nothing to do here.
            }
            catch (Exception ex)
            {
                Log("[mp] sample: " + ex.Message);
            }
            await DelayAsync(_options.SampleInterval, ct).ConfigureAwait(false);
        }
    }

    public byte[]? GetAvatarBytes(int uid)
    {
        if (_avatarCache.TryGetValue(uid, out var cached)) return cached;
        var member = Room?.Member(uid);
        if (member == null || string.IsNullOrEmpty(member.AvatarUrl)) return null;
        var bytes = _avatars.GetAsync(member.AvatarUrl).GetAwaiter().GetResult();
        _avatarCache[uid] = bytes;
        return bytes;
    }

    public byte[]? LoadAvatar(int uid, string? avatarUrl)
    {
        if (string.IsNullOrEmpty(avatarUrl)) return null;
        var bytes = _avatars.GetAsync(avatarUrl).GetAwaiter().GetResult();
        if (bytes != null) _avatarCache[uid] = bytes;
        return bytes;
    }

    public IReadOnlyList<MpAccuracySample> GetSeries(int uid) =>
        _series.TryGetValue(uid, out var list) ? (IReadOnlyList<MpAccuracySample>)list.ToArray() : Array.Empty<MpAccuracySample>();

    public void ClearSeries()
    {
        _series.Clear();
        _lastAcc.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_client != null) await _client.DisposeAsync().ConfigureAwait(false);
        _avatars.Dispose();
        _cts?.Dispose();
        _pump = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
