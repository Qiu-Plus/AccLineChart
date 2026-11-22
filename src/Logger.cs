namespace AccLineChart;

internal static class Logger
{
    internal static bool IsDebug => SettingManager.DebugLog;

    internal static void Msg(string message) => MelonLogger.Msg(message);

    internal static void Warning(string message) => MelonLogger.Warning(message);
}
