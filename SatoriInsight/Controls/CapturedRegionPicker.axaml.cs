using Avalonia.Controls;
using Avalonia.Interactivity;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class CapturedRegionPicker : UserControl
{
    public event EventHandler? RegionFocused;

    public CapturedRegionPicker() => InitializeComponent();

    private void Region_Focused(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RegionMapViewModel { SelectedRegion: not null })
        {
            RegionFocused?.Invoke(this, EventArgs.Empty);
        }
    }
}
