using System.Text.Json;
using System.Text.Json.Serialization;

namespace EuterpeMp;

// =====================================================================
// Confirmed wire model — captured live from protocol 8 / mp8 (2026-11-24).
// Envelope: {"t":"room_state","v":<snapshot seq>,"d":{...}}
// =====================================================================

/// <summary>Room chart filter (captured from room_state.d.chart_filter).</summary>
public sealed class MpChartFilter
{
    public double DiffMin;
    public double DiffMax;
    public bool AllowVanilla;
    public bool AllowCustom;
    public bool AllowHidden;
    public bool AllowUnrated;
    public bool AllowTouhou;
    public string? CustomRatingSource;

    public static MpChartFilter Parse(JsonElement e)
    {
        var f = new MpChartFilter();
        if (e.ValueKind != JsonValueKind.Object) return f;
        f.DiffMin = MpJson.Num(e, "diff_min") ?? 0;
        f.DiffMax = MpJson.Num(e, "diff_max") ?? 0;
        f.AllowVanilla = MpJson.Bool(e, "allow_vanilla") ?? false;
        f.AllowCustom = MpJson.Bool(e, "allow_custom") ?? false;
        f.AllowHidden = MpJson.Bool(e, "allow_hidden") ?? false;
        f.AllowUnrated = MpJson.Bool(e, "allow_unrated") ?? false;
        f.AllowTouhou = MpJson.Bool(e, "allow_touhou") ?? false;
        f.CustomRatingSource = MpJson.Str(e, "custom_rating_source");
        return f;
    }
}

/// <summary>Chart reference inside a round.</summary>
public sealed class MpChartRef
{
    public string? Cid;
    public string? VanillaUid;
    public int? Slot;
    public double? Difficulty;
    public bool RevealAudio;

    public static MpChartRef Parse(JsonElement e)
    {
        var c = new MpChartRef();
        if (e.ValueKind != JsonValueKind.Object) return c;
        c.Cid = MpJson.Str(e, "cid");
        c.VanillaUid = MpJson.Str(e, "vanilla_uid");
        c.Slot = (int?)MpJson.Num(e, "slot");
        c.Difficulty = MpJson.Num(e, "difficulty");
        c.RevealAudio = MpJson.Bool(e, "reveal_audio") ?? false;
        return c;
    }
}

/// <summary>Live round in room_state.d.round (present while status != "vote").</summary>
public sealed class MpRound
{
    public int Seq;
    public MpChartRef Chart = new();
    public string Goal = "";
    public List<int> Entrants = new();
    public JsonElement? Stage;
    public long T0;
    public long? Deadline;
    public long? StageFloor;

    public static MpRound Parse(JsonElement e)
    {
        var r = new MpRound();
        if (e.ValueKind != JsonValueKind.Object) return r;
        r.Seq = (int)(MpJson.Num(e, "seq") ?? 0);
        if (MpJson.TryGet(e, out var chart, "chart")) r.Chart = MpChartRef.Parse(chart);
        r.Goal = MpJson.Str(e, "goal") ?? "";
        if (MpJson.TryGet(e, out var entrants, "entrants") && entrants.ValueKind == JsonValueKind.Array)
            foreach (var x in entrants.EnumerateArray())
                if (x.TryGetInt32(out var uid)) r.Entrants.Add(uid);
        if (MpJson.TryGet(e, out var stage, "stage")) r.Stage = stage.Clone();
        r.T0 = (long)(MpJson.Num(e, "t0") ?? 0);
        r.Deadline = (long?)MpJson.Num(e, "deadline");
        r.StageFloor = (long?)MpJson.Num(e, "stage_floor");
        return r;
    }
}

/// <summary>Summary of the previous round (room_state.d.last_round).</summary>
public sealed class MpLastRound
{
    public int Seq;
    public bool Aborted;
    public string? Reason;
    public MpChartRef Chart = new();

    public static MpLastRound Parse(JsonElement e)
    {
        var r = new MpLastRound();
        if (e.ValueKind != JsonValueKind.Object) return r;
        r.Seq = (int)(MpJson.Num(e, "seq") ?? 0);
        r.Aborted = MpJson.Bool(e, "aborted") ?? false;
        r.Reason = MpJson.Str(e, "reason");
        if (MpJson.TryGet(e, out var chart, "chart")) r.Chart = MpChartRef.Parse(chart);
        return r;
    }
}

