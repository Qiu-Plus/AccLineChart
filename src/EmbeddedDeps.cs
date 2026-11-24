using System.Reflection;

namespace AccLineChart;

/// <summary>
/// Single-file release support: MelonLoader loads the mod DLL on its own, while the
/// pure-managed MinHook.NET dependency (used by EuterpeMpTap to read multiplayer
/// frames in-process) is embedded as a resource and loaded into the AppDomain here.
/// </summary>
internal static class EmbeddedDeps
{
    internal const string MinHookResource = "AccLineChart.Resources.MinHook.NET.dll";

    private static bool _loaded;

    internal static void EnsureLoaded()
    {
        lock (typeof(EmbeddedDeps))
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loaded.GetName().Name == "MinHook.NET")
                    {
                        return; // already provided by UserLibs; keep that one
                    }
                }

                var assembly = typeof(EmbeddedDeps).Assembly;
                using var stream = assembly.GetManifestResourceStream(MinHookResource);
                if (stream == null || stream.Length <= 0)
                {
                    Logger.Warning("[deps] embedded MinHook.NET not found; multiplayer tap disabled");
                    return;
                }

                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var chunk = stream.Read(bytes, read, bytes.Length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }

                Assembly.Load(bytes);
                Logger.Msg("[deps] embedded MinHook.NET loaded");
            }
            catch (Exception ex)
            {
                Logger.Warning("[deps] embedded MinHook.NET load failed: " + ex.Message);
            }
        }
    }
}
