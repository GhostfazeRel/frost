using System.Windows;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public partial class MainWindow
{
    private record PaletteCommand(string Title, string Gesture, Action Run);

    /// <summary>The command palette list. To add a command, append a line here: <c>new("Title", "Shortcut text or empty", () => ...)</c>.</summary>
    private List<PaletteCommand> BuildCommands()
    {
        var t = _vm.CurrentTab;
        var st = _vm.Settings;
        var isDefault = DefaultFileManager.IsEnabled();
        var cmds = new List<PaletteCommand>
        {
            new("New tab", "Ctrl+T", () => _vm.NewTab()),
            new("Close tab", "Ctrl+W", () => _vm.CloseTab(_vm.CurrentTab)),
            new("Reopen closed tab", "Ctrl+Shift+T", _vm.ReopenClosedTab),
            new("Duplicate tab", "", () => _vm.DuplicateTab(_vm.CurrentTab)),
            new("Next tab", "Ctrl+Tab", () => _vm.CycleTab(1)),
            new("Previous tab", "Ctrl+Shift+Tab", () => _vm.CycleTab(-1)),
            new("Go to folder", "Ctrl+P", ShowGoToFolder),
            new("Back", "Alt+Left", () => _vm.CurrentTab?.GoBack()),
            new("Forward", "Alt+Right", () => _vm.CurrentTab?.GoForward()),
            new("Up one level", "Alt+Up", () => _vm.CurrentTab?.Up()),
            new("Refresh", "F5", () => { _vm.CurrentTab?.Refresh(); _vm.LoadDrives(); }),
            new("Edit address bar", "Ctrl+L", BeginAddressEdit),
            new("Search", "Ctrl+F", () => { SearchBox.Focus(); SearchBox.SelectAll(); }),
            new("New folder", "Ctrl+Shift+N", _vm.NewFolder),
            new("New text document", "", _vm.NewTextFile),
            new("Copy", "Ctrl+C", () => _vm.CopySelection(false)),
            new("Cut", "Ctrl+X", () => _vm.CopySelection(true)),
            new("Paste", "Ctrl+V", () => _vm.Paste()),
            new("Select all", "Ctrl+A", () => ActiveList.SelectAll()),
            new("Rename", "F2", _vm.BeginRenameSelection),
            new("Delete", "Delete", () => _vm.Delete(false)),
            new("Delete permanently", "Shift+Delete", () => _vm.Delete(true)),
            new("Copy path", "Ctrl+Shift+C", _vm.CopyPath),
            new("Properties", "Alt+Enter", () => _vm.Properties(_vm.CurrentTab?.Path)),
            new("Open terminal here", "", () => _vm.OpenTerminal()),
            new("View: Details", "Ctrl+Shift+1", () => _vm.SetView(ViewMode.Details)),
            new("View: Tiles", "Ctrl+Shift+2", () => _vm.SetView(ViewMode.Tiles)),
            new("View: Large icons", "Ctrl+Shift+3", () => _vm.SetView(ViewMode.Large)),
            new("View: Extra large icons", "Ctrl+Shift+4", () => _vm.SetView(ViewMode.ExtraLarge)),
            new("Sort by name", "", () => SortBy(SortColumn.Name)),
            new("Sort by date modified", "", () => SortBy(SortColumn.Modified)),
            new("Sort by type", "", () => SortBy(SortColumn.Type)),
            new("Sort by size", "", () => SortBy(SortColumn.Size)),
            new("Sort ascending", "", () => _vm.SetSortDirection(false)),
            new("Sort descending", "", () => _vm.SetSortDirection(true)),
            new(_vm.ShowHidden ? "Hide hidden items" : "Show hidden items", "Ctrl+H", () => _vm.ShowHidden = !_vm.ShowHidden),
            new(_vm.PreviewPane ? "Hide preview pane" : "Show preview pane", "Alt+P", () => _vm.PreviewPane = !_vm.PreviewPane),
            new(t != null && _vm.IsPinned(t.Path) ? "Unpin this folder from sidebar" : "Pin this folder to sidebar", "", () => { if (_vm.CurrentTab is { } c) _vm.TogglePin(c.Path); }),
            new(isDefault ? "Restore Explorer as default" : "Make Frost the default file manager", "", () =>
            {
                if (!isDefault && !Installer.IsInstalled) _vm.Notify("Install Frost first (run Frost-Setup) to make it the default file manager.");
                else _vm.SetDefaultFileManager(!isDefault);
            }),
            new("Check for updates", "", () => CheckForUpdates(true)),
        };
        // integration: append Undo / Zip / Batch rename entries here
        return cmds;
    }

    // keeps the chosen column, applies the current direction (same as the Sort menu)
    private void SortBy(SortColumn c) { _vm.Settings.SortColumn = c; _vm.SetSortDirection(_vm.Settings.SortDescending); }

    private void ShowCommandPalette()
    {
        var cmds = BuildCommands();
        new PaletteWindow(this, "Type a command", q =>
            Fuzzy.Rank(cmds, q, c => c.Title).Select(r => new PaletteRow(new PaletteEntry(r.Item.Title, r.Item.Gesture, _ => r.Item.Run()), r.Match)).ToList()).Show();
    }

    private void ShowGoToFolder()
    {
        var places = GoToCandidates();
        new PaletteWindow(this, "Go to folder (Ctrl+Enter: new tab)", q =>
        {
            var rows = new List<PaletteRow>();
            var typed = Environment.ExpandEnvironmentVariables(q.Trim().Trim('"'));
            if (typed.Length > 0 && Path.IsPathRooted(typed) && Directory.Exists(typed))
                rows.Add(new PaletteRow(new PaletteEntry("Go to " + typed, "", ctrl => OpenFolder(typed, ctrl)), new FuzzyMatch(0, Array.Empty<int>())));
            rows.AddRange(Fuzzy.Rank(places, q, p => p.Path).Take(60).Select(r => new PaletteRow(new PaletteEntry(r.Item.Path, r.Item.Kind, ctrl => OpenFolder(r.Item.Path, ctrl)), r.Match)));
            return rows;
        }).Show();
    }

    private void OpenFolder(string path, bool newTab)
    {
        if (newTab) _vm.NewTab(path); else _vm.CurrentTab?.Navigate(path);
    }

    /// <summary>Recent, pinned, quick access, drives, then subfolders of the current folder; deduped, missing folders skipped.</summary>
    private List<(string Path, string Kind)> GoToCandidates()
    {
        var list = new List<(string Path, string Kind)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p, string kind, bool check = true)
        {
            if (string.IsNullOrEmpty(p)) return;
            var key = p.Length > 3 ? p.TrimEnd('\\', '/') : p;
            if (seen.Contains(key)) return;
            try { if (check && !Directory.Exists(p)) return; } catch { return; }
            seen.Add(key); list.Add((p, kind));
        }
        foreach (var p in _vm.Settings.RecentFolders) Add(p, "Recent");
        foreach (var p in _vm.Settings.Pinned) Add(p, "Pinned");
        foreach (var i in _vm.Sections[0].Items) Add(i.Path, "Quick access");
        foreach (var i in _vm.Sections[2].Items) Add(i.Path, "Drive", false);
        if (_vm.CurrentTab != null)
            foreach (var i in _vm.CurrentTab.Items.Where(i => i.IsDirectory).Take(300)) Add(i.FullPath, "Subfolder", false);
        return list;
    }
}
