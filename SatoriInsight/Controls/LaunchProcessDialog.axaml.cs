using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using SatoriInsight.Models;
using SatoriInsight.ViewModels;

namespace SatoriInsight.Controls;

public partial class LaunchProcessDialog : FAContentDialog
{
    public LaunchProcessDialog() => InitializeComponent();

    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    public ProcessLaunchOptions? Options { get; private set; }

    private LaunchProcessViewModel ViewModel => (LaunchProcessViewModel)DataContext!;

    private void Launch_Click(FAContentDialog sender, FAContentDialogButtonClickEventArgs e)
    {
        Options = ViewModel.CreateOptions();
        e.Cancel = Options is null;
    }

    private async void BrowseApplication_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var applicationType = new FilePickerFileType("Applications")
            {
                Patterns = ["*.exe", "*.dll"]
            };

            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose application",
                AllowMultiple = false,
                FileTypeFilter = OperatingSystem.IsWindows()
                    ? [applicationType, FilePickerFileTypes.All]
                    : [FilePickerFileTypes.All]
            });

            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                ViewModel.Application = path;
            }
        }
        catch (Exception exception)
        {
            ViewModel.Error = exception.Message;
        }
    }

    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                Title = "Choose working directory",
                AllowMultiple = false
            };
            var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(options);

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                ViewModel.WorkingDirectory = path;
            }
        }
        catch (Exception exception)
        {
            ViewModel.Error = exception.Message;
        }
    }
}
