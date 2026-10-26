using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
#if AVALONIA_PRO
using Avalonia.Controls.Charts;
using Avalonia.Threading;
using SatoriInsight.ViewModels;
#endif

namespace SatoriInsight.Controls;

public partial class PauseHistogramView : UserControl
{
    // Chart CategoryPath and ValuePath resolve properties at runtime.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Models.HistogramBin))]
    public PauseHistogramView() => InitializeComponent();

#if AVALONIA_PRO
    private void PauseChart_DataPointClicked(object? sender, DataPointClickedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.DataItem is Models.HistogramBin bin)
        {
            vm.FilterToPauseBin(bin);

            var log = TopLevel.GetTopLevel(this)?.FindControl<DataGrid>("EventsGrid");
            Dispatcher.UIThread.Post(() => log?.BringIntoView());
        }
    }
#endif
}
