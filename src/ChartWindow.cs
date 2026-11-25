namespace AccLineChart;

internal static class ChartWindow
{
    private const float TitleHeight = 38f;
    private const float HintHeight = 30f;
    private const float SidebarWidth = 196f;
    private const float WindowRadius = 10f;
    private const float CardRadius = 8f;
    private const float GripSize = 18f;

    private static readonly Color WindowColor = new(0.070f, 0.080f, 0.115f, 0.955f);
    private static readonly Color TitleColor = new(0.095f, 0.108f, 0.152f, 0.985f);
    private static readonly Color CardColor = new(0.098f, 0.112f, 0.156f, 0.96f);
    private static readonly Color CardColorAlt = new(0.082f, 0.094f, 0.132f, 0.96f);
    private static readonly Color BorderColor = new(0.235f, 0.27f, 0.36f, 0.85f);
    private static readonly Color Accent = new(0.36f, 0.84f, 0.98f, 1f);
    private static readonly Color AccentDim = new(0.36f, 0.84f, 0.98f, 0.18f);
    private static readonly Color TextColor = new(0.90f, 0.93f, 0.98f, 1f);
    private static readonly Color TextDim = new(0.58f, 0.64f, 0.75f, 1f);
    private static readonly Color GridColor = new(1f, 1f, 1f, 0.06f);
    private static readonly Color AxisColor = new(1f, 1f, 1f, 0.14f);
    private static readonly Color GreatColor = new(1f, 0.80f, 0.18f, 1f);
    private static readonly Color MissColor = new(1f, 0.29f, 0.32f, 1f);
    private static readonly Color ShadowColor = new(0f, 0f, 0f, 0.30f);

    private static bool _visible;
    private static bool _initialized;
    private static Rect _rect;

    private static bool _draggingMove;
    private static bool _draggingResize;
    private static Vector2 _dragOffset;
    private static Vector2 _resizeStartMouse;
    private static Vector2 _resizeStartSize;

    private static int _activeSlider = -1;
    private static readonly Rect[] _sliderTracks = new Rect[3];
    private static Rect _titleBarRect;
    private static Rect _closeRect;
    private static Rect _gripRect;

    private static GUIStyle _titleStyle;
    private static GUIStyle _subtitleStyle;
    private static GUIStyle _sectionStyle;
    private static GUIStyle _textStyle;
    private static GUIStyle _smallStyle;
    private static GUIStyle _centerStyle;
    private static GUIStyle _rightStyle;
    private static GUIStyle _badgeStyle;
    private static Font _font;

    private static bool _loggedLayout;
    private static bool _stylesReady;
    private static bool _stylesDirty;
    private static bool _prewarmRequested;

    private static long _drawTicks;
    private static int _perfCount;
    private static double _perfSum;
    private static double _perfMax;

    private static readonly List<Vector2> LinePoints = new(2048);

    // Multiplayer series + hex color input
    private static readonly List<PlayerSeries> _allSeries = new(64);
    private static Rect _hexRect;
    private static bool _hexActive;
    private static string _hexBuffer = "";

    // Multiplayer view mode: all players vs one selected player (F8 / legend click).
    private static bool _viewAll = true;
    private static int _focusedUid;
    private static int _hoverUid;
    private static readonly List<(Rect Rect, int Uid)> _legendRows = new(16);
    private static Rect _legendArea;
    private static float _legendScroll;
    private static float _legendContentHeight;
    private static float _legendViewHeight;

    private static Texture2D White => Texture2D.whiteTexture;

    internal static bool Visible => _visible;

    private static long _lastShowTicks;

    /// <summary>Ask for the styles/font to be resolved again (e.g. once the game fonts are loaded).</summary>
    internal static void RequestPrewarm()
    {
        if (!_visible)
        {
            _prewarmRequested = true;
            _stylesDirty = true;
        }
    }

    internal static void ShowResults()
    {
        try
        {
            // PnlVictory.OnVictory is raised twice on some game versions.
            long now = Environment.TickCount64;
            if (now - _lastShowTicks < 750)
            {
                return;
            }

            _lastShowTicks = now;
            EnsureInitialized();
            if (!_visible)
            {
                _rect = SettingManager.WindowRect;
            }

            _visible = true;
            _multiplayerMode = false;
            Logger.Msg($"[ChartWindow] results shown, samples={RunRecorder.Self?.Samples.Count ?? 0}, totalNotes={RunRecorder.TotalUnits}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ChartWindow] ShowResults failed: {ex}");
        }
    }