/// <summary>
/// One room member — this is where nickname / avatar live.
/// Captured example:
/// {"uid":430,"nick":"Qiu-Plus","avatar":"/static/images/1000/1396.webp","roboko":10.337,
///  "character":30,"elfin":2,"cosmetic_character":33,"connected":true,"muted":false,
///  "resting":false,"vote":null,"chart_ready":true,"chart_match":null,"sync_state":null,
///  "sync_progress":null,"loaded":true,"round_drop":null,"state":"running",
///  "new_score":false,"new_acc":false,"first_time":false}
/// </summary>
public sealed class MpMember
{
    public int Uid;
    /// <summary>Nickname (wire field is <c>nick</c>).</summary>
    public string Nick = "";
    /// <summary>Avatar URL. Wire value is usually a site-relative path like /static/images/1000/1396.webp.</summary>
    public string AvatarUrl = "";
    public double? Roboko;
    public int? Character;
    public int? Elfin;
    public int? CosmeticCharacter;
    public bool Connected;
    public bool Muted;
    public bool Resting;
    public bool ChartReady;
    public bool Loaded;
    public string? State;          // e.g. "running", "cleared"
    public double? SyncProgress;   // live in-round progress when present
    public JsonElement? SyncState; // live in-round sync blob (shape depends on client build)
    public JsonElement? Vote;
    public JsonElement? ChartMatch;
    public JsonElement? RoundDrop;
    public bool NewScore;
    public bool NewAccuracy;
    public bool FirstTime;

    public static MpMember Parse(JsonElement e)
    {
        var m = new MpMember
        {
            Uid = (int)(MpJson.Num(e, "uid") ?? 0),
            Nick = MpJson.Str(e, "nick", "nickname") ?? "",
            Roboko = MpJson.Num(e, "roboko", "rating"),
            Character = (int?)MpJson.Num(e, "character", "role"),
            Elfin = (int?)MpJson.Num(e, "elfin"),
            CosmeticCharacter = (int?)MpJson.Num(e, "cosmetic_character"),
            Connected = MpJson.Bool(e, "connected") ?? false,
            Muted = MpJson.Bool(e, "muted") ?? false,
            Resting = MpJson.Bool(e, "resting") ?? false,
            ChartReady = MpJson.Bool(e, "chart_ready") ?? false,
            Loaded = MpJson.Bool(e, "loaded") ?? false,
            State = MpJson.Str(e, "state"),
            SyncProgress = MpJson.Num(e, "sync_progress"),
            NewScore = MpJson.Bool(e, "new_score") ?? false,
            NewAccuracy = MpJson.Bool(e, "new_acc") ?? false,
            FirstTime = MpJson.Bool(e, "first_time") ?? false,
        };
        var avatar = MpJson.Str(e, "avatar", "avatar_url") ?? "";
        m.AvatarUrl = ResolveAvatar(avatar);
        if (MpJson.TryGet(e, out var v, "vote")) m.Vote = v.Clone();
        if (MpJson.TryGet(e, out var cm, "chart_match")) m.ChartMatch = cm.Clone();
        if (MpJson.TryGet(e, out var ss, "sync_state")) m.SyncState = ss.Clone();
        if (MpJson.TryGet(e, out var rd, "round_drop")) m.RoundDrop = rd.Clone();
        return m;
    }

    public static string ResolveAvatar(string? avatar)
    {
        if (string.IsNullOrWhiteSpace(avatar)) return "";
        if (avatar.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            avatar.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return avatar;
        return "https://euterpe-org.com" + (avatar.StartsWith('/') ? "" : "/") + avatar;
    }

    public override string ToString() => $"{Nick}({Uid}) roboko={Roboko?.ToString("0.00") ?? "?"} state={State}";
}

/// <summary>room_state.d. Captured live on 2026-11-24 (protocol 8).</summary>
public sealed class MpRoomState
{
    public string RoomId = "";
    public string Visibility = "";
    public string? Code;
    public int HostUid;
    public int Max;
    public string Goal = "";
    public string ModVersion = "";
    public string GameVersion = "";
    public MpChartFilter ChartFilter = new();
    public int LoadingMinS;
    public bool VotesOpen;
    public int BlacklistRev;
    public long StartAt;
    public string Status = "";        // "vote" | "battle" | ...
    public MpRound? Round;
    public MpLastRound? LastRound;
    public List<MpMember> Members = new();
    public int Observers;
    public int RoomVersion;
    public int SnapshotSeq;           // envelope "v"
    public string RawJson = "";
    public DateTimeOffset UpdatedAt = DateTimeOffset.UtcNow;

