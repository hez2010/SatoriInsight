using System.Diagnostics.CodeAnalysis;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class ProcessPickerDialog : FAContentDialog
{
    // Keep the properties inspected by DataGrid sorting.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(LocalDotNetProcess))]
    public ProcessPickerDialog() => InitializeComponent();

    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.RefreshProcessesAsync();
        }
    }
}