    internal static void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        _draggingMove = false;
        _draggingResize = false;
        _activeSlider = -1;
        SettingManager.WindowRect = _rect;
        SettingManager.Save();
    }

    internal static void ToggleDebug()
    {
        if (_visible)
        {
            Hide();
            Logger.Msg("[ChartWindow] preview hidden (F9)");
            return;
        }

        Logger.Msg("[ChartWindow] preview shown (F9)");

        // Never replace real multiplayer results with synthetic debug samples.
        if (!MultiplayerRecorder.HasRoundData)
        {
            RunRecorder.FillDebugData();
        }
        else
        {
            Logger.Msg("[ChartWindow] debug fill skipped: multiplayer data present");
        }

        EnsureInitialized();
        _rect = SettingManager.WindowRect;
        _visible = true;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        Rect saved = SettingManager.WindowRect;
        if (saved.width < 420f)
        {
            saved.width = 420f;
        }

        if (saved.height < 260f)
        {
            saved.height = 260f;
        }

        _rect = saved;
    }

    internal static void OnGUI()
    {
        try
        {
            // Build the styles during the very first IMGUI event (usually the main menu), not when
            // the results screen opens: resolving the CJK font used to cause a one-time hitch there.
            if (_prewarmRequested && !_visible)
            {
                if (_stylesDirty)
                {
                    // The first pass may run before the game fonts are loaded (OS font fallback);
                    // rebuild once when entering a real scene so the game font is picked up.
                    _titleStyle = null;
                }

                long styleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                EnsureInitialized();
                EnsureStyles();
                _stylesReady = _titleStyle != null;
                _stylesDirty = false;
                _prewarmRequested = false;
                double styleMs = (System.Diagnostics.Stopwatch.GetTimestamp() - styleStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Logger.Msg($"[ChartWindow] styles ready in {styleMs:0.0}ms");
            }

            if (!_stylesReady && _visible)
            {
                EnsureInitialized();
                EnsureStyles();
                _stylesReady = _titleStyle != null;
            }

            if (!_visible)
            {
                return;
            }

            var e = Event.current;
            HandleInput(e);

            // Everything below only draws: skip it for layout/mouse/input events.
            if (e.type != EventType.Repaint)
            {
                return;
            }

            _drawTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Draw();
            PerfSample((System.Diagnostics.Stopwatch.GetTimestamp() - _drawTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ChartWindow] OnGUI failed: {ex}");
            _visible = false;
        }
    }

    private static void PerfSample(double ms)
    {
        if (!Logger.IsDebug)
        {
            return;
        }

        _perfCount++;
        _perfSum += ms;
        if (ms > _perfMax)
        {
            _perfMax = ms;
        }

        if (_perfCount % 120 == 0)
        {
            Logger.Msg($"[ChartWindow] draw avg={_perfSum / 120.0:0.00}ms max={_perfMax:0.00}ms");
            _perfSum = 0;
            _perfMax = 0;
        }
    }

    #region resources

    private static void EnsureStyles()
    {
        if (_titleStyle != null)
        {
            return;
        }

        _font = ResolveFont();

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextColor },
        };

        _subtitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextDim },
        };

        _sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextColor },
        };

        _textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextColor },
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextDim },
        };

        _centerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextDim },
        };

        _rightStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleRight,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextColor },
        };

        _badgeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Overflow,
            normal = { textColor = TextColor },
        };

        if (_font == null)
        {
            return;
        }

        foreach (var style in new[] { _titleStyle, _subtitleStyle, _sectionStyle, _textStyle, _smallStyle, _centerStyle, _rightStyle, _badgeStyle })
        {
            style.font = _font;
        }
    }

    private static Font ResolveFont()
    {
        // The game ships several Source Han Sans faces; prefer the Chinese, non-bold one.
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            Font best = null;
            int bestScore = int.MinValue;
            foreach (var font in fonts)
            {
                if (font == null || string.IsNullOrEmpty(font.name))
                {
                    continue;
                }

                string name = font.name;
                int score = 0;
                if (name.Contains("SourceHanSans") || name.Contains("Source Han Sans"))
                {
                    score += 4;
                    if (name.Contains("CN") || name.Contains("SC") || name.Contains("Chinese"))
                    {
                        score += 4;
                    }

                    if (name.Contains("Normal") || name.Contains("Regular") || name.Contains("Medium"))
                    {
                        score += 3;
                    }
                    else if (name.Contains("Bold") || name.Contains("Heavy"))
                    {
                        score -= 2;
                    }
                }
                else if (name.Contains("YaHei") || name.Contains("SimHei") || name.Contains("Noto Sans"))
                {
                    score += 3;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = font;
                }
            }

            if (best != null && bestScore > 0)
            {
                Logger.Msg($"[ChartWindow] font: {best.name}");
                return best;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ChartWindow] Resources font lookup failed: {ex.Message}");
        }

        string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Segoe UI", "Arial" };
        foreach (var name in candidates)
        {
            try
            {
                var font = Font.CreateDynamicFontFromOSFont(name, 20);
                if (font != null)
                {
                    Logger.Msg($"[ChartWindow] font: os '{name}'");
                    return font;
                }
            }
            catch
            {
                // try next candidate
            }
        }

        try
        {
            var texts = UnityEngine.Object.FindObjectsOfType<Text>();
            foreach (var text in texts)
            {
                if (text != null && text.font != null)
                {
                    Logger.Msg($"[ChartWindow] font: scene '{text.font.name}'");
                    return text.font;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    #endregion

    #region input

    private static void HandleInput(Event e)
    {
        Vector2 mouse = e.mousePosition;

        switch (e.type)
        {
            case EventType.ScrollWheel:
                if (_legendArea.width > 0f && _legendArea.Contains(mouse) &&
                    _legendContentHeight > _legendViewHeight)
                {
                    var max = Mathf.Max(0f, _legendContentHeight - _legendViewHeight);
                    _legendScroll = Mathf.Clamp(_legendScroll + e.delta.y * 28f, 0f, max);
                    e.Use();
                }

                break;

            case EventType.MouseDown when e.button == 0:
                if (_hexRect.width > 0f && _hexRect.Contains(mouse))
                {
                    BeginHexEdit();
                    e.Use();
                    return;
                }

                if (_hexActive && !_hexRect.Contains(mouse))
                {
                    CommitHexEdit(true);
                }

                foreach (var (legendRect, legendUid) in _legendRows)
                {
                    if (legendRect.width > 0f && legendRect.Contains(mouse))
                    {
                        _focusedUid = legendUid;
                        _viewAll = false;
                        e.Use();
                        return;
                    }
                }

                if (_closeRect.Contains(mouse))
                {
                    Hide();
                    e.Use();
                    return;
                }

                for (int i = 0; i < _sliderTracks.Length; i++)
                {
                    if (_sliderTracks[i].width > 0f && _sliderTracks[i].Contains(mouse))
                    {
                        _activeSlider = i;
                        ApplySliderValue(i, mouse.x);
                        e.Use();
                        return;
                    }
                }

                if (_titleBarRect.Contains(mouse))
                {
                    _draggingMove = true;
                    _dragOffset = mouse - _rect.position;
                    e.Use();
                    return;
                }

                if (_gripRect.Contains(mouse))
                {
                    _draggingResize = true;
                    _resizeStartMouse = mouse;
                    _resizeStartSize = _rect.size;
                    e.Use();
                }

                break;

            case EventType.KeyDown when _hexActive:
                HandleHexKey(e);
                e.Use();
                return;

            case EventType.MouseDrag when e.button == 0:
                if (_draggingMove)
                {
                    Vector2 position = mouse - _dragOffset;
                    position.x = Math.Clamp(position.x, -_rect.width + 80f, Screen.width - 80f);
                    position.y = Math.Clamp(position.y, 0f, Screen.height - TitleHeight - 10f);
                    _rect.position = position;
                    e.Use();
                }
                else if (_draggingResize)
                {
                    Vector2 size = _resizeStartSize + (mouse - _resizeStartMouse);
                    size.x = Math.Clamp(size.x, 480f, Math.Max(480f, Screen.width - 20f));
                    size.y = Math.Clamp(size.y, 280f, Math.Max(280f, Screen.height - 20f));
                    _rect.size = size;
                    e.Use();
                }
                else if (_activeSlider >= 0)
                {
                    ApplySliderValue(_activeSlider, mouse.x);
                    e.Use();
                }

                break;

            case EventType.MouseUp when e.button == 0:
                if (_draggingMove || _draggingResize)
                {
                    _draggingMove = false;
                    _draggingResize = false;
                    SettingManager.WindowRect = _rect;
                    SettingManager.Save();
                    e.Use();
                }
                else if (_activeSlider >= 0)
                {
                    _activeSlider = -1;
                    SettingManager.Save();
                    e.Use();
                }

                break;
        }
    }

    private static void ApplySliderValue(int index, float mouseX)
    {
        Rect track = _sliderTracks[index];
        if (track.width <= 1f || RunRecorder.Self == null)
        {
            return;
        }

        float value = Math.Clamp((mouseX - track.x) / track.width, 0f, 1f);
        var color = RunRecorder.Self.Color;
        switch (index)
        {
            case 0:
                color.r = value;
                break;
            case 1:
                color.g = value;
                break;
            case 2:
                color.b = value;
                break;
        }

        RunRecorder.Self.Color = color;
        SettingManager.SetOwnLineColor(color);
    }

    /// <summary>Local run series + multiplayer players from the in-process Euterpe tap.</summary>
    private static IReadOnlyList<PlayerSeries> CollectSeries()
    {
        _allSeries.Clear();
        var self = RunRecorder.Self;
        if (self != null && MultiplayerRecorder.SelfUid != 0)
        {
            self.Uid = MultiplayerRecorder.SelfUid;
            if (!string.IsNullOrEmpty(MultiplayerRecorder.SelfNick))
            {
                self.Name = MultiplayerRecorder.SelfNick;
            }
        }

        foreach (var own in RunRecorder.Series)
        {
            if (own.Samples.Count > 0) _allSeries.Add(own);
        }

        // Only the multiplayer results panel may show other players; the single-player
        // results screen used to keep showing the last room's players.
        if (_multiplayerMode)
        {
            _allSeries.AddRange(MultiplayerRecorder.Series);
        }

        return _allSeries;
    }

    /// <summary>Room left / new battle: leave multiplayer view so stale players disappear.</summary>
    internal static void LeaveMultiplayerView()
    {
        _multiplayerMode = false;
        if (_visible)
        {
            Hide();
        }
    }

    /// <summary>F8: switch between "all players in one panel" and "only the selected player".</summary>
    internal static void ToggleAllPlayers()
    {
        if (!_visible)
        {
            ShowMultiplayer();
            _viewAll = true;
            return;
        }

        _viewAll = !_viewAll;
        if (!_viewAll)
        {
            var series = CollectSeries();
            if (series.Count > 0 && series.All(p => p.Uid != _focusedUid))
            {
                _focusedUid = series[0].Uid;
            }
        }

        Logger.Msg($"[ChartWindow] view all={_viewAll} focusedUid={_focusedUid}");
    }

    /// <summary>Show the chart with multiplayer results (used when a multiplayer round ends).</summary>
    internal static void ShowMultiplayer()
    {
        try
        {
            EnsureInitialized();
            if (!_visible)
            {
                _rect = SettingManager.WindowRect;
            }

            _multiplayerMode = true;
            var series = CollectSeries();
            if (series.Count == 0)
            {
                // e.g. the room was left right before the auto-show fired: nothing to present.
                _multiplayerMode = false;
                Hide();
                Logger.Msg("[ChartWindow] multiplayer panel skipped (no data)");
                return;
            }

            _visible = true;
            Logger.Msg($"[ChartWindow] multiplayer results shown (series={series.Count})");
            foreach (var p in series)
            {
                Logger.Msg($"[ChartWindow]   series uid={p.Uid} name={p.Name} self={p.IsSelf} approx={p.IsApproximate} " +
                           $"samples={p.Samples.Count} final={p.FinalAcc:0.00}" +
                           (p.Samples.Count > 0 ? $" t=[{p.Samples[0].Time:0.0}..{p.Samples[^1].Time:0.0}]" : ""));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ChartWindow] ShowMultiplayer failed: {ex}");
        }
    }

    private static void BeginHexEdit()
    {
        _hexActive = true;
        _hexBuffer = SettingManager.OwnLineColorHex;
    }

    private static void HandleHexKey(Event e)
    {
        switch (e.keyCode)
        {
            case KeyCode.Backspace:
                if (_hexBuffer.Length > 0)
                {
                    _hexBuffer = _hexBuffer[..^1];
                }
                return;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                CommitHexEdit(true);
                return;
            case KeyCode.Escape:
                _hexActive = false;
                _hexBuffer = SettingManager.OwnLineColorHex;
                return;
        }

        char c = e.character;
        bool valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '#';
        if (!valid || _hexBuffer.Length >= 7)
        {
            return;
        }

        _hexBuffer += c;
    }

    private static void CommitHexEdit(bool save)
    {
        if (!_hexActive)
        {
            return;
        }

        _hexActive = false;
        if (SettingManager.TrySetOwnLineColorHex(_hexBuffer))
        {
            if (RunRecorder.Self != null)
            {
                RunRecorder.Self.Color = SettingManager.OwnLineColor;
            }

            Logger.Msg($"[ChartWindow] line color -> {SettingManager.OwnLineColorHex}");
        }
        else
        {
            Logger.Msg($"[ChartWindow] invalid hex '{_hexBuffer}', kept {SettingManager.OwnLineColorHex}");
        }

        _hexBuffer = SettingManager.OwnLineColorHex;
        if (save)
        {
            SettingManager.Save();
        }
    }

    #endregion

    #region drawing

    private static void Draw()
    {
        if (!_loggedLayout)
        {
            _loggedLayout = true;
            try
            {
                Logger.Msg($"[ChartWindow] layout screen={Screen.width}x{Screen.height} guiScale={GUI.matrix.m00:0.###},{GUI.matrix.m11:0.###} rect={_rect}");
            }
            catch
            {
                // ignore
            }
        }

        // Shadow
        DrawRounded(new Rect(_rect.x - 3f, _rect.y - 1f, _rect.width + 6f, _rect.height + 8f), ShadowColor, WindowRadius + 2f);

        // Window
        DrawRounded(_rect, WindowColor, WindowRadius);
        DrawRoundedBorder(_rect, WindowRadius, BorderColor);

        // Title bar
        _titleBarRect = new Rect(_rect.x, _rect.y, _rect.width, TitleHeight);
        var titleClip = new Rect(_rect.x, _rect.y, _rect.width, TitleHeight);
        DrawRounded(titleClip, TitleColor, WindowRadius);
        DrawRect(new Rect(_rect.x + 1f, _rect.y + WindowRadius, _rect.width - 2f, TitleHeight - WindowRadius), TitleColor);

        // Accent bar + title text
        DrawRect(new Rect(_rect.x + 12f, _rect.y + 11f, 3f, 16f), Accent);
        GUI.Label(new Rect(_rect.x + 22f, _rect.y + 1f, 240f, 25f), "准度曲线", _titleStyle);

        string subtitle = string.IsNullOrEmpty(RunRecorder.ChartName) ? "AccLineChart" : RunRecorder.ChartName;
        GUI.Label(new Rect(_rect.x + 23f, _rect.y + 23f, _rect.width - 100f, 18f), subtitle, _subtitleStyle);

        // Close button
        _closeRect = new Rect(_rect.xMax - 32f, _rect.y + 9f, 22f, 20f);
        bool closeHover = _closeRect.Contains(Event.current.mousePosition);
        DrawCircle(new Vector2(_closeRect.center.x, _closeRect.center.y), 10f,
            closeHover ? new Color(0.85f, 0.30f, 0.33f, 1f) : new Color(0.20f, 0.23f, 0.30f, 1f));
        GUI.Label(_closeRect, "X", _badgeStyle);

        float bodyTop = _rect.y + TitleHeight;
        float hintTop = _rect.yMax - HintHeight;
        float bodyHeight = hintTop - bodyTop - 8f;
        if (bodyHeight < 120f)
        {
            return;
        }

        var chartCard = new Rect(_rect.x + 10f, bodyTop + 6f, _rect.width - SidebarWidth - 32f, bodyHeight);
        var sideCard = new Rect(_rect.xMax - SidebarWidth - 12f, bodyTop + 6f, SidebarWidth, bodyHeight);

        DrawRounded(chartCard, CardColor, CardRadius);
        DrawRoundedBorder(chartCard, CardRadius, new Color(0.20f, 0.24f, 0.32f, 0.8f));
        DrawRounded(sideCard, CardColorAlt, CardRadius);
        DrawRoundedBorder(sideCard, CardRadius, new Color(0.20f, 0.24f, 0.32f, 0.8f));

        DrawChart(chartCard);
        DrawSidebar(sideCard);

        // Hint bar
        GUI.Label(new Rect(_rect.x + 14f, hintTop + 7f, _rect.width - 130f, HintHeight - 12f),
            "拖动标题栏移动 · 右下角缩放 · 点 X 关闭 · 点玩家切换 · F8 全部/单人", _smallStyle);

        // Resize grip
        _gripRect = new Rect(_rect.xMax - GripSize, _rect.yMax - GripSize, GripSize, GripSize);
        var gripColor = new Color(TextDim.r, TextDim.g, TextDim.b, 0.65f);
        DrawRect(new Rect(_gripRect.x + 5f, _gripRect.yMax - 5f, 9f, 1f), gripColor);
        DrawRect(new Rect(_gripRect.x + 9f, _gripRect.yMax - 9f, 5f, 1f), gripColor);
    }

    private static void DrawChart(Rect card)
    {
        float left = 76f;
        float top = 34f;
        float right = 12f;
        float bottom = 42f;
        var plot = new Rect(card.x + left, card.y + top, card.width - left - right, card.height - top - bottom);
        if (plot.width < 60f || plot.height < 50f)
        {
            return;
        }

        IReadOnlyList<PlayerSeries> series = CollectSeries();
        if (!_viewAll)
        {
            var focused = series.Where(p => p.Uid == _focusedUid).ToList();
            if (focused.Count > 0 && focused.Any(p => p.Samples.Count > 0))
            {
                series = focused;
            }
        }

        bool hasSamples = false;
        float minAcc = 100f;
        float maxTime = 1f;

        foreach (var player in series)
        {
            foreach (var sample in player.Samples)
            {
                hasSamples = true;
                minAcc = Math.Min(minAcc, sample.Acc);
                maxTime = Math.Max(maxTime, sample.Time);
            }
        }

        if (!hasSamples)
        {
            GUI.Label(plot, "这一局还没有判定数据", _centerStyle);
            return;
        }

        maxTime = Mathf.Max(1f, maxTime * 1.05f);

        float yMax = 100f;
        float yMin = Mathf.Max(0f, minAcc - 0.35f);
        float step = NiceStep(yMax - yMin, 4);
        yMin = Mathf.Max(0f, Mathf.Floor(yMin / step) * step);
        if (yMax - yMin < step * 1.5f)
        {
            yMin = Mathf.Max(0f, yMax - step * 2f);
        }

        int rowCount = Mathf.Max(2, Mathf.RoundToInt((yMax - yMin) / step));
        while (rowCount > 7)
        {
            step *= 2f;
            rowCount = Mathf.Max(2, Mathf.RoundToInt((yMax - yMin) / step));
        }

        // 100% baseline
        DrawLine(new Vector2(plot.x, plot.y), new Vector2(plot.xMax, plot.y), new Color(0.55f, 0.85f, 0.65f, 0.30f), 1f);

        // Grid and y labels
        for (int i = 0; i <= rowCount; i++)
        {
            float acc = yMin + step * i;
            if (acc > yMax + 0.001f)
            {
                break;
            }

            float y = MapY(acc, plot, yMin, yMax);
            bool isEdge = i == 0 || i == rowCount;
            if (!isEdge)
            {
                DrawRect(new Rect(plot.x, y, plot.width, 1f), GridColor);
            }

            GUI.Label(new Rect(card.x + 8f, y - 10f, left - 20f, 20f), $"{acc:0.00}%", _rightStyle);
        }

        // Vertical grid and time labels
        const int cols = 5;
        for (int i = 0; i <= cols; i++)
        {
            float x = plot.x + plot.width * i / cols;
            if (i > 0 && i < cols)
            {
                DrawRect(new Rect(x, plot.y, 1f, plot.height), GridColor);
            }

            float seconds = maxTime * i / cols;
            GUI.Label(new Rect(x - 32f, plot.yMax + 6f, 64f, 18f), FormatAxisTime(seconds, maxTime), _smallStyle);
        }

        GUI.Label(new Rect(plot.xMax - 28f, plot.yMax + 25f, 28f, 16f), "时间", _smallStyle);
        GUI.Label(new Rect(card.x + 10f, card.y + 5f, 72f, 18f), "准度 (%)", _smallStyle);

        DrawPlotLines(series, plot, yMin, yMax, maxTime);

        DrawEndLabels(series, plot, yMin, yMax, maxTime);
    }

    // ---------------------------------------------------------------------
    // Plot lines are rasterised into a cached Texture2D instead of issuing
    // thousands of GUI rotated-texture draws every frame (that was the cause
    // of the frame drops with many multiplayer players).
    // ---------------------------------------------------------------------

    private static Texture2D? _plotTexture;
    private static Color32[]? _plotPixels;
    private static int _plotTextureW;
    private static int _plotTextureH;
    private static long _plotSignature = long.MinValue;
    private static bool _multiplayerMode;

    private static long ComputePlotSignature(IReadOnlyList<PlayerSeries> series, int w, int h, float yMin, float yMax, float maxTime)
    {
        var hash = 1469598103934665603UL;
        void Mix(long v)
        {
            hash ^= (ulong)v;
            hash *= 1099511628211UL;
        }

        Mix(w);
        Mix(h);
        Mix(BitConverter.SingleToInt32Bits(yMin));
        Mix(BitConverter.SingleToInt32Bits(yMax));
        Mix(BitConverter.SingleToInt32Bits(maxTime));
        foreach (var player in series)
        {
            Mix(player.Uid);
            Mix(player.Samples.Count);
            Mix(player.IsApproximate ? 1 : 0);
            Mix((player.Color.r * 255f).GetHashCode() ^ (player.Color.g * 255f).GetHashCode() ^ (player.Color.b * 255f).GetHashCode());
            if (player.Samples.Count > 0)
            {
                var last = player.Samples[^1];
                Mix(BitConverter.SingleToInt32Bits(last.Time));
                Mix(BitConverter.SingleToInt32Bits(last.Acc));
            }
        }

        return unchecked((long)hash);
    }

    private static void DrawPlotLines(IReadOnlyList<PlayerSeries> series, Rect plot, float yMin, float yMax, float maxTime)
    {
        int w = Mathf.Max(2, Mathf.RoundToInt(plot.width));
        int h = Mathf.Max(2, Mathf.RoundToInt(plot.height));

        if (_plotTexture == null || _plotTextureW != w || _plotTextureH != h)
        {
            if (_plotTexture != null)
            {
                UnityEngine.Object.Destroy(_plotTexture);
            }

            _plotTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                // Scene changes call Resources.UnloadUnusedAssets(); runtime textures without this
                // flag get destroyed and the chart goes blank until the next rasterization.
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            _plotPixels = new Color32[w * h];
            _plotTextureW = w;
            _plotTextureH = h;
            _plotSignature = long.MinValue;
        }

        long signature = ComputePlotSignature(series, w, h, yMin, yMax, maxTime);
        if (signature != _plotSignature)
        {
            RasterizePlot(series, w, h, yMin, yMax, maxTime);
            _plotSignature = signature;
            if (Logger.IsDebug)
            {
                Logger.Msg($"[ChartWindow] plot rasterized {w}x{h} series={series.Count} y=[{yMin:0.00}..{yMax:0.00}] t={maxTime:0.0}");
            }
        }

        GUI.DrawTexture(new Rect(plot.x, plot.y, w, h), _plotTexture, ScaleMode.StretchToFill, true);
    }

    private static void RasterizePlot(IReadOnlyList<PlayerSeries> series, int w, int h, float yMin, float yMax, float maxTime)
    {
        var pixels = _plotPixels!;
        Array.Clear(pixels, 0, pixels.Length);
        float span = Mathf.Max(0.0001f, yMax - yMin);

        foreach (var player in series)
        {
            var samples = player.Samples;
            if (samples.Count == 0) continue;

            var core = (Color32)player.Color;
            core.a = 255;
            var glow = new Color32(core.r, core.g, core.b, 70);

            int prevX = -1;
            int prevY = -1;
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                int x = Mathf.Clamp(Mathf.RoundToInt(w * Mathf.Clamp(sample.Time / maxTime, 0f, 1f)), 0, w - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(h * (1f - Mathf.Clamp((sample.Acc - yMin) / span, 0f, 1f))), 0, h - 1);
                if (prevX >= 0)
                {
                    RasterLine(pixels, w, h, prevX, prevY, x, y, glow, 3);
                    RasterLine(pixels, w, h, prevX, prevY, x, y, core, player.IsSelf ? 2 : 1);
                }

                prevX = x;
                prevY = y;
            }

            if (player.IsSelf)
            {
                var great = (Color32)GreatColor;
                great.a = 255;
                var miss = (Color32)MissColor;
                miss.a = 255;
                for (var i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    if (!sample.IsGreat && !sample.IsMiss) continue;
                    int x = Mathf.Clamp(Mathf.RoundToInt(w * Mathf.Clamp(sample.Time / maxTime, 0f, 1f)), 0, w - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt(h * (1f - Mathf.Clamp((sample.Acc - yMin) / span, 0f, 1f))), 0, h - 1);
                    RasterLine(pixels, w, h, x - 1, y - 1, x + 1, y + 1, sample.IsGreat ? great : miss, 2);
                    RasterLine(pixels, w, h, x - 1, y + 1, x + 1, y - 1, sample.IsGreat ? great : miss, 2);
                }
            }
            else if (player.IsApproximate)
            {
                int y = Mathf.Clamp(Mathf.RoundToInt(h * (1f - Mathf.Clamp((player.FinalAcc - yMin) / span, 0f, 1f))), 0, h - 1);
                for (var x = 0; x < w; x += 8)
                {
                    for (var d = 0; d < 4 && x + d < w; d++)
                    {
                        RasterLine(pixels, w, h, x + d, y, x + d, y, core, 1);
                    }
                }
            }
        }

        _plotTexture!.SetPixels32(pixels);
        _plotTexture.Apply(false);
    }

    private static void RasterLine(Color32[] pixels, int w, int h, int x0, int y0, int x1, int y1, Color32 color, int thickness)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = -Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int half = Mathf.Max(0, thickness / 2);

        while (true)
        {
            for (var ox = -half; ox <= half; ox++)
            {
                for (var oy = -half; oy <= half; oy++)
                {
                    int px = x0 + ox;
                    int py = y0 + oy;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    // Texture2D row 0 is the BOTTOM row in Unity, while the plot maps y=0 to the top.
                    int index = (h - 1 - py) * w + px;
                    var dst = pixels[index];
                    if (color.a >= 255)
                    {
                        pixels[index] = color;
                    }
                    else if (dst.a < color.a)
                    {
                        // simple screen-space blend for the translucent glow pass
                        var a = color.a / 255f;
                        pixels[index] = new Color32(
                            (byte)(color.r * a + dst.r * (1 - a)),
                            (byte)(color.g * a + dst.g * (1 - a)),
                            (byte)(color.b * a + dst.b * (1 - a)),
                            (byte)Mathf.Min(255, dst.a + color.a));
                    }
                }
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Small avatar + nickname pills attached to the latest point of every curve, so the labels
    /// travel with the live lines instead of living only in the sidebar.
    /// </summary>
    private static void DrawEndLabels(IReadOnlyList<PlayerSeries> series, Rect plot, float yMin, float yMax, float maxTime)
    {
        if (plot.width < 220f || plot.height < 80f) return;

        var labels = new List<(float X, float Y, PlayerSeries Player)>(series.Count);
        foreach (var player in series)
        {
            if (player.Samples.Count == 0) continue;
            var last = player.Samples[^1];
            float x = plot.x + plot.width * Mathf.Clamp(last.Time / maxTime, 0f, 1f);
            float y = MapY(last.Acc, plot, yMin, yMax);
            labels.Add((x, y, player));
        }

        if (labels.Count == 0) return;

        const float labelHeight = 22f;
        const float minGap = 24f;
        labels.Sort((a, b) => a.Y.CompareTo(b.Y));
        for (var i = 1; i < labels.Count; i++)
        {
            if (labels[i].Y - labels[i - 1].Y < minGap)
                labels[i] = (labels[i].X, labels[i - 1].Y + minGap, labels[i].Player);
        }

        float overflow = labels[^1].Y - (plot.yMax - labelHeight);
        if (overflow > 0f)
        {
            for (var i = 0; i < labels.Count; i++)
                labels[i] = (labels[i].X, labels[i].Y - overflow, labels[i].Player);
        }

        foreach (var (px, py, player) in labels)
        {
            var size = _smallStyle.CalcSize(new GUIContent(player.Name));
            float width = 20f + 5f + Mathf.Min(size.x, 118f) + 12f;
            float x = px + 9f;
            if (x + width > plot.xMax - 2f) x = px - width - 9f;
            x = Mathf.Clamp(x, plot.x, Mathf.Max(plot.x, plot.xMax - width));
            float y = Mathf.Clamp(py - labelHeight / 2f, plot.y, Mathf.Max(plot.y, plot.yMax - labelHeight));

            var rect = new Rect(x, y, width, labelHeight);
            DrawRounded(rect, new Color(0.04f, 0.05f, 0.08f, 0.72f), 6f);
            DrawRoundedBorder(rect, 6f, new Color(player.Color.r, player.Color.g, player.Color.b, 0.7f));

            var avatar = MultiplayerRecorder.GetAvatar(player.Uid);
            if (avatar != null)
            {
                GUI.DrawTexture(new Rect(rect.x + 4f, rect.y + 3f, 16f, 16f), avatar, ScaleMode.ScaleToFit, true);
            }
            else
            {
                DrawCircle(new Vector2(rect.x + 12f, rect.y + 11f), 5f, player.Color);
            }

            GUI.Label(new Rect(rect.x + 24f, rect.y + 2f, rect.width - 28f, 18f), player.Name, _smallStyle);
        }
    }

    private static float NiceStep(float range, int targetTicks)
    {
        if (range <= 0.0001f)
        {
            return 0.5f;
        }

        float raw = range / Mathf.Max(1, targetTicks);
        float magnitude = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
        float[] multipliers = { 1f, 2f, 2.5f, 5f, 10f };
        foreach (float m in multipliers)
        {
            float candidate = m * magnitude;
            if (candidate >= raw - 0.0001f)
            {
                return candidate;
            }
        }

        return 10f * magnitude;
    }

    private static void DrawSeries(PlayerSeries player, Rect plot, float yMin, float yMax, float maxTime)
    {
        var samples = player.Samples;
        if (samples.Count == 0)
        {
            return;
        }

        var start = new Vector2(plot.x, MapY(100f, plot, yMin, yMax));

        // Area fill (self only, subtle), sampled every 4 px instead of every pixel.
        if (player.IsSelf)
        {
            var fillColor = new Color(player.Color.r, player.Color.g, player.Color.b, 0.10f);
            int steps = Mathf.Max(1, (int)(plot.width / 6f));
            int index = 0;
            for (int i = 0; i <= steps; i++)
            {
                float frac = i / (float)steps;
                float time = frac * maxTime;
                while (index + 1 < samples.Count && samples[index + 1].Time <= time)
                {
                    index++;
                }

                float acc;
                if (time <= samples[0].Time)
                {
                    acc = 100f;
                }
                else if (index + 1 < samples.Count)
                {
                    var a = samples[index];
                    var b = samples[index + 1];
                    float span = b.Time - a.Time;
                    float t = span <= 0.0001f ? 0f : (time - a.Time) / span;
                    acc = a.Acc + (b.Acc - a.Acc) * Mathf.Clamp01(t);
                }
                else
                {
                    acc = samples[^1].Acc;
                }

                float y = MapY(acc, plot, yMin, yMax);
                DrawRect(new Rect(plot.x + frac * plot.width, y, 6.6f, plot.yMax - y), fillColor);
            }
        }

        // Down-sample the polyline to at most ~1 point per 1.5 px, so long charts do not turn into
        // thousands of rotated texture draws per frame.
        LinePoints.Clear();
        LinePoints.Add(start);
        Vector2 last = start;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            float x = plot.x + plot.width * Math.Clamp(sample.Time / maxTime, 0f, 1f);
            var point = new Vector2(x, MapY(sample.Acc, plot, yMin, yMax));
            float dx = point.x - last.x;
            float dy = point.y - last.y;
            if (dx * dx + dy * dy >= 6.25f)
            {
                LinePoints.Add(point);
                last = point;
            }
        }

        var finalSample = samples[^1];
        var finalPoint = new Vector2(
            plot.x + plot.width * Math.Clamp(finalSample.Time / maxTime, 0f, 1f),
            MapY(finalSample.Acc, plot, yMin, yMax));
        if ((finalPoint - last).sqrMagnitude > 0.01f)
        {
            LinePoints.Add(finalPoint);
        }

        if (player.IsApproximate)
        {
            // Only the final round record is known for this player (the protocol does not
            // broadcast live accuracy), so draw a dashed level line at the official final
            // accuracy instead of pretending to know the intermediate curve.
            var approxColor = new Color(player.Color.r, player.Color.g, player.Color.b, 0.85f);
            float finalY = MapY(player.FinalAcc, plot, yMin, yMax);
            const float dash = 9f;
            const float gap = 7f;
            for (float x = plot.x; x < plot.xMax; x += dash + gap)
            {
                float x2 = Mathf.Min(x + dash, plot.xMax);
                DrawLine(new Vector2(x, finalY), new Vector2(x2, finalY), approxColor, 1.4f);
            }

            DrawCircle(new Vector2(plot.xMax - 2f, finalY), 3.2f,
                new Color(player.Color.r, player.Color.g, player.Color.b, 1f));
        }
        else
        {
            // Glow pass only when the chart is not too dense.
            if (LinePoints.Count <= 240 && samples.Count <= 900)
            {
                var glow = new Color(player.Color.r, player.Color.g, player.Color.b, 0.16f);
                for (int i = 1; i < LinePoints.Count; i++)
                {
                    DrawLine(LinePoints[i - 1], LinePoints[i], glow, player.IsSelf ? 6f : 5f);
                }
            }

            float thickness = player.IsSelf ? 2.2f : 1.6f;
            for (int i = 1; i < LinePoints.Count; i++)
            {
                DrawLine(LinePoints[i - 1], LinePoints[i], player.Color, thickness);
            }
        }

        DrawCircle(start, 2.6f, new Color(player.Color.r, player.Color.g, player.Color.b, 0.95f));

        if (!player.IsSelf)
        {
            return;
        }

        // Great / Miss markers. Dense runs use single rectangles instead of multi-row circles.
        int markerCount = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].IsGreat || samples[i].IsMiss)
            {
                markerCount++;
            }
        }

        bool simpleMarkers = markerCount > 30;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!sample.IsGreat && !sample.IsMiss)
            {
                if (SettingManager.ShowPerfectDots)
                {
                    float px = plot.x + plot.width * Math.Clamp(sample.Time / maxTime, 0f, 1f);
                    var pp = new Vector2(px, MapY(sample.Acc, plot, yMin, yMax));
                    DrawRect(new Rect(pp.x - 0.7f, pp.y - 0.7f, 1.4f, 1.4f), new Color(1f, 1f, 1f, 0.40f));
                }

                continue;
            }

            float x = plot.x + plot.width * Math.Clamp(sample.Time / maxTime, 0f, 1f);
            var point = new Vector2(x, MapY(sample.Acc, plot, yMin, yMax));

            if (sample.IsGreat)
            {
                if (simpleMarkers)
                {
                    DrawRect(new Rect(point.x - 2f, point.y - 2f, 4f, 4f), GreatColor);
                }
                else
                {
                    DrawCircle(point, 3.4f, GreatColor);
                }
            }
            else
            {
                DrawDiamond(point, 3.6f, MissColor);
            }
        }
    }

    private static float SampleAccAt(PlayerSeries player, float time)
    {
        var samples = player.Samples;
        if (samples.Count == 0)
        {
            return 100f;
        }

        if (time <= samples[0].Time)
        {
            return 100f;
        }

        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Time >= time)
            {
                var a = samples[i - 1];
                var b = samples[i];
                float span = b.Time - a.Time;
                float t = span <= 0.0001f ? 0f : (time - a.Time) / span;
                float accA = a.Acc;
                float accB = b.Acc;
                return accA + (accB - accA) * t;
            }
        }

        return samples[^1].Acc;
    }

    private static void DrawSidebar(Rect card)
    {
        float x = card.x + 12f;
        float width = card.width - 24f;
        float y = card.y + 12f;

        GUI.Label(new Rect(x, y, width, 22f), "玩家", _sectionStyle);
        DrawRect(new Rect(x, y + 17f, width, 1f), BorderColor);
        y += 24f;

        var series = CollectSeries();
        if (series.Count == 0)
        {
            GUI.Label(new Rect(x, y, width, 16f), "暂无数据", _smallStyle);
        }

        // Keep a fixed area for the legend; the colour editor stays below it.
        const float rowHeight = 30f;
        float legendTop = y;
        float legendBottom = card.yMax - 158f;
        _legendArea = new Rect(x - 4f, legendTop, width + 8f, Mathf.Max(0f, legendBottom - legendTop));
        _legendViewHeight = _legendArea.height;
        _legendContentHeight = series.Count * rowHeight;
        float maxScroll = Mathf.Max(0f, _legendContentHeight - _legendViewHeight);
        _legendScroll = Mathf.Clamp(_legendScroll, 0f, maxScroll);

        _legendRows.Clear();
        for (var i = 0; i < series.Count; i++)
        {
            var player = series[i];
            float rowY = legendTop + i * rowHeight - _legendScroll;
            if (rowY + rowHeight < legendTop || rowY > legendBottom) continue;

            var row = new Rect(x, rowY, width, 28f);
            _legendRows.Add((row, player.Uid));
            bool selected = !_viewAll && player.Uid == _focusedUid;
            bool hovered = row.Contains(Event.current.mousePosition);
            if (selected)
            {
                DrawRounded(new Rect(row.x - 4f, row.y + 1f, row.width + 8f, row.height - 2f), AccentDim, 5f);
            }
            else if (hovered)
            {
                DrawRounded(new Rect(row.x - 4f, row.y + 1f, row.width + 8f, row.height - 2f),
                    new Color(1f, 1f, 1f, 0.05f), 5f);
                _hoverUid = player.Uid;
            }

            float contentX = row.x;

            Texture2D avatar = MultiplayerRecorder.GetAvatar(player.Uid);
            if (avatar != null)
            {
                var avatarRect = new Rect(row.x + 1f, row.y + 3f, 20f, 20f);
                GUI.DrawTexture(avatarRect, avatar, ScaleMode.ScaleToFit, true);
                contentX += 26f;
            }
            else
            {
                DrawCircle(new Vector2(row.x + 7f, row.y + 12f), 5f, player.Color);
                contentX += 18f;
            }

            string name = player.IsApproximate ? (player.Name + " ≈") : player.Name;
            GUI.Label(new Rect(contentX, row.y, row.width - (contentX - row.x) - 68f, 28f), name, _textStyle);
            GUI.Label(new Rect(row.xMax - 66f, row.y, 66f, 28f), $"{player.FinalAcc:0.00}%", _rightStyle);
        }

        // Scrollbar when the roster does not fit.
        if (maxScroll > 0.5f)
        {
            var track = new Rect(card.xMax - 7f, legendTop, 3f, _legendViewHeight);
            DrawRounded(track, new Color(1f, 1f, 1f, 0.08f), 2f);
            float thumbH = Mathf.Max(18f, _legendViewHeight * (_legendViewHeight / _legendContentHeight));
            float thumbY = legendTop + (maxScroll <= 0.001f ? 0f : (_legendScroll / maxScroll) * (_legendViewHeight - thumbH));
            DrawRounded(new Rect(track.x, thumbY, track.width, thumbH), new Color(1f, 1f, 1f, 0.28f), 2f);
        }

        y = legendBottom + 10f;
        DrawRect(new Rect(x, y, width, 1f), BorderColor);
        y += 12f;

        if (RunRecorder.Self == null)
        {
            return;
        }

        GUI.Label(new Rect(x, y, width, 22f), "线条颜色", _sectionStyle);
        y += 22f;

        var color = RunRecorder.Self.Color;
        string[] labels = { "R", "G", "B" };
        Color[] channelColors =
        {
            new(1f, 0.35f, 0.35f, 1f),
            new(0.40f, 0.90f, 0.45f, 1f),
            new(0.40f, 0.65f, 1f, 1f),
        };

        for (int i = 0; i < 3; i++)
        {
            float value = i == 0 ? color.r : i == 1 ? color.g : color.b;

            GUI.Label(new Rect(x, y, 14f, 18f), labels[i], _smallStyle);

            var track = new Rect(x + 16f, y + 6f, width - 16f - 40f, 4f);
            _sliderTracks[i] = track;
            DrawRounded(track, new Color(1f, 1f, 1f, 0.10f), 2f);
            DrawRounded(new Rect(track.x, track.y, track.width * value, track.height), channelColors[i], 2f);

            var knob = new Vector2(track.x + track.width * value, track.center.y);
            DrawCircle(knob, _activeSlider == i ? 6f : 5f, new Color(0.95f, 0.97f, 1f, 1f));

            GUI.Label(new Rect(x + width - 40f, y, 40f, 18f), $"{(int)Mathf.Round(value * 255f)}", _rightStyle);

            y += 20f;
        }

        y += 4f;
        DrawRect(new Rect(x, y, width, 8f), color);
        DrawRoundedBorder(new Rect(x, y, width, 8f), 2f, new Color(0f, 0f, 0f, 0.4f));

        y += 16f;
        GUI.Label(new Rect(x, y + 2f, 34f, 16f), "HEX", _smallStyle);
        _hexRect = new Rect(x + 36f, y - 2f, width - 36f, 20f);
        if (!_hexActive)
        {
            _hexBuffer = SettingManager.OwnLineColorHex;
        }

        DrawRounded(_hexRect, new Color(1f, 1f, 1f, 0.08f), 4f);
        DrawRoundedBorder(_hexRect, 4f, _hexActive ? Accent : BorderColor);
        string hexText = _hexBuffer + (_hexActive ? "|" : "");
        GUI.Label(new Rect(_hexRect.x + 8f, _hexRect.y + 2f, _hexRect.width - 12f, 16f), hexText, _smallStyle);
    }

    private static float MapY(float acc, Rect plot, float yMin, float yMax)
    {
        float t = Mathf.Clamp((acc - yMin) / Mathf.Max(0.0001f, yMax - yMin), 0f, 1f);
        return plot.yMax - t * plot.height;
    }

    private static string FormatAxisTime(float seconds, float total)
    {
        if (total < 20f)
        {
            return $"{seconds:0.0}s";
        }

        return FormatTime(seconds);
    }

    private static string FormatTime(float seconds)
    {
        if (seconds < 0f)
        {
            seconds = 0f;
        }

        int total = (int)Mathf.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    #endregion

    #region primitives

    private static void DrawRect(Rect rect, Color color)
    {
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return;
        }

        var old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, White);
        GUI.color = old;
    }

    private static void DrawRounded(Rect rect, Color color, float radius)
    {
        float r = Mathf.Min(radius, Mathf.Min(rect.width, rect.height) * 0.5f);
        if (r < 1.5f)
        {
            DrawRect(rect, color);
            return;
        }

        float x = rect.x;
        float y = rect.y;
        float w = rect.width;
        float h = rect.height;

        DrawRect(new Rect(x, y + r, w, h - 2f * r), color);
        DrawRect(new Rect(x + r, y, w - 2f * r, r), color);
        DrawRect(new Rect(x + r, y + h - r, w - 2f * r, r), color);

        int rows = (int)Mathf.Ceil(r);
        int step = Mathf.Max(1, rows / 4);
        for (int i = 0; i < rows; i += step)
        {
            float dy = r - (i + step * 0.5f);
            float hw = Mathf.Sqrt(Mathf.Max(0f, r * r - dy * dy));
            float px = r - hw;
            float chunkH = Mathf.Min(step, rows - i);

            DrawRect(new Rect(x + px, y + i, hw, chunkH), color);
            DrawRect(new Rect(x + w - r, y + i, hw, chunkH), color);
            DrawRect(new Rect(x + px, y + h - chunkH - i, hw, chunkH), color);
            DrawRect(new Rect(x + w - r, y + h - chunkH - i, hw, chunkH), color);
        }
    }

    private static void DrawRoundedBorder(Rect rect, float radius, Color color)
    {
        float r = Mathf.Min(radius, Mathf.Min(rect.width, rect.height) * 0.5f);
        float x = rect.x;
        float y = rect.y;
        float w = rect.width;
        float h = rect.height;

        if (r < 1.5f)
        {
            DrawRect(new Rect(x, y, w, 1f), color);
            DrawRect(new Rect(x, y + h - 1f, w, 1f), color);
            DrawRect(new Rect(x, y, 1f, h), color);
            DrawRect(new Rect(x + w - 1f, y, 1f, h), color);
            return;
        }

        DrawRect(new Rect(x + r, y, w - 2f * r, 1f), color);
        DrawRect(new Rect(x + r, y + h - 1f, w - 2f * r, 1f), color);
        DrawRect(new Rect(x, y + r, 1f, h - 2f * r), color);
        DrawRect(new Rect(x + w - 1f, y + r, 1f, h - 2f * r), color);

        int borderRows = (int)Mathf.Ceil(r);
        int borderStep = Mathf.Max(1, borderRows / 4);
        for (int i = 0; i < borderRows; i += borderStep)
        {
            float dy = r - (i + borderStep * 0.5f);
            float hw = Mathf.Sqrt(Mathf.Max(0f, r * r - dy * dy));
            float px = r - hw;

            DrawRect(new Rect(x + px, y + i, 1f, Mathf.Min(borderStep, borderRows - i)), color);
            DrawRect(new Rect(x + w - px - 1f, y + i, 1f, Mathf.Min(borderStep, borderRows - i)), color);
            DrawRect(new Rect(x + px, y + h - borderStep - i, 1f, Mathf.Min(borderStep, borderRows - i)), color);
            DrawRect(new Rect(x + w - px - 1f, y + h - borderStep - i, 1f, Mathf.Min(borderStep, borderRows - i)), color);
        }
    }

    private static void DrawCircle(Vector2 center, float radius, Color color)
    {
        if (radius < 0.6f)
        {
            return;
        }

        int rows = Mathf.Max(2, (int)Mathf.Ceil(radius * 2f));
        for (int i = 0; i < rows; i++)
        {
            float dy = (i + 0.5f) - radius;
            float hw = Mathf.Sqrt(Mathf.Max(0f, radius * radius - dy * dy));
            if (hw <= 0f)
            {
                continue;
            }

            DrawRect(new Rect(center.x - hw, center.y - radius + i, hw * 2f, 1f), color);
        }
    }

    private static void DrawDiamond(Vector2 center, float radius, Color color)
    {
        Matrix4x4 matrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(45f, center);
        DrawRect(new Rect(center.x - radius * 0.72f, center.y - radius * 0.72f, radius * 1.44f, radius * 1.44f), color);
        GUI.matrix = matrix;
    }

    private static void DrawLine(Vector2 from, Vector2 to, Color color, float width)
    {
        Vector2 delta = to - from;
        float length = Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y);
        if (length < 0.01f)
        {
            return;
        }

        Matrix4x4 matrix = GUI.matrix;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        GUIUtility.RotateAroundPivot(angle, from);
        var old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, length, width), White);
        GUI.color = old;
        GUI.matrix = matrix;
    }

    #endregion
}