    public static MpRoomState Parse(JsonElement d, int snapshotSeq, string raw)
    {
        var s = new MpRoomState { RawJson = raw, SnapshotSeq = snapshotSeq, UpdatedAt = DateTimeOffset.UtcNow };
        if (d.ValueKind != JsonValueKind.Object) return s;
        s.RoomId = MpJson.Str(d, "room_id") ?? "";
        s.Visibility = MpJson.Str(d, "visibility") ?? "";
        s.Code = MpJson.Str(d, "code");
        s.HostUid = (int)(MpJson.Num(d, "host_uid") ?? 0);
        s.Max = (int)(MpJson.Num(d, "max") ?? 0);
        s.Goal = MpJson.Str(d, "goal") ?? "";
        s.ModVersion = MpJson.Str(d, "mod_version") ?? "";
        s.GameVersion = MpJson.Str(d, "game_version") ?? "";
        s.LoadingMinS = (int)(MpJson.Num(d, "loading_min_s") ?? 0);
        s.VotesOpen = MpJson.Bool(d, "votes_open") ?? false;
        s.BlacklistRev = (int)(MpJson.Num(d, "blacklist_rev") ?? 0);
        s.StartAt = (long)(MpJson.Num(d, "start_at") ?? 0);
        s.Status = MpJson.Str(d, "status") ?? "";
        s.Observers = (int)(MpJson.Num(d, "observers") ?? 0);
        s.RoomVersion = (int)(MpJson.Num(d, "room_version") ?? 0);
        if (MpJson.TryGet(d, out var cf, "chart_filter")) s.ChartFilter = MpChartFilter.Parse(cf);
        if (MpJson.TryGet(d, out var round, "round") && round.ValueKind == JsonValueKind.Object)
            s.Round = MpRound.Parse(round);
        if (MpJson.TryGet(d, out var last, "last_round") && last.ValueKind == JsonValueKind.Object)
            s.LastRound = MpLastRound.Parse(last);
        if (MpJson.TryGet(d, out var members, "members") && members.ValueKind == JsonValueKind.Array)
            foreach (var m in members.EnumerateArray())
                s.Members.Add(MpMember.Parse(m));
        return s;
    }

    public MpMember? Member(int uid) => Members.FirstOrDefault(m => m.Uid == uid);
}

/// <summary>One row of a finished round (round_record event). Fields observed as literals in Euterpe.Hot.dll: slot/acc/score/fc/fail/total.</summary>
public sealed class MpRoundRecordEntry
{
    public int? Slot;
    public double? Acc;
    public long? Score;
    public bool? FullCombo;
    public bool? Failed;
    public int? Total;

    public static MpRoundRecordEntry Parse(JsonElement e)
    {
        var r = new MpRoundRecordEntry
        {
            Slot = (int?)MpJson.Num(e, "slot"),
            Acc = MpJson.Num(e, "acc", "accuracy"),
            Score = (long?)MpJson.Num(e, "score"),
            Total = (int?)MpJson.Num(e, "total"),
        };
        if (MpJson.TryGet(e, out var fc, "fc"))
            r.FullCombo = fc.ValueKind == JsonValueKind.True || (fc.ValueKind == JsonValueKind.Number && fc.GetInt32() != 0);
        if (MpJson.TryGet(e, out var fail, "fail"))
            r.Failed = fail.ValueKind == JsonValueKind.True || (fail.ValueKind == JsonValueKind.Number && fail.GetInt32() != 0);
        if (r.Acc is { } a && a <= 1.5) r.Acc = a * 100.0;
        return r;
    }
}

/// <summary>
/// Conflict/result of one finished round. Live capture (protocol 8, 2026-11-24) shows the
/// server sends the same shape as round_history.rounds[]:
///   { seq, chart, goal, winners:[uid], settled_at, roster:[...], frames:"base64" }
/// The per-player score/accuracy are binary-packed in <see cref="Frames"/>; the roster only
/// carries nickname/avatar/flags.
/// </summary>
public sealed class MpRoundRecord
{
    public int? Seq;
    public bool Aborted;
    public string? Goal;
    public long? SettledAt;
    public MpChartRef? Chart;
    public List<int> Winners = new();
    /// <summary>Per-player public info in entry order (nickname/avatar/flags); score/acc are in Frames.</summary>
    public List<MpMember> Roster = new();
    /// <summary>Base64 binary payload: per-player result records (uid, score, accuracy, judgement counts).</summary>
    public string? Frames;
    /// <summary>Optional typed rows (older/other protocol variants).</summary>
    public List<MpRoundRecordEntry> Entries = new();

