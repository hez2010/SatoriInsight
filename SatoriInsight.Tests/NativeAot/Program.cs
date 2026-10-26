using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Charts;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SatoriInsight;
using SatoriInsight.Controls;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;

static class Smoke
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--heap-worker"])
        {
            return HeapCaptureService.RunWorker();
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: SatoriInsight.AotSmoke <workload.dll>");
            return 2;
        }

        try
        {
            Check(!RuntimeFeature.IsDynamicCodeSupported, "Run the published NativeAOT executable.");
            CheckNativeServices(Path.GetFullPath(args[0]));
            CheckUi();
            Console.WriteLine("PASS: native process discovery, EventPipe, heap capture, replay, grid selection/sorting, and charts.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckNativeServices(string workloadPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(workloadPath);
        start.ArgumentList.Add("--label");
        start.ArgumentList.Add("日本語 with spaces");
        using var target = Process.Start(start)!;

        try
        {
            Check(target.StandardOutput.ReadLine()!.StartsWith("READY "), "Workload startup.");
            var process = new LocalProcessDiscovery().ListAsync().GetAwaiter().GetResult().Single(p => p.ProcessId == target.Id);
            Check(process.CommandLine?.Contains("SatoriInsight.Workload.dll") == true, "Native process command line was lost.");
            Check(process.CommandLine!.Contains("日本語 with spaces"), "Unicode command-line arguments were lost.");
            var reusedIdentity = process with { StartTimeUtc = process.StartTimeUtc.AddSeconds(1) };
            var rejected = ProcessCommandLines.EnrichAsync([reusedIdentity], default).GetAwaiter().GetResult();
            Check(rejected[0].CommandLine is null, "PID reuse must not expose another process command line.");
            var collector = EventPipeCollector.StartAsync(process).GetAwaiter().GetResult();

            try
            {
                target.StandardInput.WriteLine("gc");
                target.StandardOutput.ReadLine();
                var timeout = Stopwatch.StartNew();

                while (timeout.Elapsed < TimeSpan.FromSeconds(12))
                {
                    var frame = collector.Read();

                    if (frame.Events.Count > 0 && frame.History.Count > 0)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }

                Check(collector.Read().Events.Count > 0, "No GC events from native collector.");
                Check(collector.Read().History.Count > 0, "No runtime counters from native collector.");
                Check(collector.Read().Regions.Any(region => region.UsedRangeBytes.HasValue), "No generation ranges from native collector.");
            }
            finally
            {
                collector.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var profile = new HeapCaptureService().CaptureAsync(new(process), captureTimeout.Token).GetAwaiter().GetResult();
            Check(profile.Objects.Length > 0 && profile.Regions.Length > 0, "Native heap worker returned no objects.");
            var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", process, 0,
                [], [], null, null, [], [], [profile], null, null, []);
            Check(SessionStorage.Read(SessionStorage.Write(session)).Snapshots[0].Objects.Length == profile.Objects.Length, "Captured heap round trip.");
            Console.WriteLine("Native process query, EventPipe and heap worker passed.");
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill();
                target.WaitForExit();
            }
        }
    }

    private static void CheckUi()
    {
        var events = new[]
        {
            new GcEvent(1, 1, 1, "BackgroundGC", "AllocSmall", 3, 1, 0, Memory: new(2048, 1024, 0, false, [new("Gen1", 2048, 1024, 0)])),
            new GcEvent(2, 2, 0, "ThreadLocal", "Collected", 1, 0, 0, new(1, 1, true, 512, 256, 1000000))
        };
        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 3,
            [new(1, 2, 4, 1), new(2, 3, 5, 1)], events, null, null, [], [], [], null, null,
            [new(1, 1, 1, 10 * 1048576), new(2, 2, 2, 12 * 1048576)]);
        var json = SessionStorage.Write(session);
        Check(SessionStorage.Read(json).Collections.Length == 2, "Session round trip.");
        using var stream = new MemoryStream();
        SessionStorage.WriteAsync(stream, session, default).GetAwaiter().GetResult();
        stream.Position = 0;
        Check(SessionStorage.ReadAsync(stream, default).GetAwaiter().GetResult().ProcessMetrics.Length == 2, "Async session round trip.");

        var model = new MainViewModel();
        model.ImportSessionAsync(json).GetAwaiter().GetResult();
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window = new MainWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var grid = window.FindControl<DataGrid>("EventsGrid")!;
        Check(grid.ItemsSource.Cast<GcEvent>().Count() == 2, "EventsGrid ItemsSource binding.");
        Check(grid.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "BackgroundGC"), "Compiled event cell binding.");
        Check(grid.Columns.Single(column => Equals(column.Header, "Before")).CanUserSort, "Before sort property was trimmed.");
        var sorted = new DataGridCollectionView(events);
        sorted.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(GcEvent.BeforeBytes), ListSortDirection.Ascending));
        Check(sorted.Cast<GcEvent>().First().Number == 2, "Numeric Before sort.");
        var selectedEvent = model.EventLog.Single(gc => gc.Number == 1);
        model.SelectedEvent = selectedEvent;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Check(Equals(grid.SelectedItem, selectedEvent), "Compiled SelectedItem binding.");

        var history = window.GetVisualDescendants().OfType<MemoryHistoryView>().Single();
        var chart = history.FindControl<CartesianChart>("MemoryChart")!;
        var rss = chart.Series.OfType<LineSeries>().Single(series => series.Title == "RSS MiB");
        Check(rss.GetDataPoints().Select(point => point.Value).SequenceEqual(new[] { 10d, 12d }), "RSS chart getter was trimmed.");
        var heap = chart.Series.OfType<LineSeries>().Single(series => series.Title == "Managed heap MiB");
        Check(heap.GetDataPoints().Select(point => point.Value).SequenceEqual(new[] { 2d, 3d }), "Heap chart getter was trimmed.");
        var histogram = window.GetVisualDescendants().OfType<PauseHistogramView>().Single().FindControl<CartesianChart>("PauseChart")!;
        Check(histogram.Series.OfType<BarSeries>().Single().GetDataPoints().Sum(point => point.Value) == 1, "Histogram getter was trimmed.");
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
