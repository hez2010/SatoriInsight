using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class RegionsView : UserControl
{
    private RegionMapViewModel ViewModel => (RegionMapViewModel)DataContext!;

    // Keep the properties inspected by DataGrid sorting.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(FreeHole))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(HeapObjectInfo))]
    public RegionsView() => InitializeComponent();

    private void RegionObjects_Click(object? sender, RoutedEventArgs e) => ViewModel.InspectRegionObjects();

    private void RegionObject_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.SelectedRegionObject is { } obj)
        {
            ViewModel.InspectRegionObject(obj);
        }
    }
}
