using EuterpeMp;
using EuterpeMpTap;

namespace AccLineChart;

/// <summary>
/// Read-only multiplayer data source for AccLineChart.
///
/// It taps the frames that the game's own Euterpe client receives (WinHTTP WebSocket
/// receive hook) — no extra connection, no login, nothing is sent to the server, so the
/// in-game multiplayer session is untouched (no disconnect/reconnect popup).
///
/// Data available here:
///   * room roster: nickname, avatar URL, Roboko rating, character/elfin  (room_state)
///   * per-round results: slot / acc / score / fc / fail / total          (round_record)
///   * round history snapshots                                            (round_history)
/// </summary>
internal static class MultiplayerTap
{
    private static MpLiveFeed? _feed;
    private static string? _dumpPath;

    internal static bool Active => _feed != null;
    internal static MpRoomState? CurrentRoom => _feed?.LastRoom;
    internal static MpRoomState? Room => _feed?.LastRoom;
    internal static IReadOnlyList<MpRoundRecord> Rounds => _feed?.Rounds ?? Array.Empty<MpRoundRecord>();
    internal static int ServerProtocol => _feed?.ServerProtocol ?? 0;
    internal static string? Build => _feed?.ServerBuild;
    internal static string? LastError => _feed?.LastError ?? WinHttpWsTap.LastError;
    internal static string? DumpPath => _dumpPath;

    /// <summary>Raised on the Unity main thread for every room snapshot (members include nick/avatar).</summary>
    internal static event Action<MpRoomState>? RoomChanged;
    /// <summary>Raised on the Unity main thread when a round finishes with per-player results.</summary>
    internal static event Action<MpRoundRecord>? RoundFinished;
    /// <summary>Live per-player accuracy/score rows from the game's own WS connection.</summary>
    internal static event Action<EuterpeMp.MpLiveScore[]>? LiveScores;
    /// <summary>Raised when the game itself leaves the multiplayer room (outgoing leave_room action).</summary>
    internal static event Action? RoomLeft;

    /// <summary>Nickname/avatar/rating of a round_record slot (slot indexes room_state.round.entrants).</summary>
    internal static MpMember? MemberForSlot(int slot) => _feed?.MemberForSlot(slot);

    internal static void Start()
    {
        if (!SettingManager.MultiplayerTap)
        {
            Logger.Msg("[mp] tap disabled by config");
            return;
        }

        try
        {
            _dumpPath = Path.Combine(MelonEnvironment.UserDataDirectory, "AccLineChart_mp_tap.jsonl");
            _feed = MpLiveFeed.Start(new MpTapOptions
            {
                DumpPath = _dumpPath,
                NetworkDiagnostics = SettingManager.MpNetworkDiagnostics,
                Log = msg =>
                {
                    if (SettingManager.DebugLog || msg.Contains("[tap]") || msg.Contains("error"))
                        Logger.Msg("[mp] " + msg);
                },
            });

            if (_feed == null)
            {
                Logger.Warning("[mp] in-process tap could not start: " + WinHttpWsTap.LastError);
                return;
            }

            _feed.RoomChanged += OnRoomChanged;
            _feed.RoundFinished += OnRoundFinished;
            _feed.LiveScores += scores => LiveScores?.Invoke(scores);
            EuterpeMpTap.WinHttpWsTap.OutgoingText += OnOutgoingText;
            _feed.RoundHistoryReceived += _ => Logger.Msg("[mp] round_history received");
            Logger.Msg($"[mp] tap active, frames -> {_dumpPath}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[mp] tap start failed: {ex}");
        }
    }

    private static void OnOutgoingText(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (!root.TryGetProperty("t", out var kind) || kind.ValueKind != System.Text.Json.JsonValueKind.String) return;
            switch (kind.GetString())
            {
                case "leave_room":
                case "leave":
                    Logger.Msg("[mp] room left (game sent leave_room)");
                    RoomLeft?.Invoke();
                    break;
            }
        }
        catch
        {
            // not JSON / not an action envelope: ignore
        }
    }

    private static void OnRoomChanged(MpRoomState room)
    {
        try
        {
            Logger.Msg($"[mp] room {room.RoomId} status={room.Status} goal={room.Goal} " +
                       $"members={room.Members.Count} observers={room.Observers}");
            foreach (var m in room.Members)
                Logger.Msg($"[mp]   member uid={m.Uid} nick={m.Nick} avatar={m.AvatarUrl} " +
                           $"roboko={m.Roboko?.ToString("0.000")} state={m.State} loaded={m.Loaded}");

            RoomChanged?.Invoke(room);
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
            Logger.Msg($"[mp] round_record seq={record.Seq} aborted={record.Aborted} entries={record.Entries.Count}");
            for (var i = 0; i < record.Entries.Count; i++)
            {
                var entry = record.Entries[i];
                var member = MemberForSlot(entry.Slot ?? i);
                Logger.Msg($"[mp]   slot={entry.Slot ?? i} acc={entry.Acc?.ToString("0.00")} " +
                           $"score={entry.Score} fc={entry.FullCombo} fail={entry.Failed} " +
                           $"nick={member?.Nick ?? "?"}");
            }

            RoundFinished?.Invoke(record);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[mp] round handler failed: {ex.Message}");
        }
    }

    internal static void Pump()
    {
        try { _feed?.Pump(); }
        catch (Exception ex) { Logger.Warning($"[mp] pump failed: {ex.Message}"); }
    }

    internal static void Stop()
    {
        try { _feed?.Dispose(); }
        catch (Exception ex) { Logger.Warning($"[mp] stop failed: {ex.Message}"); }
        finally { _feed = null; }
    }
}
