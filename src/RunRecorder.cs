using Il2CppAssets.Scripts.GameCore.HostComponent;
using Il2CppGameLogic;

namespace AccLineChart;

internal sealed class RunSample
{
    public float Time;
    public float Acc;
    public TaskResult Judge;
    public int Judged;
    public int Perfect;
    public int Great;
    public int Miss;

    public bool IsGreat => Judge == TaskResult.Great;
    public bool IsMiss => Judge == TaskResult.Miss;
}

internal sealed class PlayerSeries
{
    public int Uid;
    public string Name = "";
    public Color Color;
    public bool IsSelf;
    /// <summary>True for multiplayer players whose line is only known from the final round record (no live samples).</summary>
    public bool IsApproximate;
    /// <summary>Server (Euterpe round_record.frames) official accuracy; authoritative for the legend.</summary>
    public float? ServerFinalAcc;
    public readonly List<RunSample> Samples = new();

    public float FinalAcc => ServerFinalAcc ?? (Samples.Count == 0 ? 100f : Samples[^1].Acc);
}

/// <summary>
/// Collects one run's judgements following Info+'s rules:
/// - accuracy counters come straight from TaskStageTarget (perfect/great/block/music/energy/red point);
/// - misses are attributed per note type from GameMissPlay.MissCube and BattleEnemyManager.SetPlayResult
///   (ghost/energy/music/block/monster/long/double), which avoids counting the false "miss cube"
///   callbacks the game fires for notes that were actually hit;
/// - the note total mirrors the game's own denominator (regular combo notes + block + music + energy).
/// </summary>
internal static class RunRecorder
{
    private const uint TypeBlock = 2;
    private const uint TypeLong = 3;
    private const uint TypeGhost = 4;
    private const uint TypeEnergy = 6;
    private const uint TypeMusic = 7;
    private const uint TypeMul = 8;

    private static readonly List<PlayerSeries> _series = new();

    internal static IReadOnlyList<PlayerSeries> Series => _series;
    internal static bool InGame { get; private set; }
    internal static string ChartName { get; private set; } = "";
    internal static bool HasData => _series.Count > 0 && _series[0].Samples.Count > 0;

    internal static PlayerSeries Self => _series.Count > 0 ? _series[0] : null;

    internal static int TotalUnits { get; private set; }

    private static bool _scanDone;
    private static int _totalHittable;
    private static int _totalBlock;
    private static int _totalMusic;
    private static int _totalEnergy;

    private static int _lastPerfect;
    private static int _lastGreat;
    private static int _lastBlock;
    private static int _lastMusic;
    private static int _lastEnergy;
    private static int _lastRedPoint;
    private static int _lastJudged;
    private static int _lastMissCount;

    private static int _missCount;
    private static int _longPair;
    private static readonly HashSet<int> MissedOids = new();
    private static readonly HashSet<int> PlayedOids = new();
    private static readonly List<(int Oid, float DueTime)> PendingMulMisses = new();

    private static int _loggedSamples;
    private static int _loggedMissEvents;

    internal static void Begin()
    {
        _series.Clear();
        _series.Add(new PlayerSeries
        {
            Name = "你",
            Color = SettingManager.OwnLineColor,
            IsSelf = true,
        });

        InGame = true;
        ChartName = "";

        _scanDone = false;
        TotalUnits = 0;
        _totalHittable = _totalBlock = _totalMusic = _totalEnergy = 0;

        _lastPerfect = _lastGreat = _lastBlock = _lastMusic = _lastEnergy = _lastRedPoint = 0;
        _lastJudged = 0;
        _lastMissCount = 0;
        _missCount = 0;
        _longPair = 0;
        MissedOids.Clear();
        PlayedOids.Clear();
        PendingMulMisses.Clear();
        _loggedSamples = 0;
        _loggedMissEvents = 0;

        ChartWindow.Hide();
    }

    internal static void OnRunStarted()
    {
        Begin();
        TryScanTotals();
    }

    internal static void End()
    {
        InGame = false;
    }

    internal static float SongTime => ReadSongTime();

