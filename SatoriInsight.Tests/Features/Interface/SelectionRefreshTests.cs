using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class SelectionRefreshTests
{
    [AvaloniaFact]
    public async Task BoundGridsKeepSelectionWhileDataRefreshes()
    {
        var target = TestData.Target;
        var discovery = new Discovery
        {
            Targets = [target]
        };
        var session = new Session(target);
        var selected = new GcEvent(7, 1, 1, "NonConcurrentGC", "AllocSmall", 2, 1, 0.1);
        session.Frame = Frame([selected]);
        await using var vm = new MainViewModel(discovery, session);
        await vm.AttachAsync(target);
        await vm.RefreshProcessesAsync();
        var events = BoundGrid(nameof(MainViewModel.EventLog), nameof(MainViewModel.SelectedEvent), nameof(GcEvent.NumberLabel));
        var eventView = new DataGridCollectionView(vm.EventLog);
        events.ItemsSource = eventView;
        events.Columns[0].Header = "Event";
        events.Columns[0].SortMemberPath = nameof(GcEvent.EndSeconds);
        var processes = BoundGrid(nameof(MainViewModel.LocalProcesses), nameof(MainViewModel.SelectedLocalProcess), nameof(LocalDotNetProcess.Name));
        var window = new Window
        {
            DataContext = vm,
            Width = 600,
            Height = 500,
            Content = new StackPanel
            {
                Children =
                {
                    events,
                    processes
                }
            }
        };
        window.Show();

        try
        {
            RefreshLayout();
            events.SelectedItem = selected;
            processes.SelectedItem = target;
            Assert.Equal(selected, vm.SelectedEvent);
            vm.Tick(); // Even an unchanged counter refresh previously replaced ItemsSource.
            RefreshLayout();
            Assert.Equal(selected, events.SelectedItem);
            var arrivals = Enumerable.Range(0, 900).Select(i => new GcEvent(7, i + 2, 2, "NonConcurrentGC", "AllocSmall", 2, 1, 0.1)).ToArray();

            foreach (var count in new[] { 30, 250, 900 })
            {
                session.Frame = Frame([selected, .. arrivals.Take(count)]);
                vm.Tick();
                RefreshLayout();
                Assert.Equal(selected, events.SelectedItem);
                Assert.Equal(selected, vm.SelectedEvent);
                Assert.Equal("GC #7 · Gen1", vm.EventTitle);
                Assert.Contains(selected, vm.EventLog);
                Assert.Equal(count + 1, vm.Events.Count);
                Assert.Equal(count + 1, vm.EventLog.Count);
            }

            var header = events.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(control => control.Content is not null);
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            RefreshLayout();
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            RefreshLayout();
            Assert.Equal(eventView.Cast<GcEvent>().OrderByDescending(gc => gc.EndSeconds), eventView.Cast<GcEvent>());
            events.ScrollIntoView(vm.EventLog[450], events.Columns[0]);
            RefreshLayout();
            var scrollbar = events.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.Name == "PART_VerticalScrollbar");
            var offset = scrollbar.Value;
            Assert.True(offset > 0);
            session.Frame = Frame([selected, .. arrivals, new(1000, 1000, 2, "NonConcurrentGC", "AllocSmall", 2, 1, 0.1)]);
            vm.Tick();
            RefreshLayout();
            Assert.Equal(selected, events.SelectedItem);
            Assert.Equal(selected, vm.SelectedEvent);
            Assert.Equal(eventView.Cast<GcEvent>().OrderByDescending(gc => gc.EndSeconds), eventView.Cast<GcEvent>());
            Assert.Equal(offset, scrollbar.Value, precision: 3);

            events.SelectedItem = vm.EventLog[0];
            vm.GenerationFilterIndex = 2; // Gen 1 excludes the selected Gen 2 row.
            RefreshLayout();
            Assert.Null(events.SelectedItem);
            Assert.Null(vm.SelectedEvent);
            var refreshedTarget = target with
            {
                CommandLine = "dotnet TestTarget.dll --updated"
            };
            discovery.Targets = [target with
            {
                ProcessId = 5678,
                Name = "NewTarget"
            }, refreshedTarget];
            await vm.RefreshProcessesAsync();
            RefreshLayout();
            Assert.Equal(refreshedTarget, processes.SelectedItem);
            Assert.Equal(refreshedTarget.CommandLine, vm.SelectedLocalProcess?.CommandLine);
            Assert.True(vm.CanAttach);
            discovery.Targets = [refreshedTarget with
            {
                StartTimeUtc = target.StartTimeUtc.AddMinutes(1)
            }

            ];
            await vm.RefreshProcessesAsync();
            RefreshLayout();
            Assert.Null(processes.SelectedItem); // A reused PID is a different process.
            Assert.False(vm.CanAttach);
        }
        finally
        {
            window.Close();
        }

        void RefreshLayout()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static DataGrid BoundGrid(string items, string selection, string text)
    {
        var grid = new DataGrid
        {
            Height = 200,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(text) });
        grid.Bind(DataGrid.ItemsSourceProperty, new Binding(items));
        grid.Bind(DataGrid.SelectedItemProperty, new Binding(selection) { Mode = BindingMode.TwoWay });
        return grid;
    }

    private static DiagnosticFrame Frame(GcEvent[] events) => new(events.Max(e => e.EndSeconds) + 1, [], events, [], null, new(), new(), []);

    private sealed class Discovery : ILocalProcessDiscovery
    {
        public IReadOnlyList<LocalDotNetProcess> Targets { get; set; } = [];

        public Task<IReadOnlyList<LocalDotNetProcess>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Targets);
    }

    private sealed class Session(LocalDotNetProcess target) : ILiveTelemetrySession, ILiveTelemetrySessionFactory
    {
        public LocalDotNetProcess Target => target;

        public DiagnosticFrame Frame { get; set; } = null!;

        private readonly TaskCompletionSource<CollectionEnd> _end = new();

        public Task<CollectionEnd> Completion => _end.Task;

        public DiagnosticFrame Read() => Frame;

        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess process, CancellationToken cancellationToken = default) => Task.FromResult<ILiveTelemetrySession>(this);

        public ValueTask DisposeAsync()
        {
            _end.TrySetResult(new(false));
            return ValueTask.CompletedTask;
        }
    }
}
