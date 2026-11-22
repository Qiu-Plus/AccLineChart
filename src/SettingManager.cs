namespace AccLineChart;

internal static class SettingManager
{
    private static readonly string ConfigPath = Path.Combine(MelonEnvironment.UserDataDirectory, $"{Name}.cfg");

    private static MelonPreferences_Entry<float> _lineR;
    private static MelonPreferences_Entry<float> _lineG;
    private static MelonPreferences_Entry<float> _lineB;
    private static MelonPreferences_Entry<float> _windowX;
    private static MelonPreferences_Entry<float> _windowY;
    private static MelonPreferences_Entry<float> _windowW;
    private static MelonPreferences_Entry<float> _windowH;
    private static MelonPreferences_Entry<bool> _showPerfectDots;
    private static MelonPreferences_Entry<bool> _debugLog;

    internal static Color OwnLineColor =>
        new(_lineR.Value, _lineG.Value, _lineB.Value, 1f);

    internal static void SetOwnLineColor(Color color)
    {
        _lineR.Value = color.r;
        _lineG.Value = color.g;
        _lineB.Value = color.b;
    }

    internal static bool ShowPerfectDots => _showPerfectDots.Value;
    internal static bool DebugLog => _debugLog.Value;

    internal static Rect WindowRect
    {
        get => new(_windowX.Value, _windowY.Value, _windowW.Value, _windowH.Value);
        set
        {
            _windowX.Value = value.x;
            _windowY.Value = value.y;
            _windowW.Value = value.width;
            _windowH.Value = value.height;
        }
    }

    internal static void Save()
    {
        try
        {
            MelonPreferences.Save();
        }
        catch
        {
            // ignore persistence failures
        }
    }

    internal static void Register()
    {
        var category = MelonPreferences.CreateCategory(Name, "AccLineChart");
        category.SetFilePath(ConfigPath);

        _lineR = category.CreateEntry("LineColorR", 0.25f, "Own line color R");
        _lineG = category.CreateEntry("LineColorG", 0.85f, "Own line color G");
        _lineB = category.CreateEntry("LineColorB", 1f, "Own line color B");
        _windowX = category.CreateEntry("WindowX", 120f, "Window X");
        _windowY = category.CreateEntry("WindowY", 90f, "Window Y");
        _windowW = category.CreateEntry("WindowW", 720f, "Window width");
        _windowH = category.CreateEntry("WindowH", 380f, "Window height");
        _showPerfectDots = category.CreateEntry("ShowPerfectDots", false, "Mark Perfect notes with small dots");
        _debugLog = category.CreateEntry("DebugLog", false, "Write debug lines to the MelonLoader log");
    }
}
