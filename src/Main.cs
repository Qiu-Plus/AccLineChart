namespace AccLineChart;

internal class Main : MelonMod
{
    public override void OnInitializeMelon()
    {
        SettingManager.Register();

        HarmonyInstance.PatchAll(typeof(Main).Assembly);
        if (SettingManager.DebugLog)
        {
            LoggerInstance.Msg($"[main] harmony patches applied: {HarmonyInstance.GetPatchedMethods().Count()} methods");
        }
        MelonEvents.OnGUI.Subscribe(ChartWindow.OnGUI);

        MultiplayerTap.Start();
        MultiplayerRecorder.Initialize();

        if (SettingManager.DebugLog)
        {
            MultiplayerRecorder.DebugAvatarSelfTest("https://euterpe-org.com/static/images/1000/1396.webp");
        }

        LoggerInstance.Msg($"{Name} {ModVersion} initialized.");
    }

    public override void OnDeinitializeMelon()
    {
        MelonEvents.OnGUI.Unsubscribe(ChartWindow.OnGUI);
        MultiplayerTap.Stop();
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        try
        {
            switch (sceneName)
            {
                case "GameMain":
                    // Do NOT reset the run here: Euterpe routes back through GameMain when a
                    // multiplayer round ends, and that used to wipe the local player's curve
                    // before the results window opened. A real new run is started by
                    // StageBattleComponent.GameStart instead.
                    ChartWindow.Hide();
                    ChartWindow.RequestPrewarm();
                    break;
                case "UISystem_PC":
                    if (MultiplayerRecorder.PendingAutoShow)
                    {
                        _mpShowAt = Time.time + 0.8f;
                    }
                    else
                    {
                        ChartWindow.Hide();
                    }

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
    private static float _mpShowAt = -1f;

    public override void OnUpdate()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                ChartWindow.ToggleDebug();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                ChartWindow.ToggleAllPlayers();
            }

            RunRecorder.Poll();
            MultiplayerTap.Pump();
            MultiplayerRecorder.Poll();
            ChartWindow.UpdatePointerState();
            InputGuard.SuppressScroll = ChartWindow.PointerOverWindow;
            UiInputBlocker.Tick();

            // After a multiplayer round, show the chart automatically once we are back in a menu.
            if (MultiplayerRecorder.PendingAutoShow && !RunRecorder.InGame && _mpShowAt < 0f)
            {
                _mpShowAt = Time.time + 0.8f;
            }

            if (_mpShowAt >= 0f && Time.time >= _mpShowAt)
            {
                _mpShowAt = -1f;
                if (MultiplayerRecorder.PendingAutoShow)
                {
                    MultiplayerRecorder.PendingAutoShow = false;
                    ChartWindow.ShowMultiplayer();
                }
            }


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
