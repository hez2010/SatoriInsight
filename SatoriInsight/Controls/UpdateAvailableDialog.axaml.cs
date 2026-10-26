using FluentAvalonia.UI.Controls;

namespace SatoriInsight.Controls;

public partial class UpdateAvailableDialog : FAContentDialog
{
    public UpdateAvailableDialog() => InitializeComponent();

    protected override Type StyleKeyOverride => typeof(FAContentDialog);
}
