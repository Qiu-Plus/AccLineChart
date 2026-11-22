using System.Globalization;
using System.Text;

namespace AccLineChart;

/// <summary>
/// Debug helper: writes the current Unity UI hierarchy to UserData/AccLineChart_scene.txt.
/// Used to find the objects Euterpe creates for its multiplayer HUD.
/// </summary>
internal static class SceneProbe
{
    internal static void Dump()
    {
        try
        {
            var sb = new StringBuilder();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            sb.AppendLine($"# scene dump {DateTime.Now:yyyy-MM-dd HH:mm:ss} scene={scene.name}");

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            sb.AppendLine($"# canvases={canvases.Length}");
            int count = 0;
            foreach (var canvas in canvases)
            {
                sb.AppendLine($"## Canvas '{canvas.name}' root={canvas.isRootCanvas} order={canvas.sortingOrder} scale={F(canvas.scaleFactor)}");
                DumpTransform(canvas.transform, 0, sb, ref count);
            }

            string dir = MelonEnvironment.UserDataDirectory;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string latestPath = Path.Combine(dir, "AccLineChart_scene.txt");
            string historyPath = Path.Combine(dir, $"AccLineChart_scene_{stamp}.txt");
            File.WriteAllText(latestPath, sb.ToString());
            File.WriteAllText(historyPath, sb.ToString());
            Logger.Msg($"[Probe] scene dumped: {historyPath} ({count} objects)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Probe] dump failed: {ex}");
        }
    }

    private static void DumpTransform(Transform transform, int depth, StringBuilder sb, ref int count)
    {
        if (transform == null || depth > 16)
        {
            return;
        }

        count++;
        sb.Append(' ', depth * 2).Append("- ").Append(transform.name);
        sb.Append(transform.gameObject.activeSelf ? "" : " (inactive)");

        try
        {
            var rect = transform.TryCast<RectTransform>();
            if (rect != null)
            {
                sb.Append(" pos=").Append(F(rect.anchoredPosition.x)).Append(',').Append(F(rect.anchoredPosition.y));
                sb.Append(" size=").Append(F(rect.sizeDelta.x)).Append('x').Append(F(rect.sizeDelta.y));
            }
        }
        catch
        {
            // ignore rect failures
        }

        try
        {
            var components = transform.GetComponents<Component>();
            sb.Append(" [");
            bool first = true;
            foreach (var component in components)
            {
                if (component == null)
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append(", ");
                }

                first = false;
                sb.Append(component.GetType().Name);

                if (component is Text text)
                {
                    sb.Append("='").Append(text.text).Append('\'');
                    if (text.fontSize > 0)
                    {
                        sb.Append(" fs=").Append(text.fontSize);
                    }

                    var color = text.color;
                    sb.Append(" rgba=").Append(F(color.r)).Append(',').Append(F(color.g)).Append(',').Append(F(color.b)).Append(',').Append(F(color.a));
                }
                else if (component is Image image)
                {
                    if (image.sprite != null && !string.IsNullOrEmpty(image.sprite.name))
                    {
                        sb.Append("(sprite=").Append(image.sprite.name).Append(')');
                    }

                    sb.Append(" fill=").Append(F(image.fillAmount)).Append(" type=").Append(image.type);
                    var color = image.color;
                    sb.Append(" rgba=").Append(F(color.r)).Append(',').Append(F(color.g)).Append(',').Append(F(color.b)).Append(',').Append(F(color.a));
                }
                else if (component is RawImage raw)
                {
                    if (raw.texture != null)
                    {
                        sb.Append("(tex=").Append(raw.texture.name).Append(' ').Append(raw.texture.width).Append('x').Append(raw.texture.height).Append(')');
                    }

                    var color = raw.color;
                    sb.Append(" rgba=").Append(F(color.r)).Append(',').Append(F(color.g)).Append(',').Append(F(color.b)).Append(',').Append(F(color.a));
                }
                else if (component is CanvasGroup group)
                {
                    sb.Append(" alpha=").Append(F(group.alpha)).Append(" visible=").Append(group.blocksRaycasts);
                }
                else if (component is Canvas canvas)
                {
                    sb.Append(" order=").Append(canvas.sortingOrder);
                }
                else
                {
                    try
                    {
                        var tmp = component.TryCast<Il2CppTMPro.TMP_Text>();
                        if (tmp != null && !string.IsNullOrEmpty(tmp.text))
                        {
                            sb.Append("='").Append(tmp.text).Append('\'');
                        }
                    }
                    catch
                    {
                        // not a TMP text
                    }
                }
            }

            sb.Append(']');
        }
        catch
        {
            // ignore component failures
        }

        sb.AppendLine();

        int childCount = transform.childCount;
        for (int i = 0; i < childCount; i++)
        {
            DumpTransform(transform.GetChild(i), depth + 1, sb, ref count);
        }
    }

    private static string F(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
