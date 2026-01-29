using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Controls;

public sealed class CampathTimelineControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<CampathKeyframeViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<CampathTimelineControl, IReadOnlyList<CampathKeyframeViewModel>?>(nameof(Items));

    public static readonly StyledProperty<CampathKeyframeViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<CampathTimelineControl, CampathKeyframeViewModel?>(nameof(SelectedItem));

    public static readonly StyledProperty<double> PlayheadTimeProperty =
        AvaloniaProperty.Register<CampathTimelineControl, double>(nameof(PlayheadTime), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<CampathTimelineControl, double>(nameof(Duration), 5.0);

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<CampathTimelineControl, bool>(nameof(IsPlaying));

    private readonly List<(Rect rect, CampathKeyframeViewModel keyframe)> _keyframeRects = new();
    private bool _draggingPlayhead;
    private CampathKeyframeViewModel? _draggingKeyframe;
    private bool _itemsHooked;
    private bool _freecamPreviewActive;
    private bool _campathPreviewActive;
    private bool _keyframesHooked;
    private Point _pressPoint;

    public CampathTimelineControl()
    {
        Focusable = true;
    }

    static CampathTimelineControl()
    {
        AffectsRender<CampathTimelineControl>(ItemsProperty, SelectedItemProperty, PlayheadTimeProperty, DurationProperty, IsPlayingProperty);
        ItemsProperty.Changed.AddClassHandler<CampathTimelineControl>((ctrl, args) => ctrl.OnItemsChanged(args));
    }

    public IReadOnlyList<CampathKeyframeViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public CampathKeyframeViewModel? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public double PlayheadTime
    {
        get => GetValue(PlayheadTimeProperty);
        set => SetValue(PlayheadTimeProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public event Action<double>? FreecamPreviewRequested;
    public event Action? FreecamPreviewEnded;
    public event Action? CampathPreviewRequested;
    public event Action? CampathPreviewEnded;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus();
            var pt = e.GetPosition(this);

            if (HitTestPlayhead(pt))
            {
                _draggingPlayhead = true;
                _freecamPreviewActive = IsCtrlDown(e.KeyModifiers);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var keyframe = HitTestKeyframe(pt);
            if (keyframe != null)
            {
                _pressPoint = pt;
                SelectedItem = keyframe;
                _draggingKeyframe = keyframe;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (SelectedItem != null)
            {
                SelectedItem = null;
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingPlayhead)
        {
            var pt = e.GetPosition(this);
            var time = XToTime(pt.X);
            if (IsShiftDown(e.KeyModifiers))
            {
                var snap = FindNearestKeyframeTime(time);
                if (snap.HasValue)
                    time = snap.Value;
            }
            PlayheadTime = time;
            var ctrlDown = IsCtrlDown(e.KeyModifiers);
            if (ctrlDown && !_freecamPreviewActive)
            {
                _freecamPreviewActive = true;
            }
            else if (!ctrlDown && _freecamPreviewActive)
            {
                _freecamPreviewActive = false;
                FreecamPreviewEnded?.Invoke();
            }

            if (_freecamPreviewActive)
                FreecamPreviewRequested?.Invoke(time);

            var altDown = IsAltDown(e.KeyModifiers);
            if (altDown && !ctrlDown && !_campathPreviewActive)
            {
                _campathPreviewActive = true;
                CampathPreviewRequested?.Invoke();
            }
            else if ((!altDown || ctrlDown) && _campathPreviewActive)
            {
                _campathPreviewActive = false;
                CampathPreviewEnded?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (_draggingKeyframe != null)
        {
            var pt = e.GetPosition(this);
            var delta = pt - _pressPoint;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3.0)
            {
            }
            _draggingKeyframe.Time = XToTime(pt.X);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_draggingPlayhead || _draggingKeyframe != null)
        {
            _draggingPlayhead = false;
            _draggingKeyframe = null;
            if (_freecamPreviewActive)
            {
                _freecamPreviewActive = false;
                FreecamPreviewEnded?.Invoke();
            }
            if (_campathPreviewActive)
            {
                _campathPreviewActive = false;
                CampathPreviewEnded?.Invoke();
            }
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && SelectedItem != null)
        {
            if (Items is IList<CampathKeyframeViewModel> list)
            {
                list.Remove(SelectedItem);
                SelectedItem = null;
                e.Handled = true;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        _keyframeRects.Clear();

        var background = new SolidColorBrush(Color.Parse("#0E0E0E"));
        context.FillRectangle(background, bounds);

        var paddingLeft = 6.0;
        var paddingRight = 6.0;
        var paddingTop = 6.0;
        var paddingBottom = 6.0;
        var rulerHeight = 18.0;
        var stripTop = paddingTop + rulerHeight;
        var stripBottom = bounds.Height - paddingBottom;
        var stripMid = (stripTop + stripBottom) * 0.5;
        var stripLeft = paddingLeft;
        var stripRight = bounds.Width - paddingRight;

        var linePen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        context.DrawLine(linePen, new Point(stripLeft, stripMid), new Point(stripRight, stripMid));

        DrawTimeRuler(context, stripLeft, stripRight, paddingTop + 2.0);
        DrawKeyframes(context, stripLeft, stripRight, stripMid);
        DrawPlayhead(context, stripLeft, stripRight, paddingTop + 2.0, stripBottom);
        DrawCurrentTimeText(context, stripLeft, stripRight, paddingTop);
    }

    private void DrawTimeRuler(DrawingContext context, double left, double right, double y)
    {
        var duration = Math.Max(0.01, Duration);
        var width = right - left;
        if (width <= 1)
            return;

        var secondsPerPixel = duration / width;
        var minLabelSpacing = 50.0;
        var minStep = secondsPerPixel * minLabelSpacing;
        var step = ChooseStep(minStep);

        var pen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        for (var t = 0.0; t <= duration + 0.0001; t += step)
        {
            var x = TimeToX(t, left, right);
            context.DrawLine(pen, new Point(x, y + 2), new Point(x, y + 8));

            var text = new FormattedText(
                FormatTime(t),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Gray);
            context.DrawText(text, new Point(x + 2, y - 2));
        }
    }

    private void DrawKeyframes(DrawingContext context, double left, double right, double midY)
    {
        if (Items == null || Items.Count == 0)
            return;

        var size = 16.0;
        foreach (var key in Items)
        {
            var x = TimeToX(key.Time, left, right);
            var rect = new Rect(x - size * 0.5, midY - size * 0.5, size, size);
            _keyframeRects.Add((rect.Inflate(3), key));

            var isSelected = key == SelectedItem;
            var color = isSelected ? Color.Parse("#E6E6E6") : Color.Parse("#AAAAAA");
            var brush = new SolidColorBrush(color);

            var diamond = new StreamGeometry();
            using (var gc = diamond.Open())
            {
                gc.BeginFigure(new Point(x, rect.Top), true);
                gc.LineTo(new Point(rect.Right, midY));
                gc.LineTo(new Point(x, rect.Bottom));
                gc.LineTo(new Point(rect.Left, midY));
                gc.EndFigure(true);
            }
            context.DrawGeometry(brush, null, diamond);
        }
    }

    private void DrawPlayhead(DrawingContext context, double left, double right, double top, double bottom)
    {
        var x = TimeToX(PlayheadTime, left, right);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#FFB84D")), 1.5);
        context.DrawLine(pen, new Point(x, top), new Point(x, bottom));

        var headSize = 14.0;
        var head = new StreamGeometry();
        using (var gc = head.Open())
        {
            gc.BeginFigure(new Point(x, top + headSize), true);
            gc.LineTo(new Point(x - headSize * 0.5, top));
            gc.LineTo(new Point(x + headSize * 0.5, top));
            gc.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#FFB84D")), null, head);
    }

    private void DrawCurrentTimeText(DrawingContext context, double left, double right, double y)
    {
        var text = new FormattedText(
            $"t {PlayheadTime:0.00}s",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Brushes.LightGray);
        var textY = Bounds.Height - 16;
        context.DrawText(text, new Point(left, textY));
    }

    private CampathKeyframeViewModel? HitTestKeyframe(Point pt)
    {
        foreach (var (rect, key) in _keyframeRects)
        {
            if (rect.Contains(pt))
                return key;
        }
        return null;
    }

    private bool HitTestPlayhead(Point pt)
    {
        var left = 6.0;
        var right = Bounds.Width - 6.0;
        var x = TimeToX(PlayheadTime, left, right);
        var headSize = 14.0;
        var top = 6.0 + 2.0;
        var hitRect = new Rect(x - headSize * 0.6, top, headSize * 1.2, headSize + 4.0);
        return hitRect.Contains(pt);
    }

    private double TimeToX(double time, double left, double right)
    {
        var duration = Math.Max(0.01, Duration);
        var t = Math.Clamp(time, 0.0, duration);
        return left + (t / duration) * (right - left);
    }

    private double XToTime(double x)
    {
        var left = 6.0;
        var right = Bounds.Width - 6.0;
        if (right <= left)
            return 0.0;
        var t = (x - left) / (right - left);
        t = Math.Clamp(t, 0.0, 1.0);
        return t * Math.Max(0.01, Duration);
    }

    private static bool IsShiftDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift);

    private static bool IsCtrlDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control);

    private static bool IsAltDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt);

    private double? FindNearestKeyframeTime(double time)
    {
        if (Items == null || Items.Count == 0)
            return null;
        var nearest = Items.OrderBy(k => Math.Abs(k.Time - time)).FirstOrDefault();
        return nearest?.Time;
    }

    private void OnItemsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_itemsHooked && e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= OnItemsCollectionChanged;

        UnhookKeyframeItems();

        _itemsHooked = false;
        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += OnItemsCollectionChanged;
            _itemsHooked = true;
        }

        HookKeyframeItems();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<CampathKeyframeViewModel>())
                item.PropertyChanged -= OnKeyframePropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<CampathKeyframeViewModel>())
                item.PropertyChanged += OnKeyframePropertyChanged;
        }

        InvalidateVisual();
    }

    private void HookKeyframeItems()
    {
        if (_keyframesHooked || Items == null)
            return;

        foreach (var item in Items.OfType<CampathKeyframeViewModel>())
            item.PropertyChanged += OnKeyframePropertyChanged;
        _keyframesHooked = true;
    }

    private void UnhookKeyframeItems()
    {
        if (!_keyframesHooked || Items == null)
            return;

        foreach (var item in Items.OfType<CampathKeyframeViewModel>())
            item.PropertyChanged -= OnKeyframePropertyChanged;
        _keyframesHooked = false;
    }

    private void OnKeyframePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static double ChooseStep(double minStep)
    {
        var steps = new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0 };
        foreach (var step in steps)
        {
            if (step >= minStep)
                return step;
        }
        return 120.0;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 60.0)
            return $"{seconds:0.##}s";
        var mins = Math.Floor(seconds / 60.0);
        var sec = seconds - mins * 60.0;
        return $"{mins:0}m{sec:00}s";
    }
}
