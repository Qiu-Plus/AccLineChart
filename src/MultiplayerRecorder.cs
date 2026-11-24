using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using EuterpeMp;

namespace AccLineChart;

/// <summary>
/// Builds multiplayer player series (nickname / avatar / per-player accuracy) from the
/// read-only frames captured by <see cref="MultiplayerTap"/>.
///
/// Data sources (already verified wire model):
///   * room_state.d.members[]  -> uid, nick, avatar, roboko (roster, colors, avatars)
///   * room_state.d.members[].sync_state -> live in-round accuracy when the build provides it
///   * round_record.d.entries[] -> final per-player acc/score/fc (slot indexes round entrants)
///
/// If only the final round record is available the series is marked <see cref="PlayerSeries.IsApproximate"/>
/// and the chart draws a dashed straight line from 100% to the final value instead of pretending to have a
/// real curve.
/// </summary>
internal static class MultiplayerRecorder
{
    private static readonly object Gate = new();
    private static readonly List<PlayerSeries> _series = new();
    private static readonly Dictionary<int, PlayerSeries> _byUid = new();
    private static readonly Dictionary<int, MpMember> _members = new();
    private static readonly Dictionary<int, int> _liveSamples = new();
    private static readonly HashSet<int> _loggedSync = new();
    private static readonly Dictionary<int, Texture2D> _avatarTextures = new();
    private static readonly Dictionary<int, byte[]> _pendingAvatarBytes = new();
    private static readonly HashSet<int> _avatarRequested = new();
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim AvatarGate = new(3, 3);
    private static int _avatarFailures;

    // Website-style spectator connection: gives live per-player score/accuracy frames.
    private static readonly ConcurrentQueue<MpLiveScore[]> _liveQueue = new();
    private static MpSpectator? _spectator;
    private static string? _spectatorRoom;
    private static string? _spectatorError;

    private static int _selfUid;
    private static string _selfNick = "";
    private static int _roundSeq = -1;
    private static List<int> _entrants = new();
    private static float _lastSongTime;
    private static float _lastLiveTick;
    private static bool _roundActive;
    private static string? _roomId;
    private static readonly Dictionary<int, long> _lastTickByUid = new();
    private static int _liveRejects;

    internal static string? LastRoomId { get; private set; }
    internal static int SelfUid => _selfUid;
    internal static string SelfNick { get; private set; } = "";
    internal static string SelfAvatarUrl { get; private set; } = "";
    internal static bool PendingAutoShow { get; set; }
    internal static double? SelfOfficialAcc { get; private set; }
    internal static bool HasRoundData
    {
        get { lock (Gate) return _series.Any(s => s.Samples.Count > 0); }
    }

    internal static bool SpectatorActive => _spectator != null;
    internal static string? SpectatorError => _spectatorError;

    internal static IReadOnlyList<PlayerSeries> Series
    {
        get { lock (Gate) return _series.ToArray(); }
    }

    internal static void Initialize()
    {
        try
        {
            if (EuterpeSession.TryGetAccount(out var uid, out var nick))
            {
                _selfUid = uid;
                _selfNick = nick;
            }
        }
        catch
        {
            // identity is only used to avoid drawing our own line twice
        }

        MultiplayerTap.RoomChanged += OnRoomChanged;
        MultiplayerTap.RoundFinished += OnRoundFinished;
        MultiplayerTap.LiveScores += scores => _liveQueue.Enqueue(scores);
        Logger.Msg($"[mp] recorder ready (self uid={_selfUid} nick='{_selfNick}')");
    }

    internal static void Reset()
    {
        try { _spectator?.DisposeAsync().AsTask().Wait(300); } catch { }
        _spectator = null;
        _spectatorRoom = null;
        while (_liveQueue.TryDequeue(out _)) { }
        lock (Gate)
        {
            _series.Clear();
            _byUid.Clear();
            _liveSamples.Clear();
            _roundSeq = -1;
            _entrants = new List<int>();
        }
    }

    /// <summary>Called from Main.OnUpdate (Unity main thread).</summary>
    internal static void Poll()
    {
        try
        {
            if (RunRecorder.InGame)
            {
                _lastSongTime = RunRecorder.SongTime;
            }
        }
        catch
        {
            // ignore
        }

        PromoteAvatars();
        while (_liveQueue.TryDequeue(out var scores))
        {
            ApplyLiveScores(scores);
        }
    }