    public static MpRoundRecord Parse(JsonElement d)
    {
        var r = new MpRoundRecord();
        if (d.ValueKind != JsonValueKind.Object) return r;
        r.Seq = (int?)MpJson.Num(d, "seq");
        r.Aborted = MpJson.Bool(d, "aborted") ?? false;
        r.Goal = MpJson.Str(d, "goal");
        r.SettledAt = (long?)MpJson.Num(d, "settled_at");
        if (MpJson.TryGet(d, out var chart, "chart") && chart.ValueKind == JsonValueKind.Object)
            r.Chart = MpChartRef.Parse(chart);
        if (MpJson.TryGet(d, out var winners, "winners") && winners.ValueKind == JsonValueKind.Array)
            foreach (var w in winners.EnumerateArray())
                if (w.TryGetInt32(out var uid)) r.Winners.Add(uid);
        if (MpJson.TryGet(d, out var roster, "roster") && roster.ValueKind == JsonValueKind.Array)
            foreach (var m in roster.EnumerateArray())
                r.Roster.Add(MpMember.Parse(m));
        r.Frames = MpJson.Str(d, "frames");
        var array = d;
        if (MpJson.TryGet(d, out var entries, "entries") && entries.ValueKind == JsonValueKind.Array)
            array = entries;
        else if (MpJson.TryGet(d, out var results, "results") && results.ValueKind == JsonValueKind.Array)
            array = results;
        if (array.ValueKind == JsonValueKind.Array)
            foreach (var e in array.EnumerateArray())
                r.Entries.Add(MpRoundRecordEntry.Parse(e));
        return r;
    }
}

/// <summary>A single decoded server message.</summary>
public sealed class MpEvent
{
    public string Kind = "unknown";
    public JsonElement Payload;
    public int SnapshotSeq;
    public string RawJson = "";
    public DateTimeOffset At = DateTimeOffset.UtcNow;
}

public sealed class MpEventNames
{
    // server -> client (literal set from Euterpe.Hot.dll + captured frames)
    public static readonly string[] ServerEvents =
    {
        "auth_ok", "relations", "room_state", "chat_history", "round_history",
        "round_record", "time_pong", "server_mono", "rounds", "messages",
        "follows", "server_protocol", "blacklist", "error",
    };

    // client -> server (literal set from Euterpe.Hot.dll)
    public static readonly string[] ClientActions =
    {
        "auth", "create_room", "join_room", "time_ping", "set_profile", "leave_room",
        "edit_room", "transfer_host", "kick", "unmute", "set_resting", "vote_chart",
        "clear_vote", "host_clear_vote", "host_clear_pool", "set_votes_open",
        "blacklist_set", "withdraw_round", "add_time", "chart_ready", "sync_state",
        "chart_load_failed", "loaded", "abort_round", "finish", "chat_send",
    };

    public static readonly string[] ErrorCodes =
    {
        "TICKET_INVALID", "TICKET_EXPIRED", "VERSION_UNSUPPORTED", "ROOM_NOT_FOUND",
        "ROOM_FULL", "BAD_CODE", "HOST_BLOCKED", "ALREADY_IN_ROOM", "NOT_IN_ROOM",
        "TARGET_NOT_IN_ROOM", "VERSION_MISMATCH", "SERVER_FULL", "SPECTATORS_FULL",
        "NOT_HOST", "MUTED", "SELF_TARGET", "CONTENT_BLOCKED", "CHART_FILTERED",
        "CUSTOM_CHARTS_DISABLED", "CHART_BLACKLISTED", "CHAT_RATE_LIMITED",
        "TOGGLING_TOO_FAST", "START_COOLDOWN", "POOL_LOCKED", "VOTES_CLOSED",
        "ENTRY_CLOSED", "BLACKLIST_TOO_LARGE", "POOL_EMPTY", "ROUND_IN_FLIGHT",
        "TOO_LATE", "CAPACITY_BELOW_MEMBERS", "INVALID_ARGUMENT", "INVALID_STATE",
    };
}
