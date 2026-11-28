using HarmonyLib;

namespace AccLineChart;

/// <summary>
/// Muse Dash's menus read their scroll/navigation axes through InputManager (backed by Rewired),
/// which bypasses both uGUI raycasting and UnityEngine.Input — that is why the song carousel kept
/// scrolling under the chart window. Returning 0 for UI axes while the pointer is over our window
/// stops the game's lists from reacting, while everything outside the window stays untouched.
/// </summary>
internal static class GameUiInputGuard
{
    [HarmonyPatch(typeof(Il2CppAssets.Scripts.PeroTools.Managers.InputManager), "GetAxisFromUI")]
    internal static class GetAxisFromUiPatch
    {
        private static bool Prefix(ref float __result)
        {
            if (!ChartWindow.PointerOverWindow)
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(Il2CppAssets.Scripts.PeroTools.Managers.InputManager), "RewiredGetAxisRaw")]
    internal static class RewiredGetAxisRawPatch
    {
        private static bool Prefix(ref float __result)
        {
            if (!ChartWindow.PointerOverWindow)
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }
}
