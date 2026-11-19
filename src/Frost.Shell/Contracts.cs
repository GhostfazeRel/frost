// CONTRACT FILE: public API of Frost.Shell (Windows shell / Win32 interop) that Frost.App depends on.
// Implementers: replace the `throw new NotImplementedException()` bodies (you may split into files),
// but do NOT change public signatures. All methods are called on the UI (STA) thread unless noted.
using System.Windows;
using System.Windows.Media;

namespace Frost.Shell;

public enum DriveKind { Fixed, Removable, Network, CdRom, Other }

public sealed record DriveItem(string Name, string Label, string RootPath, long TotalBytes, long FreeBytes, DriveKind Kind);

public sealed record QuickFolder(string Name, string Path, string Glyph); // Glyph: a Segoe Fluent Icons char, e.g. ""
