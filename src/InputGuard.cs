using HarmonyLib;
using UnityEngine;

namespace AccLineChart;

/// <summary>
/// Swallows the game's mouse wheel while the pointer is over the chart window.
///
/// The transparent uGUI blocker stops clicks/drags from reaching the game, but Muse Dash's
/// menus also scroll on raw mouse-wheel input (it reads <see cref="Input.mouseScrollDelta"/>
/// somewhere below uGUI), so the wheel still scrolled the song list behind the window.
/// Patching the getter is the narrowest hook: only the wheel delta is affected, and only while
/// the pointer is over our window.
/// </summary>
internal static class InputGuard
{
    internal static bool SuppressScroll;

    [HarmonyPatch(typeof(Input), nameof(Input.mouseScrollDelta), MethodType.Getter)]
    internal static class MouseScrollDeltaPatch
    {
        private static bool Prefix(ref Vector2 __result)
        {
            if (!SuppressScroll)
            {
                return true;
            }

            __result = Vector2.zero;
            return false;
        }
    }
}
