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
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    public IReadOnlyList<(string Old, string New)> Applied { get; private set; } = Array.Empty<(string, string)>();

    public BatchRenameWindow(IReadOnlyList<string> paths)
    {
        _paths = paths;
        InitializeComponent();
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => Recompute();
        Closing += (_, e) => { if (_busy) e.Cancel = true; else _cts?.Cancel(); }; // never close while files are being renamed
        TitleText.Text = $"Rename {paths.Count} items";
        Loaded += (_, _) => { FindBox.Focus(); Recompute(); };
    }

    private bool TryOptions(out BatchRenameOptions o)
    {
        o = new();
        if (!int.TryParse(StartBox.Text.Trim(), out var start) || !int.TryParse(StepBox.Text.Trim(), out var step)
            || !int.TryParse(WidthBox.Text.Trim(), out var width) || width is < 1 or > 18) return false;
        o = new BatchRenameOptions
        {
            Find = FindBox.Text, Replace = ReplaceBox.Text, UseRegex = RegexCheck.IsChecked == true, MatchCase = CaseCheck.IsChecked == true,
            Prefix = PrefixBox.Text, Suffix = SuffixBox.Text, IncludeExtension = ExtCheck.IsChecked == true,
            Start = start, Step = step, Width = width, Case = _case
        };
        return true;
    }

    private void Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdatePreview(); }

    private void CaseMode_Click(object sender, RoutedEventArgs e)
    {
        _case = sender == CaseLower ? BatchCase.Lower : sender == CaseUpper ? BatchCase.Upper : sender == CaseTitle ? BatchCase.Title : BatchCase.Unchanged;
        foreach (var t in new[] { CaseKeep, CaseLower, CaseUpper, CaseTitle }) t.IsChecked = t == sender;
        UpdatePreview();
    }

    /// <summary>Debounced: the plan is computed off the UI thread ~150 ms after the last change; older runs are cancelled.</summary>
    private void UpdatePreview()
    {
        OkButton.IsEnabled = false; // never apply a stale preview
        _debounce.Stop(); _debounce.Start();
    }

    private async void Recompute()
    {
        _debounce.Stop();
        _cts?.Cancel();
        if (!TryOptions(out var opts))
        {
            _plan = Array.Empty<BatchRenameItem>();
            Preview.ItemsSource = null;
            SummaryText.Text = "Start, step and digits (1-18) must be whole numbers.";
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        IReadOnlyList<BatchRenameItem> plan;
        try { plan = await Task.Run(() => BatchRename.Plan(_paths, opts, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; } // a newer run is on its way
        catch (Exception ex) { SummaryText.Text = "Couldn't build the preview: " + ex.Message; return; }
        if (cts.IsCancellationRequested) return;
        _plan = plan;
        Preview.ItemsSource = plan.Select(p => new Row(p.OldName, p.NewName, p.Error, p.Error != null ? ErrorBrush : (Brush)FindResource("TextBrush"))).ToList();
        int errors = plan.Count(p => p.Error != null), changed = plan.Count(p => p.NewName != p.OldName);
        SummaryText.Text = errors > 0 ? $"{errors} problem(s): hover a red name for details." : $"{changed} of {plan.Count} will be renamed.";
        OkButton.IsEnabled = errors == 0 && changed > 0 && !_busy;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled || _busy) return;
        _busy = true; OkButton.IsEnabled = false; CancelButton.IsEnabled = false; ErrorText.Visibility = Visibility.Collapsed;
        var plan = _plan;
        try
        {
            Applied = await Task.Run(() => BatchRename.Apply(plan));
            _busy = false; // closing is allowed again
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            CancelButton.IsEnabled = true;
            ErrorText.Text = ex is BatchRenameRollbackException ? ex.Message : "Nothing was renamed (changes were rolled back): " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            Recompute();
        }
    }

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase and not TextBoxBase) try { DragMove(); } catch { }
    }
}
