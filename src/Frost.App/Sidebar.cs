using System.Collections.ObjectModel;
using System.Windows;

namespace Frost.App;

public sealed class SidebarItem : ObservableObject
{
    private bool _current;
    public SidebarItem(string name, string path, string glyph) { Name = name; Path = path; Glyph = glyph; }
    public string Name { get; }
    public string Path { get; }
    public string Glyph { get; }
    public bool IsPinned { get; init; }
    public bool IsDrive { get; init; }
    public double UsedPercent { get; init; }
    public string FreeText { get; init; } = "";
    public Visibility DriveVisibility => IsDrive ? Visibility.Visible : Visibility.Collapsed;
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
}

public sealed class SidebarSection : ObservableObject
{
    private bool _expanded = true;
    public SidebarSection(string title, string? hint = null)
    {
        Title = title; Hint = hint;
        Items.CollectionChanged += (_, _) => { Raise(nameof(HintVisibility)); };
    }
    public string Title { get; }
    public string? Hint { get; }
    public ObservableCollection<SidebarItem> Items { get; } = new();
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(HintVisibility)); } }
    public Visibility HintVisibility => Hint != null && Items.Count == 0 && IsExpanded ? Visibility.Visible : Visibility.Collapsed;
}
