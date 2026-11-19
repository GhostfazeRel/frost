using System.Diagnostics;
using System.Windows.Input;
using Frost.Core;

namespace Frost.App;

public sealed class OperationViewModel : ObservableObject
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private double _percent;
    private string _item = "Preparing…", _detail = "";

    public OperationViewModel(string title)
    {
        Title = title;
        CancelCommand = new RelayCommand(_ => Cts.Cancel());
    }

    public string Title { get; }
    public CancellationTokenSource Cts { get; } = new();
    public ICommand CancelCommand { get; }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string CurrentItem { get => _item; private set => Set(ref _item, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public void Report(OperationProgress p)
    {
        CurrentItem = p.CurrentItem;
        Percent = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal
                : p.ItemsTotal > 0 ? 100.0 * p.ItemsDone / p.ItemsTotal : 0;
        var secs = _sw.Elapsed.TotalSeconds;
        if (secs < 0.5 || p.BytesDone <= 0) { Detail = $"{p.ItemsDone} of {p.ItemsTotal} items"; return; }
        var speed = p.BytesDone / secs;
        var left = speed > 0 ? TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed) : TimeSpan.Zero;
        Detail = $"{Format.Size((long)speed)}/s · {FormatLeft(left)} left";
    }

    private static string FormatLeft(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" :
        t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{Math.Max(1, t.Seconds)} s";
}
