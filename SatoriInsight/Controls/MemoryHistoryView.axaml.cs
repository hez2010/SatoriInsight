using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class MemoryHistoryView : UserControl
{
    private Control? _dragTrack;
    private double _dragOriginX;
    private double _dragLeft;
    private double _dragWidth;
    private double _dragMaximum;
    private double _dragStart;
    private long _lastUpdate;
    private bool _dragging;

    // Chart CategoryPath and ValuePath resolve properties at runtime.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(MetricPoint))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ProcessMetricPoint))]
    public MemoryHistoryView()
    {
        InitializeComponent();
        Background = Avalonia.Media.Brushes.Transparent;
        LayoutUpdated += (_, _) =>
        {
#if AVALONIA_PRO
            var origin = PlotAreaProbe.TranslatePoint(new Point(), MemoryChart);

            if (origin is not { } point || PlotAreaProbe.Bounds.Width <= 0)
            {
                return;
            }

            CollectionTimeline.LeftInset = point.X;
            CollectionTimeline.RightInset = Math.Max(0, MemoryChart.Bounds.Width - point.X - PlotAreaProbe.Bounds.Width);
#endif
            CollectionSelection.Margin = new Thickness(CollectionTimeline.LeftInset, 0, CollectionTimeline.RightInset, 0);
        };
        CollectionTimeline.CollectionInvoked += gc =>
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }

            vm.SelectTimePoint(gc.EndSeconds);
        };
        AddHandler(PointerPressedEvent, SelectionPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SelectionMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, SelectionReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PointerCaptureLost += (_, _) =>
        {
            _dragging = false;
            _dragTrack = null;
        };
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ResetTimelineSelection();
        }
    }

    private void SelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragging || DataContext is not MainViewModel { IsMemoryActivityView: true, HasSessionData: true } vm
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var bandPoint = e.GetPosition(CollectionTimeline);

#if AVALONIA_PRO
        var chartPoint = e.GetPosition(MemoryChart);
        var plotOrigin = PlotAreaProbe.TranslatePoint(new Point(), MemoryChart);

        if (plotOrigin is { } origin && new Rect(origin, PlotAreaProbe.Bounds.Size).Contains(chartPoint))
        {
            _dragTrack = MemoryChart;
            _dragLeft = origin.X;
            _dragWidth = PlotAreaProbe.Bounds.Width;
        }
        else
#endif
        if (new Rect(CollectionTimeline.Bounds.Size).Contains(bandPoint) && bandPoint.X >= CollectionTimeline.LeftInset
            && bandPoint.X <= CollectionTimeline.Bounds.Width - CollectionTimeline.RightInset)
        {
            _dragTrack = CollectionTimeline;
            _dragLeft = CollectionTimeline.LeftInset;
            _dragWidth = CollectionTimeline.Bounds.Width - CollectionTimeline.LeftInset - CollectionTimeline.RightInset;
        }
        else
        {
            return;
        }

        if (_dragWidth <= 0)
        {
            return;
        }

        _dragMaximum = vm.ChartMaximumSeconds;
        _dragOriginX = e.GetPosition(_dragTrack).X;
        _dragStart = TimeAt(_dragOriginX);
        _dragging = true;
        _lastUpdate = Environment.TickCount64;
        e.Pointer.Capture(this);
        vm.SelectTimePoint(_dragStart);
        e.Handled = true;
    }

    private double TimeAt(double x) => Math.Clamp((x - _dragLeft) / _dragWidth, 0, 1) * _dragMaximum;

    private void UpdateSelection(PointerEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _dragTrack is null)
        {
            return;
        }

        var x = e.GetPosition(_dragTrack).X;

        if (Math.Abs(x - _dragOriginX) < 4)
        {
            vm.SelectTimePoint(_dragStart);
        }
        else
        {
            vm.SelectPeriod(_dragStart, TimeAt(x));
        }
    }

    private void SelectionMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (Environment.TickCount64 - _lastUpdate >= 100)
        {
            _lastUpdate = Environment.TickCount64;
            UpdateSelection(e);
        }

        e.Handled = true;
    }

    private void SelectionReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        UpdateSelection(e);
        _dragging = false;
        _dragTrack = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
