using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Frost.Core;

namespace Frost.App;

public sealed record UpdateInfo(string Tag, Version Version, string Body, string AssetUrl, string? Sha256);

/// <summary>Looks for a newer Frost-Setup-*.exe on GitHub releases and downloads it.</summary>
public static class Updater
{
    private const string ReleasesUrl = "https://api.github.com/repos/GhostfazeRel/frost/releases?per_page=10";
    private static readonly HttpClient Http = CreateClient();

    public static Version Current => typeof(Updater).Assembly.GetName().Version!;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-call timeouts: downloads can take longer than 10 s
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Frost-Updater");
        return c;
    }

    /// <summary>The newest installable release if it is newer than this build, else null. Throws on network/parse errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));

        UpdateInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (UpdateVersion.Parse(tag) is not { } v || (best != null && v <= best.Version)) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.StartsWith("Frost-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!a.TryGetProperty("browser_download_url", out var u) || u.GetString() is not { } url || !IsGitHubUrl(url)) continue;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var sha = UpdateVersion.ParseSha256Digest(digest);
                if (sha == null) continue; // never run an installer we cannot verify
                var body = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                best = new UpdateInfo(tag!, v, body, url, sha);
                break;
            }
        }
        return best != null && UpdateVersion.IsNewer(best.Tag, Current) ? best : null;
    }

    private static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes setups left in %Temp% by earlier updates (a running one just stays). Never throws.</summary>
    public static void DeleteOldDownloads()
    {
        try { foreach (var f in Directory.GetFiles(Path.GetTempPath(), "Frost-Setup-*.exe")) try { File.Delete(f); } catch { } }
        catch { }
    }

    /// <summary>Downloads the setup to %Temp%, verifying the SHA-256 digest when GitHub provides one. Deletes the file on any failure.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"Frost-Setup-{u.Version.ToString(3)}.exe");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stallAfter = TimeSpan.FromSeconds(30);
        stall.CancelAfter(stallAfter);
        try
        {
            using (var resp = await Http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                using var dst = File.Create(file);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                {
                    stall.CancelAfter(stallAfter); // 30 s without a byte = stalled
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0) progress.Report(100.0 * done / total);
                }
                if (u.Sha256 != null && !Convert.ToHexString(sha.GetHashAndReset()).Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download is damaged (checksum mismatch). Please try again later.");
            }
            return file;
        }
        catch (Exception ex)
        {
            try { File.Delete(file); } catch { }
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException("The download stopped responding. Please try again later.");
            throw;
        }
    }
}
