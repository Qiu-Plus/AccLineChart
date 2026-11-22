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
    public int Cool;
    public int Miss;

    public bool IsGreat => Judge == TaskResult.Great;
    public bool IsMiss => Judge == TaskResult.Miss;
}

internal sealed class PlayerSeries
{
    public string Name = "";
    public Color Color;
    public bool IsSelf;
    public readonly List<RunSample> Samples = new();

    public float FinalAcc => Samples.Count == 0 ? 100f : Samples[^1].Acc;
}

/// <summary>
/// Collects one run's judgements. The per-frame counters on TaskStageTarget are the source of
/// truth, and the per-note callbacks (BattleEnemyManager.SetPlayResult / GameMissPlay.MissCube)
/// are used as cheap triggers so a note is picked up in the very frame it resolves.
/// The note totals mirror what the game itself counts (Info+ uses the same breakdown): regular
/// combo notes, block notes, energy notes and music notes.
/// </summary>
internal static class RunRecorder
{
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
    private static int _lastMiss;
    private static int _lastJudged;

    private static int _loggedSamples;

    private static int _missCount;
    private static int _lastMissCount;
    private static readonly HashSet<int> MissedIds = new();

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

        _lastPerfect = _lastGreat = _lastBlock = _lastMusic = _lastEnergy = _lastRedPoint = _lastMiss = 0;
        _lastJudged = 0;
        _missCount = 0;
        _lastMissCount = 0;
        MissedIds.Clear();
        _loggedSamples = 0;

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

    /// <summary>Called from PnlVictory.OnVictory: log the final numbers and keep the shown end
    /// value identical to the game's own accuracy.</summary>
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

            Logger.Msg($"[RunRecorder] final ours={ours:0.00} game={game:0.00} total={TotalUnits} judged={_lastJudged} miss={_lastMiss}(evt={_missCount})");

            if (game > 0.01f && Math.Abs(game - ours) > 0.01f)
            {
                self.Samples.Add(new RunSample
                {
                    Time = ReadSongTime(),
                    Acc = Math.Clamp(game, 0f, 100f),
                    Judge = TaskResult.None,
                    Judged = _lastJudged,
                    Perfect = _lastPerfect,
                    Great = _lastGreat,
                    Miss = _lastMiss,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] finalize failed: {ex.Message}");
        }
    }

    internal static float SongTime => ReadSongTime();

    /// <summary>A note was reported by BattleEnemyManager.SetPlayResult. result == 1 means miss.</summary>
    internal static void OnNoteResult(int idx, byte result)
    {
        if (!InGame)
        {
            return;
        }

        try
        {
            if (result <= 1 && MissedIds.Add(idx))
            {
                _missCount++;
            }

            Poll();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] OnNoteResult failed: {ex.Message}");
        }
    }

    /// <summary>A missed note/ghost was reported by GameMissPlay.MissCube.</summary>
    internal static void OnMissCube(int idx)
    {
        if (!InGame)
        {
            return;
        }

        try
        {
            bool missed = true;
            try
            {
                var manager = BattleEnemyManager.instance;
                if (manager != null)
                {
                    byte result = manager.GetPlayResult(idx);
                    missed = result <= 1;
                }
            }
            catch
            {
                missed = true;
            }

            if (missed && MissedIds.Add(idx))
            {
                _missCount++;
            }

            Poll();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] OnMissCube failed: {ex.Message}");
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

            int perfect = task.m_PerfectResult;
            int great = task.m_GreatResult;
            int block = task.m_Block;
            int music = task.m_MusicCount;
            int energy = task.m_EnergyCount;
            int redPoint = task.m_RedPoint;
            int gameMiss = task.m_MissResult;
            int miss = Math.Max(_missCount, gameMiss);

            int judged = perfect + great + block + music + energy + redPoint + miss;
            float time = ReadSongTime();

            var self = Self;
            if (self == null)
            {
                return;
            }

            if (self.Samples.Count > 0)
            {
                var last = self.Samples[^1];
                bool restarted = judged < _lastJudged || time < last.Time - 0.5f;
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

            double weighted = perfect + great * 0.5 + block + music + energy + redPoint;
            int rest = Math.Max(0, total - judged);
            float acc = total > 0 ? (float)Math.Clamp((weighted + rest) / total * 100.0, 0.0, 100.0) : 100f;

            int dGreat = great - _lastGreat;
            int dMiss = _missCount - _lastMissCount;

            if (dGreat > 0 || dMiss > 0)
            {
                for (int i = 0; i < dGreat; i++)
                {
                    AddSample(self, time, acc, TaskResult.Great, judged, perfect, great, miss);
                }

                for (int i = 0; i < dMiss; i++)
                {
                    AddSample(self, time, acc, TaskResult.Miss, judged, perfect, great, miss);
                }
            }
            else
            {
                AddSample(self, time, acc, TaskResult.Prefect, judged, perfect, great, miss);
            }

            _lastPerfect = perfect;
            _lastGreat = great;
            _lastBlock = block;
            _lastMusic = music;
            _lastEnergy = energy;
            _lastRedPoint = redPoint;
            _lastMiss = miss;
            _lastMissCount = _missCount;
            _lastJudged = judged;

            if (_loggedSamples < 8)
            {
                _loggedSamples++;
                Logger.Msg($"[RunRecorder] judged={judged} total={total} acc={acc:0.00} p={perfect} g={great} b={block} m={music} e={energy} rp={redPoint} miss={miss}(evt={_missCount},game={gameMiss}) time={time:0.000}");
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
                    case 2:
                        block++;
                        break;
                    case 6:
                        energy++;
                        break;
                    case 7:
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
        InGame = true;
    }
}