    private static void StartSpectator(string roomId)
    {
        if (!SettingManager.MultiplayerLive) return;
        if (_spectator != null)
        {
            try { _spectator.DisposeAsync().AsTask().Wait(500); } catch { }
            _spectator = null;
        }

        _spectatorRoom = roomId;
        _spectatorError = null;
        var spec = new MpSpectator(new MpSpectator.Options
        {
            RoomRef = roomId,
            Log = msg => Logger.Msg("[live] " + msg),
        });
        _spectator = spec;
        spec.LiveScores += scores => _liveQueue.Enqueue(scores);
        spec.Error += code =>
        {
            _spectatorError = code;
            Logger.Warning("[live] spectator error " + code);
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await spec.ConnectAsync().ConfigureAwait(false);
                Logger.Msg("[live] spectator connected for room " + roomId);
            }
            catch (Exception ex)
            {
                _spectatorError = ex.GetType().Name;
                Logger.Warning("[live] spectator failed: " + ex.Message);
            }
        });
    }

    private static void ApplyLiveScores(MpLiveScore[] scores)
    {
        try
        {
            lock (Gate)
            {
                if (!_roundActive) return;
                foreach (var score in scores)
                {
                    if (score.Uid == 0 || score.Uid == _selfUid) continue;
                    if (score.ChartTickMs < 0 || score.ChartTickMs > 3_600_000) continue;
                    if (_members.Count > 0 && !_members.ContainsKey(score.Uid))
                    {
                        if (Interlocked.Increment(ref _liveRejects) <= 5)
                            Logger.Warning($"[mp] live row rejected: unknown uid={score.Uid} tick={score.ChartTickMs} acc={score.AccuracyPercent:0.00}");
                        continue;
                    }

                    var series = GetOrCreateSeriesLocked(score.Uid);
                    if (_lastTickByUid.TryGetValue(score.Uid, out var previousTick) &&
                        score.ChartTickMs + 2000 < previousTick)
                    {
                        // Tick went backwards: a new round started and we missed the room_state reset.
                        series.Samples.Clear();
                        series.IsApproximate = false;
                    }

                    _lastTickByUid[score.Uid] = score.ChartTickMs;
                    var time = (float)(score.ChartTickMs / 1000.0);
                    if (time <= 0f) continue;
                    // Per-player monotonic check only: rows in one packet share the same tick.
                    if (series.Samples.Count > 0 && time <= series.Samples[^1].Time + 0.02f) continue;
                    var acc = (float)Math.Clamp(score.AccuracyPercent, 0.0, 100.0);
                    if (series.Samples.Count == 0 && time > 0.5f)
                        series.Samples.Add(new RunSample { Time = 0f, Acc = 100f });
                    series.Samples.Add(new RunSample { Time = time, Acc = acc });
                    series.IsApproximate = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("[live] apply failed: " + ex.Message);
        }
    }

    private static void OnRoomChanged(MpRoomState room)
    {
        try
        {
            var newAvatars = new List<int>();
            lock (Gate)
            {
                if (!string.IsNullOrEmpty(room.RoomId) && room.RoomId != _roomId)
                {
                    if (_roomId != null)
                    {
                        Logger.Msg($"[mp] room changed {_roomId} -> {room.RoomId}, clearing series");
                        _series.Clear();
                        _byUid.Clear();
                        _liveSamples.Clear();
                        _lastTickByUid.Clear();
                        while (_liveQueue.TryDequeue(out _)) { }
                    }

                    _roomId = room.RoomId;
                }

                LastRoomId = room.RoomId;
                foreach (var m in room.Members)
                {
                    if (m.Uid == 0) continue;
                    _members[m.Uid] = m;
                    if (_selfUid == 0 && !string.IsNullOrEmpty(_selfNick) &&
                        string.Equals(m.Nick, _selfNick, StringComparison.Ordinal))
                    {
                        _selfUid = m.Uid;
                    }

                    if (m.Uid == _selfUid)
                    {
                        SelfNick = m.Nick;
                        SelfAvatarUrl = m.AvatarUrl;
                    }

                    if (m.SyncState is { } sync && sync.ValueKind != JsonValueKind.Null)
                    {
                        var raw = Truncate(sync.GetRawText(), 240);
                        if (_loggedSync.Add(m.Uid) || (m.Uid == _selfUid && raw != "null"))
                            Logger.Msg($"[mp] sync_state uid={m.Uid} raw={raw}");
                    }
                }

                if (room.Round is { } round)
                {
                    if (round.Seq != _roundSeq)
                    {
                        _roundSeq = round.Seq;
                        _entrants = new List<int>(round.Entrants);
                        _lastLiveTick = 0f;
                        RebuildSeriesLocked();
                        Logger.Msg($"[mp] round seq={round.Seq} entrants=[{string.Join(",", _entrants)}] chart={round.Chart.VanillaUid}");
                    }

                    _roundActive = room.Status == "battle" || room.Status == "prep";

                    // Live accuracy from the server's 31-byte binary score frames is applied in
                    // ApplyLiveScores; sync_state remains a fallback for older builds.
                    //
                    foreach (var m in room.Members)
                    {
                        if (m.SyncState is not { } sync) continue;
                        if (!TryReadLiveAccuracy(sync, out var acc)) continue;
                        AppendLiveLocked(m.Uid, acc);
                    }
                }

                newAvatars.AddRange(_members.Keys);
            }

            foreach (var uid in newAvatars) RequestAvatar(uid);

            if (!string.IsNullOrEmpty(room.RoomId) && room.RoomId != _spectatorRoom)
            {
                StartSpectator(room.RoomId);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[mp] room handler failed: {ex.Message}");
        }
    }

    private static void OnRoundFinished(MpRoundRecord record)
    {
        try
        {
            lock (Gate)
            {
                _roundActive = false;
                foreach (var member in record.Roster)
                {
                    if (member.Uid != 0) _members[member.Uid] = member;
                }

                var frames = TryDecodeFrames(record.Frames, record.Roster);
                if (_selfUid != 0 && frames.TryGetValue(_selfUid, out var selfResult))
                {
                    SelfOfficialAcc = selfResult.Acc;
                    var selfSeries = RunRecorder.Self;
                    if (selfSeries != null)
                    {
                        var local = selfSeries.FinalAcc;
                        selfSeries.ServerFinalAcc = (float)selfResult.Acc;
                        Logger.Msg($"[mp] server self result: score={selfResult.Score} acc={selfResult.Acc:0.00}% " +
                                   $"(local last={local:0.00}, diff={selfResult.Acc - local:+0.00;-0.00})");
                    }
                }

                var startEntrants = record.Roster.Count > 0
                    ? record.Roster.Select(m => m.Uid).ToList()
                    : _entrants;

                for (var i = 0; i < startEntrants.Count; i++)
                {
                    var uid = startEntrants[i];
                    if (uid == 0 || uid == _selfUid) continue;
                    var series = GetOrCreateSeriesLocked(uid);
                    if (!frames.TryGetValue(uid, out var result))
                    {
                        Logger.Msg($"[mp] round_record roster has uid={uid} but frames decode missed it");
                        continue;
                    }

                    series.ServerFinalAcc = (float)Math.Clamp(result.Acc, 0.0, 100.0);
                    if (series.Samples.Count == 0)
                    {
                        // No live data at all for this player: dashed approximation from 100% to the result.
                        series.IsApproximate = true;
                        series.Samples.Add(new RunSample { Time = 0f, Acc = 100f });
                        var fallbackEnd = Mathf.Max(_lastSongTime, 1f);
                        series.Samples.Add(new RunSample { Time = fallbackEnd, Acc = (float)Math.Clamp(result.Acc, 0.0, 100.0) });
                        continue;
                    }

                    // Live curve exists: pin its end to the server's official accuracy at the last
                    // received chart tick (no horizontal extrapolation after the round ends).
                    series.IsApproximate = false;
                    var endTime = series.Samples[^1].Time;
                    if (endTime <= 0.01f) endTime = Mathf.Max(_lastSongTime, 1f);
                    if (Math.Abs(series.Samples[^1].Acc - (float)result.Acc) > 0.01f)
                        series.Samples.Add(new RunSample { Time = endTime + 0.05f, Acc = (float)Math.Clamp(result.Acc, 0.0, 100.0) });
                    Logger.Msg($"[mp] result uid={uid} nick={series.Name} score={result.Score} acc={result.Acc:0.00}% time={endTime:0.0}s");
                }

                PendingAutoShow = true;
                Logger.Msg($"[mp] round_record seq={record.Seq} roster={record.Roster.Count} decoded={frames.Count} " +
                           $"winners=[{string.Join(",", record.Winners)}] chart={record.Chart?.VanillaUid}");
            }

            foreach (var member in record.Roster)
            {
                if (member.Uid != 0) RequestAvatar(member.Uid);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[mp] round handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode the round_record.frames payload. Live sample (2026-11-24) shows a Rust-packed
    /// per-player record: uid(u64) + 0(u32) + ?(u32) + score(u32) + accuracy(f32, 0..1) + counts.
    /// We locate each roster uid and read score at +12 / accuracy at +16, validating the float range.
    /// </summary>
    private static Dictionary<int, (double Acc, long Score)> TryDecodeFrames(string? base64, IReadOnlyList<MpMember> roster)
    {
        var result = new Dictionary<int, (double, long)>();
        if (string.IsNullOrWhiteSpace(base64) || roster.Count == 0) return result;

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch
        {
            return result;
        }

        foreach (var member in roster)
        {
            var uid = member.Uid;
            if (uid == 0) continue;

            var offset = FindPattern(data, BitConverter.GetBytes((ulong)(uint)uid));
            if (offset < 0) offset = FindPattern(data, BitConverter.GetBytes((uint)uid));
            if (offset < 0 || offset + 20 > data.Length) continue;

            var score = BitConverter.ToUInt32(data, offset + 12);
            var acc = BitConverter.ToSingle(data, offset + 16);
            if (float.IsNaN(acc) || acc <= 0f || acc > 1.001f) continue;
            result[uid] = (acc * 100.0, score);
        }

        return result;
    }

    private static int FindPattern(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return i;
        }

        return -1;
    }

    private static void RebuildSeriesLocked()
    {
        _series.Clear();
        _byUid.Clear();
        _liveSamples.Clear();
        _lastTickByUid.Clear();
        while (_liveQueue.TryDequeue(out _)) { }
        foreach (var uid in _entrants)
        {
            if (uid == 0 || uid == _selfUid) continue;
            GetOrCreateSeriesLocked(uid);
        }
    }

    private static PlayerSeries GetOrCreateSeriesLocked(int uid)
    {
        if (_byUid.TryGetValue(uid, out var series)) return series;

        _members.TryGetValue(uid, out var member);
        series = new PlayerSeries
        {
            Uid = uid,
            Name = string.IsNullOrEmpty(member?.Nick) ? $"玩家{uid}" : member!.Nick,
            Color = ColorForUid(uid),
            IsSelf = false,
        };
        _byUid[uid] = series;
        _series.Add(series);
        return series;
    }

    private static void AppendLiveLocked(int uid, double acc)
    {
        if (uid == 0 || uid == _selfUid) return;
        var series = GetOrCreateSeriesLocked(uid);
        var time = RunRecorder.InGame ? RunRecorder.SongTime : _lastSongTime;
        if (series.Samples.Count > 0)
        {
            var last = series.Samples[^1];
            if (time <= last.Time + 0.10f) return;
        }

        var accPct = Math.Clamp(acc <= 1.5 ? acc * 100.0 : acc, 0.0, 100.0);
        if (series.Samples.Count == 0 && time > 0.5f)
            series.Samples.Add(new RunSample { Time = 0f, Acc = 100f });

        series.Samples.Add(new RunSample { Time = time, Acc = (float)accPct });
        series.IsApproximate = false;
        _liveSamples[uid] = _liveSamples.TryGetValue(uid, out var n) ? n + 1 : 1;
    }

    /// <summary>Extract an accuracy percentage from a sync_state blob, only when it is explicit.</summary>
    private static bool TryReadLiveAccuracy(JsonElement sync, out double acc)
    {
        acc = 0;
        if (sync.ValueKind != JsonValueKind.Object) return false;
        if (MpJson.Num(sync, "acc", "accuracy", "acc_percent", "accuracy_percent", "acc100") is not { } value)
            return false;
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        acc = value;
        return true;
    }

    private static Color ColorForUid(int uid)
    {
        // Stable, well separated hues; self keeps the user's configured color.
        var hue = (uid * 0.6180339887f) % 1f;
        return Color.HSVToRGB(hue, 0.62f, 1f);
    }

    internal static Texture2D? GetAvatar(int uid)
    {
        lock (Gate)
        {
            if (_avatarTextures.TryGetValue(uid, out var tex)) return tex;
        }

        RequestAvatar(uid);
        return null;
    }

    private static void RequestAvatar(int uid)
    {
        string url;
        lock (Gate)
        {
            if (uid == 0 || _avatarRequested.Contains(uid)) return;
            if (!_members.TryGetValue(uid, out var member) || string.IsNullOrEmpty(member.AvatarUrl)) return;
            _avatarRequested.Add(uid);
            url = member.AvatarUrl;
        }

        _ = Task.Run(async () =>
        {
            await AvatarGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("HTTP " + (int)resp.StatusCode);
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return;
                lock (Gate) _pendingAvatarBytes[uid] = bytes;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _avatarFailures) <= 6)
                    Logger.Warning($"[mp] avatar download failed uid={uid} url={url}: {ex.Message}");
                lock (Gate) _avatarRequested.Remove(uid); // allow a later retry
            }
            finally
            {
                AvatarGate.Release();
            }
        });
    }

    private static void PromoteAvatars()
    {
        List<KeyValuePair<int, byte[]>>? ready = null;
        lock (Gate)
        {
            if (_pendingAvatarBytes.Count > 0)
            {
                ready = _pendingAvatarBytes.ToList();
                _pendingAvatarBytes.Clear();
            }
        }

        if (ready == null) return;
        foreach (var pair in ready)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var bytes = NormalizeAvatarBytes(pair.Value);
                if (bytes == null || !tex.LoadImage(bytes))
                {
                    Logger.Warning($"[mp] avatar for uid={pair.Key} could not be decoded ({pair.Value.Length} bytes)");
                    continue;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                lock (Gate) _avatarTextures[pair.Key] = tex;
                if (!_avatarLogged)
                {
                    _avatarLogged = true;
                    Logger.Msg($"[mp] avatar decoded: uid={pair.Key} {tex.width}x{tex.height}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[mp] avatar decode failed: {ex.Message}");
            }
        }
    }

    private static bool _avatarLogged;

    /// <summary>
    /// Euterpe avatars are WebP; Unity's Texture2D.LoadImage does not decode WebP.
    /// Decode WebP to PNG through the SkiaSharp assembly that MelonLoader already loads
    /// from UserLibs (reflection, so AccLineChart has no hard dependency on it).
    /// </summary>
    private static byte[]? NormalizeAvatarBytes(byte[] bytes)
    {
        if (bytes.Length < 12 ||
            bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
            bytes[8] != (byte)'W' || bytes[9] != (byte)'E' || bytes[10] != (byte)'B' || bytes[11] != (byte)'P')
        {
            return bytes;
        }

        var png = WebpToPng(bytes);
        if (png != null) return png;
        return null;
    }

    private static Type? _skBitmapType, _skImageType, _skDataType, _skFormatType;
    private static MethodInfo? _skDecode, _skFromBitmap, _skEncode, _skToArray;
    private static bool _skiaProbed, _skiaAvailable;

    private static byte[]? WebpToPng(byte[] webp)
    {
        if (!EnsureSkia()) return null;
        object? bitmap = null, image = null, data = null;
        try
        {
            bitmap = _skDecode!.Invoke(null, new object[] { webp });
            if (bitmap == null) return null;
            image = _skFromBitmap!.Invoke(null, new[] { bitmap });
            if (image == null) return null;
            var format = Enum.Parse(_skFormatType!, "Png");
            data = _skEncode!.Invoke(image, new[] { format, (object)100 });
            if (data == null) return null;
            return _skToArray!.Invoke(data, null) as byte[];
        }
        catch (Exception ex)
        {
            Logger.Warning($"[mp] webp decode failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            (data as IDisposable)?.Dispose();
            (image as IDisposable)?.Dispose();
            (bitmap as IDisposable)?.Dispose();
        }
    }

    private static bool EnsureSkia()
    {
        lock (Gate)
        {
            if (_skiaProbed) return _skiaAvailable;
            _skiaProbed = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SkiaSharp");
                if (asm == null)
                {
                    var path = Path.Combine(MelonEnvironment.GameRootDirectory, "UserLibs", "SkiaSharp.dll");
                    if (File.Exists(path)) asm = Assembly.LoadFrom(path);
                }

                if (asm == null) return false;
                _skBitmapType = asm.GetType("SkiaSharp.SKBitmap");
                _skImageType = asm.GetType("SkiaSharp.SKImage");
                _skDataType = asm.GetType("SkiaSharp.SKData");
                _skFormatType = asm.GetType("SkiaSharp.SKEncodedImageFormat");
                if (_skBitmapType == null || _skImageType == null || _skDataType == null || _skFormatType == null)
                    return false;

                _skDecode = _skBitmapType.GetMethod("Decode", new[] { typeof(byte[]) });
                _skFromBitmap = _skImageType.GetMethod("FromBitmap", new[] { _skBitmapType });
                _skEncode = _skImageType.GetMethod("Encode", new[] { _skFormatType, typeof(int) });
                _skToArray = _skDataType.GetMethod("ToArray", Type.EmptyTypes);
                _skiaAvailable = _skDecode != null && _skFromBitmap != null && _skEncode != null && _skToArray != null;
                if (_skiaAvailable) Logger.Msg("[mp] SkiaSharp webp decoder ready");
                return _skiaAvailable;
            }
            catch (Exception ex)
            {
                Logger.Warning("[mp] SkiaSharp not available: " + ex.Message);
                return false;
            }
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AccLineChart/1.1");
        return http;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
