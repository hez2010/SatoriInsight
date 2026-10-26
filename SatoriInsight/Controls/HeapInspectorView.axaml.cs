using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
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
    public HeapInspectorView() => InitializeComponent();

    private async void Capture_Click(object? s, RoutedEventArgs e) => await Vm.CaptureHeapAsync();

    private void Cancel_Click(object? s, RoutedEventArgs e) => Vm.CancelHeapOperation();

    private void ViewRegions_Click(object? s, RoutedEventArgs e) => Vm.ShowCapturedRegions();

    private void Remove_Click(object? s, RoutedEventArgs e) => Vm.RemoveSnapshot();

    private void Objects_Click(object? s, RoutedEventArgs e) => Vm.InspectTypeObjects();

    private void AllTypes_Click(object? s, RoutedEventArgs e) => Vm.ShowAllObjects();

    private async void Root_Click(object? s, RoutedEventArgs e) => await Vm.FindRootAsync();

    private void Follow_Click(object? s, RoutedEventArgs e) => Vm.FollowReference();

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
