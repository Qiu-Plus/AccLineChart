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
    private static MelonPreferences_Entry<bool> _multiplayerTap;
    private static MelonPreferences_Entry<string> _hexColor;
    private static MelonPreferences_Entry<bool> _multiplayerLive;
    private static MelonPreferences_Entry<bool> _mpNetworkDiagnostics;

    internal static Color OwnLineColor =>
        TryParseHex(_hexColor.Value, out var hex) ? hex : new Color(_lineR.Value, _lineG.Value, _lineB.Value, 1f);

    /// <summary>Current line color as #RRGGBB (used by the hex input field).</summary>
    internal static string OwnLineColorHex => _hexColor.Value;

    internal static void SetOwnLineColor(Color color)
    {
        _lineR.Value = color.r;
        _lineG.Value = color.g;
        _lineB.Value = color.b;
        _hexColor.Value = ToHex(color);
    }

    /// <summary>Applies a user typed hex color ("#RRGGBB", "RRGGBB", "#RGB", "RGB").</summary>
    internal static bool TrySetOwnLineColorHex(string text)
    {
        if (!TryParseHex(text, out var color)) return false;
        _lineR.Value = color.r;
        _lineG.Value = color.g;
        _lineB.Value = color.b;
        _hexColor.Value = ToHex(color);
        return true;
    }

    internal static bool TryParseHex(string? text, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length != 6) return false;
        if (!byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(s[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = new Color(r / 255f, g / 255f, b / 255f, 1f);
        return true;
    }

    internal static string ToHex(Color color) =>
        $"#{Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f):X2}" +
        $"{Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f):X2}" +
        $"{Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f):X2}";

    internal static bool ShowPerfectDots => _showPerfectDots.Value;
    internal static bool DebugLog => _debugLog.Value;

    /// <summary>Read Euterpe multiplayer frames in-process (no extra connection; no disconnect).</summary>
    internal static bool MultiplayerTap => _multiplayerTap.Value;

    /// <summary>Use an Euterpe web-style spectator connection to read every player's live accuracy.</summary>
    internal static bool MultiplayerLive => _multiplayerLive.Value;

    /// <summary>Log outgoing WebSocket frames / WinHTTP requests (diagnostics; noisy).</summary>
    internal static bool MpNetworkDiagnostics => _mpNetworkDiagnostics.Value;

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
        _multiplayerTap = category.CreateEntry("MultiplayerTap", true,
            "Read Euterpe multiplayer frames in-process (read-only; no extra connection, no disconnect)");
        _hexColor = category.CreateEntry("LineColorHex", "#40D9FF", "Own line color as hex (#RRGGBB or RRGGBB)");
        _multiplayerLive = category.CreateEntry("MultiplayerLive", false,
            "EXPERIMENTAL: extra website-style spectator connection. Can make the game reconnect; keep off unless testing");
        _mpNetworkDiagnostics = category.CreateEntry("MpNetworkDiagnostics", false,
            "Log outgoing Euterpe WebSocket frames and WinHTTP requests (diagnostics; noisy)");
    }
}