    /// <summary>BattleEnemyManager.SetPlayResult: per-note result (hits, long misses, double notes).</summary>
    internal static void OnNoteResult(int idx, byte result)
    {
        if (!InGame)
        {
            return;
        }

        try
        {
            var stage = StageBattleComponent.instance;
            MusicData note = null;
            try
            {
                note = stage?.GetMusicDataByIdx(idx);
            }
            catch
            {
                note = null;
            }

            if (note != null)
            {
                uint type = note.noteData.type;
                int oid = note.objId;

                if (type == TypeMul)
                {
                    if (result is 3 or 4)
                    {
                        PlayedOids.Add(oid);
                    }
                    else if (result <= 1)
                    {
                        float length = 1f;
                        try
                        {
                            length = (float)note.configData.length;
                        }
                        catch
                        {
                            length = 1f;
                        }

                        PendingMulMisses.Add((oid, ReadSongTime() + Math.Max(0.1f, length)));
                    }
                }
                else if (type == TypeBlock && result == 4)
                {
                    PlayedOids.Add(oid);
                }
                else if (type == TypeLong && result == 1)
                {
                    CountMiss(oid, note.isLongPressStart, $"long result={result}");
                }
            }

            Poll();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] OnNoteResult failed: {ex.Message}");
        }
    }

    /// <summary>GameMissPlay.MissCube: a note/ghost/collectible was missed.</summary>
    internal static void OnMissCube(int idx)
    {
        if (!InGame)
        {
            return;
        }

        try
        {
            var stage = StageBattleComponent.instance;
            MusicData note = null;
            try
            {
                note = stage?.GetMusicDataByIdx(idx);
            }
            catch
            {
                note = null;
            }

            if (note != null)
            {
                byte result;
                try
                {
                    var manager = BattleEnemyManager.instance;
                    result = manager != null ? manager.GetPlayResult(idx) : (byte)1;
                }
                catch
                {
                    result = 1;
                }

                uint type = note.noteData.type;
                int oid = note.objId;

                if (type == TypeMul)
                {
                    // doubles are resolved by SetPlayResult + the delayed mul miss check
                }
                else if (result <= 1)
                {
                    if (type == TypeBlock)
                    {
                        if (result != 0)
                        {
                            CountMiss(oid, false, $"block result={result}");
                        }
                    }
                    else if (type == TypeGhost)
                    {
                        CountMiss(oid, false, $"ghost result={result}");
                    }
                    else if (type == TypeEnergy)
                    {
                        CountMiss(oid, false, $"energy result={result}");
                    }
                    else if (type == TypeMusic)
                    {
                        CountMiss(oid, false, $"music result={result}");
                    }
                    else
                    {
                        CountMiss(oid, false, $"note type={type} result={result}");

                        if (note.isDouble)
                        {
                            try
                            {
                                var pair = stage.GetMusicDataByIdx(note.doubleIdx);
                                if (pair != null)
                                {
                                    CountMiss(pair.objId, false, $"double pair type={type}");
                                }
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    }
                }
            }

            Poll();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] OnMissCube failed: {ex.Message}");
        }
    }

    private static void CountMiss(int oid, bool longStart, string reason)
    {
        if (!MissedOids.Add(oid))
        {
            return;
        }

        _missCount++;
        if (longStart)
        {
            _longPair++;
        }

        if (_loggedMissEvents < 25)
        {
            _loggedMissEvents++;
            Logger.Msg($"[RunRecorder] miss +1 oid={oid} typeReason={reason} total={_missCount} longPair={_longPair}");
        }
    }

    /// <summary>Called from the per-note callbacks and from the update loop.</summary>
    internal static void Poll()
    {
        if (!InGame)
        {
            return;
        }

        try
        {
            var task = TaskStageTarget.instance;
            if (task == null)
            {
                return;
            }

            if (!_scanDone)
            {
                TryScanTotals();
            }

            float now = ReadSongTime();
            if (PendingMulMisses.Count > 0)
            {
                for (int i = PendingMulMisses.Count - 1; i >= 0; i--)
                {
                    var pending = PendingMulMisses[i];
                    if (now < pending.DueTime)
                    {
                        continue;
                    }

                    PendingMulMisses.RemoveAt(i);
                    if (!PlayedOids.Contains(pending.Oid))
                    {
                        CountMiss(pending.Oid, false, "mul delayed");
                    }
                }
            }

            int perfect = task.m_PerfectResult;
            int great = task.m_GreatResult;
            int block = task.m_Block;
            int music = task.m_MusicCount;
            int energy = task.m_EnergyCount;
            int redPoint = task.m_RedPoint;
            int gameMiss = task.m_MissResult;

            int miss = Math.Max(_missCount, gameMiss);
            int judged = perfect + great + block + music + energy + redPoint + miss;

            var self = Self;
            if (self == null)
            {
                return;
            }

            if (self.Samples.Count > 0)
            {
                var last = self.Samples[^1];
                bool restarted = judged < _lastJudged || now < last.Time - 0.5f;
                if (restarted)
                {
                    Begin();
                    TryScanTotals();
                    self = Self;
                }
                else if (judged <= _lastJudged)
                {
                    return;
                }
            }

            int total = Math.Max(TotalUnits, judged);

            double counted = perfect + great * 0.5 + block + music + energy + redPoint;
            double rest = Math.Max(0.0, total - (perfect + great + block + music + energy + redPoint) - _missCount - _longPair);
            float acc = total > 0 ? (float)Math.Clamp((counted + rest) / total * 100.0, 0.0, 100.0) : 100f;

            int dGreat = great - _lastGreat;
            int dMiss = _missCount - _lastMissCount;

            if (dGreat > 0 || dMiss > 0)
            {
                for (int i = 0; i < dGreat; i++)
                {
                    AddSample(self, now, acc, TaskResult.Great, judged, perfect, great, miss);
                }

                for (int i = 0; i < dMiss; i++)
                {
                    AddSample(self, now, acc, TaskResult.Miss, judged, perfect, great, miss);
                }
            }
            else
            {
                AddSample(self, now, acc, TaskResult.Prefect, judged, perfect, great, miss);
            }

            _lastPerfect = perfect;
            _lastGreat = great;
            _lastBlock = block;
            _lastMusic = music;
            _lastEnergy = energy;
            _lastRedPoint = redPoint;
            _lastJudged = judged;
            _lastMissCount = _missCount;

            if (_loggedSamples < 8)
            {
                _loggedSamples++;
                Logger.Msg($"[RunRecorder] judged={judged} total={total} acc={acc:0.00} p={perfect} g={great} b={block} m={music} e={energy} rp={redPoint} miss={miss}(evt={_missCount},game={gameMiss}) time={now:0.000}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] Poll failed: {ex}");
        }
    }

    private static void AddSample(PlayerSeries self, float time, float acc, TaskResult judge, int judged, int perfect, int great, int miss)
    {
        self.Samples.Add(new RunSample
        {
            Time = time,
            Acc = acc,
            Judge = judge,
            Judged = judged,
            Perfect = perfect,
            Great = great,
            Miss = miss,
        });
    }

    private static void TryScanTotals()
    {
        try
        {
            var stage = StageBattleComponent.instance;
            if (stage == null)
            {
                return;
            }

            var list = stage.GetMusicData();
            if (list == null || list.Count == 0)
            {
                return;
            }

            int hittable = 0, block = 0, music = 0, energy = 0;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                MusicData note;
                try
                {
                    note = list[i];
                }
                catch
                {
                    continue;
                }

                if (note == null || note.isLongPressing)
                {
                    continue;
                }

                if (note.noteData.addCombo)
                {
                    hittable++;
                }

                switch (note.noteData.type)
                {
                    case TypeBlock:
                        block++;
                        break;
                    case TypeEnergy:
                        energy++;
                        break;
                    case TypeMusic:
                        music++;
                        break;
                }
            }

            int total = hittable + block + music + energy;
            if (total <= 0)
            {
                return;
            }

            _totalHittable = hittable;
            _totalBlock = block;
            _totalMusic = music;
            _totalEnergy = energy;
            TotalUnits = total;
            _scanDone = true;

            Logger.Msg($"[RunRecorder] total units={total} (hittable={hittable} block={block} music={music} energy={energy})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] total scan failed: {ex.Message}");
        }
    }

    private static float ReadSongTime()
    {
        try
        {
            var battle = StageBattleComponent.instance;
            return battle == null ? 0f : battle.timeFromMusicStart;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>Called from PnlVictory.OnVictory.</summary>
    internal static void FinalizeResults()
    {
        try
        {
            var self = Self;
            if (self == null || self.Samples.Count == 0)
            {
                return;
            }

            float ours = self.FinalAcc;
            float game = 0f;
            try
            {
                var task = TaskStageTarget.instance;
                if (task != null)
                {
                    game = task.GetAccuracy() * 100f;
                }
            }
            catch
            {
                // ignored
            }

            Logger.Msg($"[RunRecorder] final ours={ours:0.00} game={game:0.00} total={TotalUnits} miss={_missCount} longPair={_longPair} judgedLast={_lastJudged}");

            if (game > 0.01f && Math.Abs(game - ours) > 0.01f)
            {
                self.Samples.Add(new RunSample
                {
                    Time = ReadSongTime(),
                    Acc = Math.Clamp(game, 0f, 100f),
                    Judge = TaskResult.None,
                    Judged = _lastJudged,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] finalize failed: {ex.Message}");
        }
    }

    /// <summary>Creates demo data so the window can be previewed from the menu.</summary>
    internal static void FillDebugData()
    {
        _series.Clear();
        var self = new PlayerSeries { Name = "你", IsSelf = true, Color = SettingManager.OwnLineColor };
        _series.Add(self);

        var rnd = new System.Random(20261122);
        int perfect = 0, great = 0, miss = 0;
        const float totalTime = 96f;
        const int notes = 420;
        for (int i = 0; i < notes; i++)
        {
            var judge = TaskResult.Prefect;
            double roll = rnd.NextDouble();
            if (roll < 0.02)
            {
                judge = TaskResult.Great;
                great++;
            }
            else if (roll < 0.024)
            {
                judge = TaskResult.Miss;
                miss++;
            }
            else
            {
                perfect++;
            }

            int judged = perfect + great + miss;
            float acc = (float)Math.Clamp((perfect + great * 0.5 + (notes - judged)) / notes * 100.0, 0.0, 100.0);
            self.Samples.Add(new RunSample
            {
                Time = totalTime * i / notes,
                Acc = acc,
                Judge = judge,
                Judged = judged,
                Perfect = perfect,
                Great = great,
                Miss = miss,
            });
        }

        ChartName = "";
        // Preview data must not enter the live recording loop: Poll() sees timeFromMusicStart=0
        // on the menu and would think the run restarted (Begin() hides the window in the same frame).
        InGame = false;
    }
}
