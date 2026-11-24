using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EuterpeMp;

/// <summary>
/// Downloads multiplayer avatars (Euterpe profile pictures) and keeps a byte cache.
/// Euterpe itself caches images under UserData/Euterpe/cache/images, but the cache
/// key is an internal hash; we cache by avatar URL instead so the mapping stays visible.
/// </summary>
public sealed class MpAvatarCache : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, byte[]?> _memory = new(StringComparer.Ordinal);
    private readonly string _directory;

    public MpAvatarCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public static string KeyFor(string url)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
    }

    public async Task<byte[]?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_memory.TryGetValue(url, out var cached)) return cached;

        var key = KeyFor(url);
        var file = Path.Combine(_directory, key + ".bin");
        try
        {
            if (File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                _memory[url] = bytes;
                return bytes;
            }
        }
        catch
        {
            // fall through to network
        }

        try
        {
            var uri = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(url) : new Uri(new Uri("https://euterpe-org.com"), url);
            var bytes = await _http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            try { await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false); } catch { /* cache only */ }
            _memory[url] = bytes;
            return bytes;
        }
        catch
        {
            _memory[url] = null;
            return null;
        }
    }

    /// <summary>Removes cached files and memory entries.</summary>
    public void Clear()
    {
        _memory.Clear();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, "*.bin"))
                File.Delete(f);
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose() => _http.Dispose();
}
