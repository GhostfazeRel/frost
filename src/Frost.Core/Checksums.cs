using System.Security.Cryptography;

namespace Frost.Core;

public static class Checksums
{
    /// <summary>MD5, SHA-1 and SHA-256 (lower-case hex) of a file, computed in one read. Progress is 0-100.</summary>
    public static async Task<(string Md5, string Sha1, string Sha256)> ComputeAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 17, true);
        var buf = new byte[1 << 17];
        long done = 0, total = Math.Max(fs.Length, 1);
        int n;
        while ((n = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            md5.AppendData(buf, 0, n); sha1.AppendData(buf, 0, n); sha256.AppendData(buf, 0, n);
            done += n; progress?.Report(100.0 * done / total);
        }
        return (Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    /// <summary>True if pasted text (spaces/colons/case ignored) equals the hash.</summary>
    public static bool Matches(string pasted, string hash)
        => !string.IsNullOrWhiteSpace(pasted) && string.Equals(new string(pasted.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray()), hash, StringComparison.OrdinalIgnoreCase);
}

public static class EmptyFolders
{
    /// <summary>The top-most folders under <paramref name="root"/> that contain no files at all (only other empty folders).
    /// The root itself is never listed. Folder links and unreadable folders count as content, so nothing doubtful is offered.</summary>
    public static List<string> Find(string root, CancellationToken ct)
    {
        var result = new List<string>();
        Scan(root, result, ct, true);
        return result;
    }

    // Returns true if dir holds anything that is not an empty folder; its empty child folders are added to result then.
    private static bool Scan(string dir, List<string> result, CancellationToken ct, bool isRoot = false)
    {
        ct.ThrowIfCancellationRequested();
        var emptyKids = new List<string>();
        bool content = false;
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (e is DirectoryInfo && (e.Attributes & FileAttributes.ReparsePoint) == 0)
                { if (Scan(e.FullName, result, ct)) content = true; else emptyKids.Add(e.FullName); }
                else content = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        if (!content && !isRoot) return false; // an empty dir is reported by its parent, as one entry
        result.AddRange(emptyKids);
        return content;
    }
}
