using System.Text.Json;

namespace EuterpeMp;

/// <summary>Tolerant JSON helpers used by the (already typed) wire model.</summary>
public static class MpJson
{
    public static IEnumerable<JsonElement> Descendants(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            if (cur.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cur.EnumerateObject())
                    stack.Push(p.Value);
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray())
                    stack.Push(item);
            }
        }
    }

    public static bool TryGet(JsonElement e, out JsonElement value, params string[] names)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in names)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        return false;
    }

    public static string? Str(JsonElement e, params string[] names) =>
        TryGet(e, out var v, names) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Num(JsonElement e, params string[] names)
    {
        if (!TryGet(e, out var v, names)) return null;
        return ToDouble(v);
    }

    public static double? ToDouble(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
        JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
        JsonValueKind.Object => Num(v, "value", "v", "fraction", "percent"),
        _ => null,
    };

    public static bool? Bool(JsonElement e, params string[] names)
    {
        if (!TryGet(e, out var v, names)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => null,
        };
    }
}
