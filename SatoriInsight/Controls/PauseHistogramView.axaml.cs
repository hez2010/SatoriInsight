using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
#if AVALONIA_PRO
using Avalonia.Controls.Charts;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;
#endif

namespace SatoriInsight.Controls;

public partial class PauseHistogramView : UserControl
{
    // Chart CategoryPath and ValuePath resolve properties at runtime.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Models.HistogramBin))]
    public PauseHistogramView() => InitializeComponent();

#if AVALONIA_PRO
    private void PauseInterval_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PauseInterval interval })
        {
            InspectPauseInterval(interval);
        }
    }

    private void PauseChart_DataPointClicked(object? sender, DataPointClickedEventArgs e)
    {
        if (e.DataItem is PauseInterval interval)
        {
            InspectPauseInterval(interval);
        }
    }

    private void InspectPauseInterval(PauseInterval interval)
    {
        ((MainViewModel)DataContext!).FilterToPauseBin(interval);
        var log = TopLevel.GetTopLevel(this)?.FindControl<TableView>("EventsGrid");
        Dispatcher.UIThread.Post(() =>
        {
            if (log?.SelectedItem is { } selected)
            {
                log.ScrollIntoView(selected);
            }

            log?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
#endif
}
