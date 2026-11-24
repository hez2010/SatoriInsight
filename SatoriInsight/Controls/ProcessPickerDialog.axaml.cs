using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class ProcessPickerDialog : FAContentDialog
{
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
