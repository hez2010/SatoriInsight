using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SatoriInsight.Controls;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class HeapInspectorTests
{
    [AvaloniaFact]
    public async Task HeapTablesSortCountsSizesAddressesAndSignedChangesNumerically()
    {
        var baseline = Capture(0, [new("Small", 2, 512), new("Medium", 10, 2048), new("Large", 1000, 2097152)]);
        var current = Capture(1, [new("Small", 4, 1024), new("Medium", 1, 1024), new("Large", 500, 1048576)]);
        await using var vm = new MainViewModel();
        vm.Snapshots.Add(baseline);
        vm.Snapshots.Add(current);
        vm.BaselineSnapshot = baseline;
        vm.SelectedSnapshot = baseline;
        var view = new HeapInspectorView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 1000, Content = new ScrollViewer { Content = view } };
        window.Show();

        try
        {
            vm.InspectionTab = 1;
            Layout(window);
            var types = view.FindControl<DataGrid>("TypesGrid")!;
            ClickHeader(window, types, "Count");
            Assert.Equal(new[] { 2, 10, 1000 }, Rows<TypeStat>(types).Select(type => type.Count));
            ClickHeader(window, types, "Count");
            Assert.Equal(new[] { 1000, 10, 2 }, Rows<TypeStat>(types).Select(type => type.Count));
            ClickHeader(window, types, "Shallow bytes");
            Assert.Equal(new long[] { 512, 2048, 2097152 }, Rows<TypeStat>(types).Select(type => type.Bytes));

            vm.InspectionTab = 2;
            vm.ShowAllObjects();
            Layout(window);
            var objects = view.FindControl<DataGrid>("ObjectsGrid")!;
            ClickHeader(window, objects, "Address");
            Assert.Equal(new ulong[] { 0xF00, 0x1000, 0x10000 }, Rows<HeapObjectInfo>(objects).Select(obj => obj.Address));
            ClickHeader(window, objects, "Size");
            Assert.Equal(new long[] { 16, 512, 2048 }, Rows<HeapObjectInfo>(objects).Select(obj => obj.Size));

            vm.SelectedSnapshot = current;
            vm.InspectionTab = 4;
            Layout(window);
            var differences = view.FindControl<DataGrid>("TypeDifferencesGrid")!;
            ClickHeader(window, differences, "Change");
            Assert.Equal(new long[] { -1048576, -1024, 512 }, Rows<TypeGrowth>(differences).Select(type => type.DeltaBytes));
            ClickHeader(window, differences, "Change");
            Assert.Equal(new long[] { 512, -1024, -1048576 }, Rows<TypeGrowth>(differences).Select(type => type.DeltaBytes));
            ClickHeader(window, differences, "Baseline");
            Assert.Equal(new long[] { 512, 2048, 2097152 }, Rows<TypeGrowth>(differences).Select(type => type.BaselineBytes));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ComparisonPickersChangeBothCapturesAndExcludeRegionOnlyCaptures()
    {
        var metadata = Capture(0, []) with { MetadataOnly = true, Objects = [], Roots = [], Holes = [] };
        var first = Capture(1, [new("Node", 1, 512)]);
        var second = Capture(2, [new("Node", 2, 2048)]);
        var third = Capture(3, [new("Node", 3, 4096)]);
        await using var vm = new MainViewModel();

        foreach (var capture in new[] { metadata, first, second, third })
        {
            vm.Snapshots.Add(capture);
        }

        vm.SelectedSnapshot = second;
        vm.InspectionTab = 4;
        var view = new HeapInspectorView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 1000, Content = view };
        window.Show();

        try
        {
            Layout(window);
            var baselinePicker = view.FindControl<ComboBox>("BaselineCapturePicker")!;
            var comparisonPicker = view.FindControl<ComboBox>("ComparisonCapturePicker")!;
            baselinePicker.SelectedItem = first;
            Layout(window);
            Assert.True(vm.HasComparison);
            Assert.Equal(1536, Assert.Single(vm.TypeDifferences).DeltaBytes);

            baselinePicker.SelectedItem = third;
            Layout(window);
            Assert.Equal(-2048, Assert.Single(vm.TypeDifferences).DeltaBytes);
            comparisonPicker.SelectedItem = first;
            Layout(window);
            Assert.Equal(first, vm.SelectedSnapshot);
            Assert.Equal(-3584, Assert.Single(vm.TypeDifferences).DeltaBytes);

            baselinePicker.IsDropDownOpen = true;
            Layout(window);
            Assert.False(baselinePicker.ContainerFromItem(metadata)!.IsEnabled);
            Assert.True(baselinePicker.ContainerFromItem(second)!.IsEnabled);
            baselinePicker.IsDropDownOpen = false;

            baselinePicker.SelectedItem = first;
            Layout(window);
            Assert.False(vm.HasComparison);
            Assert.Empty(vm.TypeDifferences);
            Assert.False(view.FindControl<DataGrid>("TypeDifferencesGrid")!.IsVisible);

            vm.SelectedSnapshot = metadata;
            Layout(window);
            Assert.Equal(metadata, vm.SelectedSnapshot);
            Assert.Equal(0, vm.InspectionTab);

            vm.SelectedSnapshot = third;
            vm.BaselineSnapshot = third;
            vm.RemoveSnapshot();
            Layout(window);
            Assert.Equal(first, vm.BaselineSnapshot);
            Assert.Equal(second, vm.SelectedSnapshot);
            Assert.True(vm.HasComparison);
        }
        finally
        {
            window.Close();
        }
    }

    private static HeapProfile Capture(int minute, TypeStat[] types) => new(
        Guid.NewGuid(), new DateTime(2026, 10, 26, 0, minute, 0, DateTimeKind.Utc), null, "test", 0, 0,
        [new(1, 0, 0xF00, 0, 0x500000 - 0xF00, 2576, 0, HeapGeneration.Gen2, 0, 3, [], ObjectRangeEnd: 0x500000)], types,
        [new(0x10000, "Large", 2048, 1, HeapGeneration.Gen2, []), new(0xF00, "Small", 16, 1, HeapGeneration.Gen0, []),
            new(0x1000, "Medium", 512, 1, HeapGeneration.Gen1, [])], [], [], []);

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void ClickHeader(Window window, DataGrid grid, string title)
    {
        Layout(window);
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(control => Equals(control.Content, title));
        var position = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(position, MouseButton.Left, RawInputModifiers.None);
        Layout(window);
    }

    private static IEnumerable<T> Rows<T>(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>()
        .Where(row => row.IsEffectivelyVisible).OrderBy(row => row.Index).Select(row => Assert.IsType<T>(row.DataContext));
}
