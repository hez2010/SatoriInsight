using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Charts;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using SatoriInsight.Controls;
using Xunit;

namespace SatoriInsight.Tests;

public class UiTests
{
    // FAAppWindow's native handle registry does not support multiple headless windows on Windows.
    // Keep the complete interactive workflow in one window.
    [AvaloniaFact(Timeout = 90000)]
    public async Task ProcessPickerConnectsRealTargetAndRendersTelemetry()
    {
        using var target = await TestWorkload.StartAsync("net10.0");
        var window = new MainWindow();
        window.Show();
        var output = TestPaths.Artifacts;
        Directory.CreateDirectory(output);

        try
        {
            Assert.False(window.ViewModel.IsConnected);
            Assert.Equal(
                new[] { "Memory activity", "Heap inspector" },
                window.FindControl<FANavigationView>("Navigation")!.MenuItems.Cast<FANavigationViewItem>().Select(item => item.Content?.ToString()));
            Assert.Equal("Memory activity", window.ViewModel.PageTitle);
            Assert.Single(window.GetVisualDescendants().OfType<MemoryHistoryView>());
            Assert.Single(window.GetVisualDescendants().OfType<PauseHistogramView>());
            Assert.Single(window.GetVisualDescendants().OfType<RegionsView>(), view => view.IsEffectivelyVisible);
            Assert.True(window.FindControl<DataGrid>("EventsGrid")!.IsEffectivelyVisible);
            Assert.Empty(window.FindControl<FANavigationView>("Navigation")!.FooterMenuItems);
            window.UpdateLayout();
            AssertRegionsBesideGenerations(window);
            Assert.Equal("—", window.ViewModel.UsedLabel);
            Assert.False(window.FindControl<Button>("ExportButton")!.IsEnabled);
            SaveFrame(window, Path.Combine(output, "disconnected-preview.png"));
            Click(window, window.FindControl<Button>("ConnectButton")!);
            await Until(() => window.GetVisualDescendants().OfType<ProcessPickerView>().Any()
                && window.ViewModel.LocalProcesses.Any(process => process.ProcessId == target.Process.Id));
            var picker = window.GetVisualDescendants().OfType<ProcessPickerView>().Single();
            window.ViewModel.ProcessSearch = target.Process.Id.ToString();
            picker.FindControl<DataGrid>("ProcessGrid")!.SelectedItem = window.ViewModel.LocalProcesses.Single();
            Assert.True(window.ViewModel.CanAttach);
            Assert.Contains("SatoriInsight.Workload", window.ViewModel.SelectedLocalProcess!.CommandLineTooltip);
            window.UpdateLayout();
            var nameCell = picker.GetVisualDescendants().OfType<TextBlock>().Single(text =>
                text.Classes.Contains("process-name") && text.IsEffectivelyVisible &&
                text.DataContext is Models.LocalDotNetProcess process && process.ProcessId == target.Process.Id);
            ToolTip.SetIsOpen(nameCell, true);
            Dispatcher.UIThread.RunJobs();
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(nameCell));
            Assert.Contains("SatoriInsight.Workload", tip.Text);
            ToolTip.SetIsOpen(nameCell, false);
            SaveFrame(window, Path.Combine(output, "process-picker-preview.png"));
            var connect = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content is string text && text == "Connect");
            Click(window, connect);
            await Until(() => window.ViewModel.IsConnected);
            await target.Process.StandardInput.WriteLineAsync("gc");
            await Until(() =>
            {
                window.ViewModel.Tick();
                return window.ViewModel.History.Count >= 4 && window.ViewModel.Events.Count > 0;
            });
            Assert.NotEqual("—", window.ViewModel.UsedLabel);
            Assert.NotEqual("—", window.ViewModel.Gen2CountLabel);
            Assert.Equal(window.ViewModel.Gen2CountLabel, window.FindControl<TextBlock>("Gen2Counter")!.Text);
            Assert.True(window.FindControl<Button>("ExportButton")!.IsEnabled);
            Assert.True(window.FindControl<Button>("DisconnectButton")!.IsVisible);
            window.ViewModel.IsPaused = true;
            window.UpdateLayout();
            var historyView = window.GetVisualDescendants().OfType<MemoryHistoryView>().Single(view => view.IsEffectivelyVisible);
            historyView.BringIntoView();
            window.UpdateLayout();
            SaveFrame(window, Path.Combine(output, "managed-heap-preview.png"));
            var historyPlot = historyView.FindControl<Border>("PlotAreaProbe")!;
            var plotOrigin = historyPlot.TranslatePoint(new Point(), window)!.Value;
            var point = plotOrigin + new Vector(historyPlot.Bounds.Width * 0.5, historyPlot.Bounds.Height * 0.5);
            var timelineEnd = window.ViewModel.ChartMaximumSeconds;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ViewModel.HasTimelineSelection);
            Assert.False(window.ViewModel.IsPeriodSelected);
            Assert.InRange(window.ViewModel.SessionSeconds, timelineEnd * 0.49, timelineEnd * 0.51);
            Assert.Equal(timelineEnd, window.ViewModel.ChartMaximumSeconds);
            Assert.True(window.ViewModel.IsMemoryActivityView);
            var collectionBand = historyView.FindControl<GcTimeline>("CollectionTimeline")!;
            var bandPoint = collectionBand.TranslatePoint(new Point(collectionBand.LeftInset + historyPlot.Bounds.Width * 0.6, 31), window)!.Value;
            window.MouseDown(bandPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(bandPoint, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ViewModel.IsMemoryActivityView);
            Assert.True(window.ViewModel.HasTimelineSelection);
            var from = plotOrigin + new Vector(historyPlot.Bounds.Width * 0.3, historyPlot.Bounds.Height * 0.5);
            var to = plotOrigin + new Vector(historyPlot.Bounds.Width * 0.8, historyPlot.Bounds.Height * 0.5);
            window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(to, RawInputModifiers.None);
            window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ViewModel.IsPeriodSelected);
            Assert.InRange(window.ViewModel.WindowStartSeconds, timelineEnd * 0.29, timelineEnd * 0.31);
            Assert.InRange(window.ViewModel.SessionSeconds, timelineEnd * 0.79, timelineEnd * 0.81);
            SaveFrame(window, Path.Combine(output, "timeline-period-preview.png"));
            Click(window, historyView.FindControl<Button>("ResetTimelineButton")!);
            Assert.False(window.ViewModel.HasTimelineSelection);
            Assert.False(window.ViewModel.IsPaused);
            window.ViewModel.IsPaused = true;
            SaveFrame(window, Path.Combine(output, "managed-heap-preview.png"));
            window.Width = 1120;
            window.Height = 800;
            SaveFrame(window, Path.Combine(output, "compact-preview.png"));
            AssertRegionsBesideGenerations(window);
            window.Width = 1480;
            window.Height = 1020;
            window.UpdateLayout();
            var histogram = window.GetVisualDescendants().OfType<PauseHistogramView>().Single(view => view.IsEffectivelyVisible);
            histogram.BringIntoView();
            window.UpdateLayout();
            var chart = histogram.FindControl<CartesianChart>("PauseChart")!;
            var probe = histogram.FindControl<Border>("HistogramPlotProbe")!;
            SaveFrame(window, Path.Combine(output, "managed-heap-preview.png"));
            var binIndex = window.ViewModel.Histogram.ToList().FindIndex(bin => bin.Count > 0);
            var origin = probe.TranslatePoint(new Point(), chart)!.Value;
            var barPoint = new Point(origin.X + probe.Bounds.Width * (binIndex + 0.5) / 7, origin.Y + probe.Bounds.Height - 3);
            var clickPoint = chart.TranslatePoint(barPoint, window)!.Value;
            window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ViewModel.HasPauseFilter);
            Assert.True(window.ViewModel.IsMemoryActivityView);
            Assert.NotEmpty(window.ViewModel.EventLog);
            Assert.True(window.FindControl<DataGrid>("EventsGrid")!.IsEffectivelyVisible);
            SaveFrame(window, Path.Combine(output, "gc-events-preview.png"));
            await target.Process.StandardInput.WriteLineAsync("fragment");

            while (await target.Process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line && line != "FRAGMENTED")
            {
            }

            window.ViewModel.NavigationIndex = 1;
            await window.ViewModel.CaptureHeapAsync();
            var captured = Assert.Single(window.ViewModel.Snapshots);
            Assert.NotEmpty(captured.Holes);
            window.ViewModel.ShowCapturedRegions();
            window.ViewModel.CapturedRegionMap.SelectedRegion = captured.Regions.MaxBy(r => r.FreeBytes);
            var inspector = window.GetVisualDescendants().OfType<HeapInspectorView>().Single();
            var regions = window.GetVisualDescendants().OfType<RegionsView>().Single(view => view.IsEffectivelyVisible);
            window.UpdateLayout();
            var map = regions.FindControl<RegionMap>("HeapMap")!;
            Assert.True(map.IsEffectivelyVisible);
            Assert.Equal(captured.Regions.Length, map.Regions!.Count);
            SaveFrame(window, Path.Combine(output, "heap-inspector-preview.png"));
            window.ViewModel.CapturedRegionMap.ColorIndex = 2;
            regions.FindControl<Slider>("CellSlider")!.Value = 28;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(28, map.CellSize);
            SaveFrame(window, Path.Combine(output, "fragmentation-preview.png"));
            map.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
            Assert.Equal(
                captured.Regions.OrderBy(r => r.RuntimeId).ThenBy(r => r.HeapIndex).ThenBy(r => r.StartAddress).Last(), window.ViewModel.CapturedRegionMap.SelectedRegion);
            window.ViewModel.CapturedRegionMap.SelectedRegion = captured.Regions.MaxBy(r => r.FreeBytes);
            window.ViewModel.InspectRegionObjects();
            Assert.All(window.ViewModel.Objects, obj => Assert.Equal(window.ViewModel.CapturedRegionMap.SelectedRegion!.Id, obj.RegionId));
            Assert.True(window.ViewModel.IsHeapInspectorView);
            Assert.Equal(2, inspector.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
            var rooted = captured.Objects.First(obj => captured.Roots.Any(r => r.ObjectAddress == obj.Address));
            window.ViewModel.InspectObject(rooted);
            Assert.Equal(rooted, window.ViewModel.SelectedObject);
            await window.ViewModel.FindRootAsync();
            Assert.True(window.ViewModel.RootPath.Count > 0, window.ViewModel.HeapStatus + " | selected: " + window.ViewModel.SelectedObject?.AddressLabel);
            Assert.Equal(3, inspector.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
            SaveFrame(window, Path.Combine(output, "root-path-preview.png"));
            window.ViewModel.InspectionTab = 0;
            using var saved = new MemoryStream();
            await window.ViewModel.ExportSessionAsync(saved);
            await window.ViewModel.DisconnectAsync();
            Assert.False(window.ViewModel.IsConnected);
            Assert.True(window.ViewModel.HasSessionData);
            Assert.True(window.ViewModel.ShowStateNotice);
            saved.Position = 0;
            await window.ViewModel.ImportSessionAsync(saved);
            Assert.True(window.ViewModel.IsReplay);
            Assert.Single(window.ViewModel.Snapshots);
            SaveFrame(window, Path.Combine(output, "session-replay-preview.png"));
        }
        finally
        {
            await window.ViewModel.DisposeAsync();
            window.Close();
        }
    }

    private static void AssertRegionsBesideGenerations(Window window)
    {
        var generation = window.FindControl<Border>("GenerationSummaryPanel")!;
        var regions = window.GetVisualDescendants().OfType<RegionsView>().Single(view => view.IsEffectivelyVisible);
        var generationOrigin = generation.TranslatePoint(new Point(), window)!.Value;
        var regionOrigin = regions.TranslatePoint(new Point(), window)!.Value;

        Assert.InRange(Math.Abs(generationOrigin.Y - regionOrigin.Y), 0, 1);
        Assert.True(regionOrigin.X >= generationOrigin.X + generation.Bounds.Width);
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();

        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("UI state did not become ready.");
            }

            await Task.Delay(60, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
