using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EuterpeMp;

/// <summary>
/// Reads the login tokens that the Euterpe mod stores for the current Windows user.
/// File: &lt;Muse Dash game root&gt;\UserData\Euterpe\session.dat
/// Format: DPAPI(CurrentUser) protected JSON {"access_token": "...", "refresh_token": "..."}.
/// The file is only read; it is never written back.
/// </summary>
public static class EuterpeSession
{
    public sealed class Tokens
    {
        public string AccessToken = "";
        public string RefreshToken = "";
        public DateTimeOffset? AccessTokenExpiresAt;
        public bool LooksExpired => AccessTokenExpiresAt is { } t && t < DateTimeOffset.UtcNow.AddMinutes(1);
        public override string ToString() => $"access={Mask(AccessToken)} ({(LooksExpired ? "expired" : "ok")})";

        private static string Mask(string s) => s.Length <= 12 ? "***" : s[..6] + "…" + s[^6..];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Typical locations; the first existing file wins.</summary>
    public static IEnumerable<string> CandidatePaths(string? gameRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot))
            yield return Path.Combine(gameRoot, "UserData", "Euterpe", "session.dat");

        foreach (var drive in new[] { "D", "E", "C" })
        {
            yield return $@"{drive}:\steam\steamapps\common\Muse Dash\UserData\Euterpe\session.dat";
            yield return $@"{drive}:\SteamLibrary\steamapps\common\Muse Dash\UserData\Euterpe\session.dat";
            yield return $@"{drive}:\Program Files (x86)\Steam\steamapps\common\Muse Dash\UserData\Euterpe\session.dat";
        }
    }

    public static Tokens? TryLoad(string? gameRoot, out string error)
    {
        foreach (var path in CandidatePaths(gameRoot))
        {
            if (!File.Exists(path)) continue;
            var t = ReadTokenFile(path, out error);
            if (t != null) return t;
        }
        error = "session.dat not found (is the Euterpe mod installed and logged in?)";
        return null;
    }

    public static Tokens? ReadTokenFile(string path, out string error)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            var plain = Unprotect(raw);
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            var tokens = new Tokens
            {
                AccessToken = GetString(root, "access_token"),
                RefreshToken = GetString(root, "refresh_token"),
            };
            tokens.AccessTokenExpiresAt = ReadJwtExpiry(tokens.AccessToken);
            error = "";
            return string.IsNullOrEmpty(tokens.AccessToken) ? null : tokens;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        var inputHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = inputHandle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output))
                throw new InvalidOperationException("CryptUnprotectData failed: " + Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            inputHandle.Free();
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? ReadJwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort identity of the logged-in Euterpe account, read from the access-token JWT.
    /// Used only to avoid drawing our own line twice in the multiplayer chart.
    /// </summary>
    public static bool TryGetAccount(out int uid, out string nick)
    {
        uid = 0;
        nick = "";
        try
        {
            var tokens = TryLoad(null, out _);
            if (tokens == null) return false;
            var parts = tokens.AccessToken.Split('.');
            if (parts.Length < 2) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            // Euterpe access tokens carry the account id as a string "sub" (e.g. "430").
            if (root.TryGetProperty("sub", out var sub))
            {
                if (sub.ValueKind == JsonValueKind.Number && sub.TryGetInt32(out var s)) uid = s;
                else if (sub.ValueKind == JsonValueKind.String && int.TryParse(sub.GetString(), out var s2)) uid = s2;
            }
            else if (root.TryGetProperty("uid", out var uidEl))
            {
                if (uidEl.ValueKind == JsonValueKind.Number && uidEl.TryGetInt32(out var u)) uid = u;
                else if (uidEl.ValueKind == JsonValueKind.String && int.TryParse(uidEl.GetString(), out var u2)) uid = u2;
            }

            if (root.TryGetProperty("nick", out var nickEl) && nickEl.ValueKind == JsonValueKind.String)
                nick = nickEl.GetString() ?? "";
            return uid != 0;
        }
        catch
        {
            return false;
        }
    }
}
