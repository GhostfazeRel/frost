using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frost.Core;

namespace Frost.App;

/// <summary>Batch rename dialog: options on top, live preview below. DialogResult true = renamed; see <see cref="Applied"/>.</summary>
public partial class BatchRenameWindow : Window
{
    private sealed record Row(string Old, string New, string? Error, Brush Brush)
    {
        public bool HasError => Error != null;
    }

    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private readonly IReadOnlyList<string> _paths;
    private IReadOnlyList<BatchRenameItem> _plan = Array.Empty<BatchRenameItem>();
    private BatchCase _case;
    private bool _busy;

    public IReadOnlyList<(string Old, string New)> Applied { get; private set; } = Array.Empty<(string, string)>();

    public BatchRenameWindow(IReadOnlyList<string> paths)
    {
        _paths = paths;
        InitializeComponent();
        TitleText.Text = $"Rename {paths.Count} items";
        Loaded += (_, _) => { FindBox.Focus(); UpdatePreview(); };
    }

    private BatchRenameOptions Options()
    {
        static int Num(TextBox b, int fallback) => int.TryParse(b.Text.Trim(), out var v) ? v : fallback;
        return new BatchRenameOptions
        {
            Find = FindBox.Text, Replace = ReplaceBox.Text, UseRegex = RegexCheck.IsChecked == true, MatchCase = CaseCheck.IsChecked == true,
            Prefix = PrefixBox.Text, Suffix = SuffixBox.Text, IncludeExtension = ExtCheck.IsChecked == true,
            Start = Num(StartBox, 1), Step = Num(StepBox, 1), Width = Num(WidthBox, 1), Case = _case
        };
    }

    private void Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdatePreview(); }

    private void CaseMode_Click(object sender, RoutedEventArgs e)
    {
        _case = sender == CaseLower ? BatchCase.Lower : sender == CaseUpper ? BatchCase.Upper : sender == CaseTitle ? BatchCase.Title : BatchCase.Unchanged;
        foreach (var t in new[] { CaseKeep, CaseLower, CaseUpper, CaseTitle }) t.IsChecked = t == sender;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _plan = BatchRename.Plan(_paths, Options());
        Preview.ItemsSource = _plan.Select(p => new Row(p.OldName, p.NewName, p.Error, p.Error != null ? ErrorBrush : (Brush)FindResource("TextBrush"))).ToList();
        int errors = _plan.Count(p => p.Error != null), changed = _plan.Count(p => p.NewName != p.OldName);
        SummaryText.Text = errors > 0 ? $"{errors} problem(s): hover a red name for details." : $"{changed} of {_plan.Count} will be renamed.";
        OkButton.IsEnabled = errors == 0 && changed > 0 && !_busy;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        _busy = true; OkButton.IsEnabled = false; ErrorText.Visibility = Visibility.Collapsed;
        var plan = _plan;
        try
        {
            Applied = await Task.Run(() => BatchRename.Apply(plan));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Nothing was renamed: " + ex.Message; // Apply rolls back on failure
            ErrorText.Visibility = Visibility.Visible;
            _busy = false;
            UpdatePreview();
        }
    }

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase and not TextBoxBase) try { DragMove(); } catch { }
    }
}
