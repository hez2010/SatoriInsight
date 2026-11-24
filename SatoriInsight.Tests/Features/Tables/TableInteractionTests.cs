using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SatoriInsight.Controls;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public sealed class TableInteractionTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new());

    [Fact]
    public Task FilteringSortedProcessesKeepsTheMatchingSelection() => WithWindow(async window =>
    {
        var target = TestData.Target;
        var other = target with { ProcessId = 20, Name = "Other" };
        var child = target with { ProcessId = 100, Name = target.Name + ".Child" };
        await using var vm = new MainViewModel(new Discovery([target, other, child]));
        await vm.RefreshProcessesAsync();
        var picker = new ProcessPickerDialog { DataContext = vm };
        var opened = new TaskCompletionSource();
        picker.Opened += (_, _) => opened.SetResult();
        var dialog = picker.ShowAsync(window);
        await opened.Task;
        var processes = picker.FindControl<TableView>("ProcessGrid")!;
        processes.SelectedItem = target;
        SortBy(window, processes, "PID");
        Assert.Equal([other, child, target], processes.ItemsView.Cast<LocalDotNetProcess>());

        vm.ProcessSearch = target.Name;
        Assert.Equal([child, target], processes.ItemsView.Cast<LocalDotNetProcess>());
        Assert.Same(target, processes.SelectedItem);
        Assert.True(picker.IsPrimaryButtonEnabled);

        vm.ProcessSearch = other.Name;
        Assert.Null(vm.SelectedLocalProcess);
        Assert.False(picker.IsPrimaryButtonEnabled);
        picker.Hide();
        await dialog;
    });

    [Fact]
    public Task SortingHeapAddressesKeepsTheObjectAndEnterFollowsItsReference() => WithWindow(async window =>
    {
        var reference = new ObjectReference(0x20, "child");
        var parent = new HeapObjectInfo(0x100, "Parent", 32, 1, HeapGeneration.Gen2, [reference]);
        var child = new HeapObjectInfo(reference.Address, "Child", 2048, 1, HeapGeneration.Gen2, []);
        var snapshot = new HeapProfile(Guid.NewGuid(), DateTime.UtcNow, null, "CoreCLR", 1, 1, [],
            [new(parent.Type, 1, parent.Size), new(child.Type, 1, child.Size)], [parent, child], [], [], []);
        await using var vm = new MainViewModel { SelectedSnapshot = snapshot, SelectedObject = parent };
        var inspector = new HeapInspectorView { DataContext = vm };
        window.Content = inspector;
        window.UpdateLayout();
        var objects = inspector.FindControl<TableView>("ObjectsGrid")!;
        var references = inspector.FindControl<TableView>("ReferencesGrid")!;
        references.SelectedItem = reference;
        SortBy(window, objects, "Address");
        Assert.Equal([child, parent], objects.ItemsView.Cast<HeapObjectInfo>());
        Assert.Same(parent, objects.SelectedItem);
        Assert.Same(reference, vm.SelectedReference);

        Assert.True(references.ContainerFromItem(reference)!.Focus());
        PressEnter(window);
        Assert.Same(child, vm.SelectedObject);
        vm.GoBackToObject();
        Assert.Same(parent, objects.SelectedItem);
    });

    private static async Task WithWindow(Func<Window, Task> test)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(TableInteractionTests));
        await session.Dispatch(async () =>
        {
            var window = new Window { Width = 1200, Height = 1000 };
            window.Show();

            try
            {
                await test(window);
                return true;
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void SortBy(Window window, TableView table, string header)
    {
        var column = table.GetVisualDescendants().OfType<TableViewColumnHeader>().Single(c => Equals(c.Content, header));
        Assert.True(column.Focus());
        PressEnter(window);
    }

    private static void PressEnter(Window window)
    {
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    }

    private sealed class Discovery(IReadOnlyList<LocalDotNetProcess> processes) : ILocalProcessDiscovery
    {
        public Task<IReadOnlyList<LocalDotNetProcess>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(processes);
    }
}
