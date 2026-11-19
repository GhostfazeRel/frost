using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Frost.Core;

public static class Search
{
    /// <summary>Recursive search under root. Query matching: case-insensitive substring of the name, or a wildcard
    /// pattern if it contains * or ? (e.g. "*.png"). Skips inaccessible folders and reparse points (no loops).
    /// Yields results as found; honors cancellation.</summary>
    public static async IAsyncEnumerable<FileEntry> RunAsync(string root, string query, bool includeHidden,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                };
                var rootDir = new DirectoryInfo(root);
                if (!rootDir.Exists) throw new DirectoryNotFoundException($"Folder not found: {root}");
                var stack = new Stack<DirectoryInfo>();
                stack.Push(rootDir);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    IEnumerable<FileSystemInfo> children;
                    try { children = dir.EnumerateFileSystemInfos("*", options); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    // Enumeration is lazy, so errors can surface mid-iteration; guard with a manual loop.
                    using var en = children.GetEnumerator();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        FileSystemInfo info;
                        try { if (!en.MoveNext()) break; info = en.Current; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
                        if (Matches(info.Name, query))
                            await channel.Writer.WriteAsync(FileEntry.FromInfo(info), token).ConfigureAwait(false);
                        if (info is DirectoryInfo sub && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                            stack.Push(sub);
                    }
                }
                channel.Writer.TryComplete();
            }
            catch (Exception e) { channel.Writer.TryComplete(e); }
        }, CancellationToken.None);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return entry;
        }
        finally { cts.Cancel(); }
    }

    /// <summary>The matching rule used by RunAsync (exposed for tests and for the in-folder quick filter).
    /// An empty query matches everything.</summary>
    public static bool Matches(string name, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (query.AsSpan().IndexOfAny('*', '?') >= 0)
            return FileSystemName.MatchesSimpleExpression(query, name, ignoreCase: true);
        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
