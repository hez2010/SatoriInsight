using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class HeapInspectorView : UserControl
{
    private MainViewModel Vm => (MainViewModel)DataContext!;

    public HeapInspectorView()
    {
        InitializeComponent();
        RegisterRowActivation(ReferencesGrid, () => Vm.FollowReference());
        RegisterRowActivation(RootPathGrid, InspectPathObject);
    }

    private async void Capture_Click(object? s, RoutedEventArgs e) => await Vm.CaptureHeapAsync();

    private void Cancel_Click(object? s, RoutedEventArgs e) => Vm.CancelHeapOperation();

    private void Remove_Click(object? s, RoutedEventArgs e) => Vm.RemoveSnapshot();

    private void AllTypes_Click(object? s, RoutedEventArgs e) => Vm.ShowAllObjects();

    private void Region_Focused(object? sender, EventArgs e) => Vm.ShowCapturedRegions();

    private void Object_Focused(object? sender, RoutedEventArgs e) => Vm.ShowObjectInspection();

    private void Back_Click(object? sender, RoutedEventArgs e) => Vm.GoBackToObject();

    private void InspectInstances_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TypeGrowth difference })
        {
            Vm.InspectInstances(difference);
            Dispatcher.UIThread.Post(() =>
            {
                TypesGrid.ScrollIntoView(TypesGrid.SelectedIndex);
                ObjectsGrid.Focus();
                ObjectsGrid.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

    private static void RegisterRowActivation(TableView table, Action activate)
    {
        table.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<TableViewRow>(includeSelf: true) is not null)
            {
                activate();
                e.Handled = true;
            }
        };

        table.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.Source is TableViewRow || e.Source == table))
            {
                activate();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void InspectPathObject()
    {
        if (RootPathGrid.SelectedItem is RootPathStep step && Vm.SelectedSnapshot?.Objects.FirstOrDefault(obj => obj.Address == step.Address) is { } obj)
        {
            Vm.InspectObject(obj);
        }
    }

    private async void Dump_Click(object? s, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open managed process dump",
            AllowMultiple = false
        };
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(options);

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            await Vm.CaptureHeapAsync(path);
        }
    }
}
