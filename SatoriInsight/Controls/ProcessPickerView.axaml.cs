using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class ProcessPickerView : UserControl
{
    // Keep the properties inspected by DataGrid sorting.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(LocalDotNetProcess))]
    public ProcessPickerView() => InitializeComponent();

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.RefreshProcessesAsync();
        }
    }
}
