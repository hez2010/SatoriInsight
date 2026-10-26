using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class HeapInspectorView : UserControl
{
    private MainViewModel Vm => (MainViewModel)DataContext!;

    // Keep the properties inspected by DataGrid sorting.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(TypeStat))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(HeapObjectInfo))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ObjectReference))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(RootPathStep))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(TypeGrowth))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(FreeHole))]
    public HeapInspectorView() => InitializeComponent();

    private async void Capture_Click(object? s, RoutedEventArgs e) => await Vm.CaptureHeapAsync();

    private void Cancel_Click(object? s, RoutedEventArgs e) => Vm.CancelHeapOperation();

    private void Remove_Click(object? s, RoutedEventArgs e) => Vm.RemoveSnapshot();

    private void AllTypes_Click(object? s, RoutedEventArgs e) => Vm.ShowAllObjects();

    private void Region_Focused(object? sender, EventArgs e) => Vm.ShowCapturedRegions();

    private void Object_Focused(object? sender, RoutedEventArgs e) => Vm.ShowObjectInspection();

    private void Back_Click(object? sender, RoutedEventArgs e) => Vm.GoBackToObject();

    private void Reference_DoubleTapped(object? sender, TappedEventArgs e) => Vm.FollowReference();

    private void Reference_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.FollowReference();
            e.Handled = true;
        }
    }

    private void RootPath_DoubleTapped(object? sender, TappedEventArgs e) => InspectPathObject(sender);

    private void RootPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            InspectPathObject(sender);
            e.Handled = true;
        }
    }

    private void InspectPathObject(object? sender)
    {
        if (sender is DataGrid { SelectedItem: RootPathStep step } && Vm.SelectedSnapshot?.Objects.FirstOrDefault(obj => obj.Address == step.Address) is { } obj)
        {
            Vm.InspectObject(obj);
        }
    }

    private void RevealSelectedObject(object? sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (grid.IsEffectivelyVisible && grid.SelectedItem is { } item)
                {
                    grid.ScrollIntoView(item, null);
                }
            }, DispatcherPriority.Loaded);
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
