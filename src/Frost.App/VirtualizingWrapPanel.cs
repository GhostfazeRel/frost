using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Frost.App;

/// <summary>Fixed-cell-size virtualizing wrap panel (vertical scrolling) for the icon views.</summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent, _viewport;
    private Point _offset;
    public int ItemsPerRow { get; private set; } = 1;

    private double CellW => Math.Max(16, ItemWidth);
    private double CellH => Math.Max(16, ItemHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        _ = InternalChildren; // makes sure the generator is initialised
        double w = double.IsInfinity(availableSize.Width) ? CellW * 4 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        int perRow = Math.Max(1, (int)(w / CellW));
        ItemsPerRow = perRow;
        int rows = (count + perRow - 1) / perRow;
        UpdateScrollInfo(new Size(w, rows * CellH), new Size(w, h));

        if (count > 0)
        {
            int firstRow = (int)(_offset.Y / CellH);
            int lastRow = (int)Math.Ceiling((_offset.Y + h) / CellH) - 1;
            int first = firstRow * perRow;
            int last = Math.Min(count - 1, (lastRow + 1) * perRow - 1);
            Realize(first, last);
            Cleanup(first, last);
        }
        else Cleanup(0, -1);

        return new Size(w, double.IsInfinity(availableSize.Height) ? rows * CellH : h);
    }

    private void Realize(int first, int last)
    {
        IItemContainerGenerator gen = ItemContainerGenerator;
        var pos = gen.GeneratorPositionFromIndex(first);
        int childIndex = pos.Offset == 0 ? pos.Index : pos.Index + 1;
        using (gen.StartAt(pos, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)gen.GenerateNext(out bool isNew);
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    gen.PrepareItemContainer(child);
                }
                child.Measure(new Size(CellW, CellH));
            }
        }
    }

    private void Cleanup(int first, int last)
    {
        IItemContainerGenerator gen = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var p = new GeneratorPosition(i, 0);
            int idx = gen.IndexFromGeneratorPosition(p);
            if (idx < first || idx > last)
            {
                gen.Remove(p, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IItemContainerGenerator gen = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            int row = idx / ItemsPerRow, col = idx % ItemsPerRow;
            InternalChildren[i].Arrange(new Rect(col * CellW, row * CellH - _offset.Y, CellW, CellH));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index) => EnsureRowVisible(index / Math.Max(1, ItemsPerRow));

    private void EnsureRowVisible(int row)
    {
        double top = row * CellH, bottom = top + CellH;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        bool changed = extent != _extent || viewport != _viewport;
        _extent = extent; _viewport = viewport;
        var maxY = Math.Max(0, extent.Height - viewport.Height);
        if (_offset.Y > maxY) { _offset.Y = maxY; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    // ---- IScrollInfo ----
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - 40);
    public void LineDown() => SetVerticalOffset(_offset.Y + 40);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 80);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 80);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        IItemContainerGenerator gen = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] is Visual c && (ReferenceEquals(c, visual) || c.IsAncestorOf(visual)))
            {
                int idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                EnsureRowVisible(idx / ItemsPerRow);
                break;
            }
        }
        return rectangle;
    }
}
