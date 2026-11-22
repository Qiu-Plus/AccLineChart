namespace AccLineChart;

internal sealed class RunSample
{
    public float Time;
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

    public float FinalAcc(int total)
    {
        if (Samples.Count == 0)
        {
            return 100f;
        }

        return RunRecorder.AccuracyOf(Samples[^1], total);
    }
}

internal static class RunRecorder
{
    private static readonly List<PlayerSeries> _series = new();

    internal static IReadOnlyList<PlayerSeries> Series => _series;
    internal static bool InGame { get; private set; }
    internal static int TotalNotes { get; private set; }
    internal static string ChartName { get; private set; } = "";
    internal static bool HasData => _series.Count > 0 && _series[0].Samples.Count > 0;

    internal static PlayerSeries Self => _series.Count > 0 ? _series[0] : null;

    private const double PerfectWeight = 1.0;
    private const double GreatWeight = 0.5;
    private const double CoolWeight = 0.5;

    private static int _loggedJudges;

    internal static int EffectiveTotal
    {
        get
        {
            int maxJudged = 0;
            foreach (var player in _series)
            {
                if (player.Samples.Count > 0)
                {
                    maxJudged = Math.Max(maxJudged, player.Samples[^1].Judged);
                }
            }

            return Math.Max(TotalNotes, maxJudged);
        }
    }

    /// <summary>
    /// Accuracy of a sample. Unjudged notes are treated as if they will all be Perfect, so a
    /// fresh run starts at 100% and only loses accuracy when a Great or a Miss happens.
    /// </summary>
    internal static float AccuracyOf(RunSample sample, int total)
    {
        if (total <= 0)
        {
            total = Math.Max(1, sample.Judged);
        }

        double weighted = sample.Perfect * PerfectWeight + sample.Great * GreatWeight + sample.Cool * CoolWeight;
        int unjudged = Math.Max(0, total - sample.Judged);
        double acc = (weighted + unjudged * PerfectWeight) / total * 100.0;
        return Math.Clamp((float)acc, 0f, 100f);
    }

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
        TotalNotes = 0;
        ChartName = "";
        _loggedJudges = 0;

        ChartWindow.Hide();
    }

    internal static void OnRunStarted()
    {
        Begin();
        TryReadTotalNotes();
    }

    /// <summary>Called after TaskStageTarget.InvokeJudgeEvent(uint result).</summary>
    internal static void OnJudge(TaskStageTarget target, uint rawResult)
    {
        if (!InGame || target == null)
        {
            return;
        }

        try
        {
            var judge = (TaskResult)rawResult;

            int perfect = SafeCount(target, TaskResult.Prefect);
            int great = SafeCount(target, TaskResult.Great);
            int cool = SafeCount(target, TaskResult.Cool);
            int miss = SafeCount(target, TaskResult.Miss);
            int judged = perfect + great + cool + miss;

            var self = Self;
            if (self == null)
            {
                return;
            }

            float time = ReadSongTime();

            if (self.Samples.Count > 0)
            {
                var last = self.Samples[^1];

                // A restart happens inside the same scene, so the counters and the song clock
                // both start over. Treat that as the beginning of a new run.
                if (judged < last.Judged || time < last.Time - 0.5f)
                {
                    Begin();
                    self = Self;
                }
                else if (judged <= last.Judged)
                {
                    // InvokeJudgeEvent can be raised twice for the same note.
                    return;
                }
            }

            if (TotalNotes <= 0 || judged > TotalNotes)
            {
                TryReadTotalNotes();
            }

            self.Samples.Add(new RunSample
            {
                Time = time,
                Judge = judge,
                Judged = judged,
                Perfect = perfect,
                Great = great,
                Cool = cool,
                Miss = miss,
            });

            if (_loggedJudges < 10)
            {
                _loggedJudges++;
                int total = EffectiveTotal;
                Logger.Msg($"[RunRecorder] judge={judge} acc={AccuracyOf(self.Samples[^1], total):0.00} time={time:0.000} p={perfect} g={great} c={cool} m={miss} total={TotalNotes} eff={total}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RunRecorder] OnJudge failed: {ex}");
        }
    }

    internal static float SongTime => ReadSongTime();

    internal static void OnNoteScored(int id, string noteType, bool isAir, float time)
    {
        if (Logger.IsDebug)
        {
            Logger.Msg($"[RunRecorder] score id={id} type={noteType} air={isAir} time={time:0.000}");
        }
    }

    private static int SafeCount(TaskStageTarget target, TaskResult result)
    {
        try
        {
            return target.GetHitCountByResult(result);
        }
        catch
        {
            return 0;
        }
    }

    private static void TryReadTotalNotes()
    {
        try
        {
            var battle = StageBattleComponent.instance;
            if (battle == null)
            {
                return;
            }

            int count = 0;
            try
            {
                var list = battle.m_MusicTickData;
                if (list != null)
                {
                    count = list.Count;
                }
            }
            catch
            {
                count = 0;
            }

            if (count <= 0)
            {
                try
                {
                    var list = battle.m_SortedMusicTickData;
                    if (list != null)
                    {
                        count = list.Count;
                    }
                }
                catch
                {
                    count = 0;
                }
            }

            if (count > 0)
            {
                TotalNotes = count;
            }
        }
        catch
        {
            // ignored
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

            self.Samples.Add(new RunSample
            {
                Time = totalTime * i / notes,
                Judge = judge,
                Judged = perfect + great + miss,
                Perfect = perfect,
                Great = great,
                Miss = miss,
            });
        }

        TotalNotes = notes;
        ChartName = "";
        InGame = true;
    }
}
