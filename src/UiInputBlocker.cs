using UnityEngine;
using UnityEngine.UI;

namespace AccLineChart;

/// <summary>
/// Blocks the game's uGUI input underneath the chart window.
///
/// The window itself is drawn with IMGUI (MelonLoader's OnGUI), which never takes part in the
/// game's uGUI event system — so dragging the window used to also drag the song list behind it,
/// clicking the player list could select songs, etc. This creates a transparent, raycast-target
/// uGUI image on a high-sorting canvas that covers exactly the chart window, so the game's
/// EventSystem treats the pointer as "over" that image and never forwards clicks/drags/scroll
/// to the buttons underneath.
/// </summary>
internal static class UiInputBlocker
{
    private static GameObject? _root;
    private static RectTransform? _rect;
    private static bool _failed;
    private static float _releaseAt = -1f;

    internal static void SetBlockedRect(Rect guiRect)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            if (_root == null)
            {
                Build();
            }

            if (_root == null || _rect == null)
            {
                return;
            }

            bool active = guiRect.width > 1f && guiRect.height > 1f;
            if (_root.activeSelf != active)
            {
                _root.SetActive(active);
            }

            if (!active)
            {
                return;
            }

            // IMGUI uses top-left origin while a Screen Space Overlay canvas uses bottom-left.
            _rect.anchoredPosition = new Vector2(guiRect.x, Screen.height - guiRect.yMax);
            _rect.sizeDelta = new Vector2(guiRect.width, guiRect.height);
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Warning("[ui] input blocker failed: " + ex.Message);
        }
    }

    /// <summary>Keeps blocking for a moment after the window closes so the closing click's
    /// mouse-up cannot land on a game button that was underneath.</summary>
    internal static void ReleaseAfter(float seconds)
    {
        _releaseAt = Time.time + seconds;
    }

    internal static void Tick()
    {
        if (_releaseAt < 0f)
        {
            return;
        }

        if (Time.time < _releaseAt)
        {
            return;
        }

        _releaseAt = -1f;
        Hide();
    }

    internal static void Hide()
    {
        try
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void Build()
    {
        _root = new GameObject("AccLineChart_InputBlocker");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760; // above the game's UI canvases

        _root.AddComponent<CanvasScaler>();
        _root.AddComponent<GraphicRaycaster>();

        var imageGo = new GameObject("Blocker");
        imageGo.transform.SetParent(_root.transform, false);
        var image = imageGo.AddComponent<Image>();
        // Nearly invisible but not fully transparent: guarantees the raycaster still hits it.
        image.color = new Color(0f, 0f, 0f, 0.004f);
        image.raycastTarget = true;

        _rect = imageGo.GetComponent<RectTransform>();
        _rect.anchorMin = Vector2.zero;
        _rect.anchorMax = Vector2.zero;
        _rect.pivot = Vector2.zero;
        _rect.anchoredPosition = Vector2.zero;
        _rect.sizeDelta = Vector2.zero;

        _root.SetActive(false);
        Logger.Msg("[ui] input blocker created");
    }
}
