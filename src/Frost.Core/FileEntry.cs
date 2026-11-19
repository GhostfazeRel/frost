namespace Frost.Core;

/// <summary>One item in a folder listing. Immutable snapshot.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,              // bytes; 0 for directories
    DateTime Modified,      // local time
    DateTime Created,       // local time
    FileAttributes Attributes)
{
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
    public bool IsHidden => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public static FileEntry FromInfo(FileSystemInfo info)
    {
        var attrs = info.Attributes;
        bool dir = info is DirectoryInfo || (attrs & FileAttributes.Directory) != 0;
        long size = !dir && info is FileInfo fi ? fi.Length : 0;
        return new FileEntry(info.Name, info.FullName, dir, size, info.LastWriteTime, info.CreationTime, attrs);
    }
}
