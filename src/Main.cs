namespace AccLineChart;

internal class Main : MelonMod
{
    public override void OnInitializeMelon()
    {
        SettingManager.Register();

        HarmonyInstance.PatchAll(typeof(Main).Assembly);
        MelonEvents.OnGUI.Subscribe(ChartWindow.OnGUI);

        LoggerInstance.Msg($"{Name} {ModVersion} initialized.");
    }

    public override void OnDeinitializeMelon()
    {
        MelonEvents.OnGUI.Unsubscribe(ChartWindow.OnGUI);
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        try
        {
            switch (sceneName)
            {
                case "GameMain":
                    RunRecorder.Begin();
                    ChartWindow.Hide();
                    ChartWindow.RequestPrewarm();
                    break;
                case "UISystem_PC":
                    ChartWindow.Hide();
                    ChartWindow.RequestPrewarm();
                    break;
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"scene handler failed: {ex}");
        }
    }

    private static readonly string OpenFlagPath =
        Path.Combine(MelonEnvironment.UserDataDirectory, "AccLineChart_open.flag");

    private static int _frames;

    public override void OnUpdate()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                ChartWindow.ToggleDebug();
            }

            RunRecorder.Poll();


            // Developer-only helpers, off unless DebugLog is enabled in the config file.
            if (!SettingManager.DebugLog)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                SceneProbe.Dump();
            }

            if (++_frames % 30 == 0 && File.Exists(OpenFlagPath))
            {
                try
                {
                    File.Delete(OpenFlagPath);
                }
                catch
                {
                    // ignore
                }

                ChartWindow.ToggleDebug();
            }
        }
        catch
        {
            // Input module may not be available very early.
        }
    }
}
