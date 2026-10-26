using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using SatoriInsight.Controls;
using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;

namespace SatoriInsight;

public partial class MainWindow : FAAppWindow
{
    public MainViewModel ViewModel { get; }

    private readonly DispatcherTimer _replayTimer;
    private int _refreshQueued;
    private readonly LaunchProcessViewModel _launchSettings = new();
    private bool _cleanupComplete;
    private bool _closing;

    public MainWindow() : this(new MainViewModel())
    {
    }

    // DataGrid sorting resolves property names at runtime, even with compiled cell bindings.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(GcEvent))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(GcGenerationStats))]
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 64;
        TitleBar.BackgroundColor = Color.Parse("#101620");
        TitleBar.ForegroundColor = Color.Parse("#E5EBF2");
        TitleBar.ButtonBackgroundColor = Color.Parse("#101620");
        TitleBar.ButtonForegroundColor = Color.Parse("#E5EBF2");
        // Leave room for the platform caption buttons. Handle client-area dragging
        // explicitly because the extended header can receive ordinary pointer events.
        HeaderContent.Margin = OperatingSystem.IsWindows() ? new(0, 0, 144, 0) : new(0);
        _replayTimer = new DispatcherTimer
        {
            Interval = EventPipeCollector.UpdateInterval
        };
        _replayTimer.Tick += (_, _) => ViewModel.Tick();
        ViewModel.RefreshRequested += QueueLiveRefresh;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Opened += (_, _) =>
        {
            UpdateReplayTimer();
            _ = CheckForUpdatesAsync();
        };
        Closed += (_, _) =>
        {
            _windowLifetime.Cancel();
            _windowLifetime.Dispose();
            _replayTimer.Stop();
            ViewModel.RefreshRequested -= QueueLiveRefresh;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
        Closing += Window_Closing;
    }

    private void QueueLiveRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);

            if (!_closing && !_cleanupComplete && !ViewModel.IsReplay)
            {
                ViewModel.Tick();
            }
        });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsReplay))
        {
            UpdateReplayTimer();
        }
    }

    private void UpdateReplayTimer()
    {
        if (!_closing && !_cleanupComplete && IsVisible && ViewModel.IsReplay)
        {
            _replayTimer.Start();
        }
        else
        {
            _replayTimer.Stop();
        }
    }

    private void TitleBarArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }

        // Walk only within the header: the window itself is focusable. Buttons and
        // other interactive descendants must keep their normal pointer behavior.
        if (source.GetSelfAndVisualAncestors().TakeWhile(visual => visual != TitleBarArea).OfType<InputElement>().Any(element => element.Focusable))
        {
            return;
        }

        e.Handled = true;

        if (e.ClickCount == 2)
        {
            if (CanResize)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    private async void SelectProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanSelectProcess)
        {
            return;
        }

        var dialog = new ProcessPickerDialog
        {
            DataContext = ViewModel
        };
        var refresh = ViewModel.RefreshProcessesAsync();
        LocalDotNetProcess? target = null;

        try
        {
            if (await ShowDialogAsync(dialog) == FAContentDialogResult.Primary)
            {
                target = ViewModel.SelectedLocalProcess;
            }
        }
        finally
        {
            // A detached grid still observes the shared process list and can clear
            // the next picker's selection when that list refreshes.
            dialog.DataContext = null;
        }

        await refresh;

        if (target is not null)
        {
            await ViewModel.AttachAsync(target);
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e) => await ViewModel.DisconnectAsync();

    private async void LaunchProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanSelectProcess)
        {
            return;
        }

        if (ViewModel.HasLaunchedProcess)
        {
            await ViewModel.StopProcessAsync();
            return;
        }

        _launchSettings.Error = "";
        var dialog = new LaunchProcessDialog
        {
            DataContext = _launchSettings
        };
        FAContentDialogResult result;

        try
        {
            result = await ShowDialogAsync(dialog);
        }
        finally
        {
            dialog.DataContext = null;
        }

        if (result == FAContentDialogResult.Primary && dialog.Options is { } options)
        {
            await ViewModel.LaunchAsync(options);
        }
    }

    private void Replay_Click(object? sender, RoutedEventArgs e) => ViewModel.ToggleReplay();

    private async void ImportSession_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sessionType = new FilePickerFileType("JSON session")
            {
                Patterns = ["*.json"]
            };
            var options = new FilePickerOpenOptions
            {
                Title = "Open recorded diagnostics session",
                AllowMultiple = false,
                FileTypeFilter = [sessionType]
            };

            var files = await StorageProvider.OpenFilePickerAsync(options);

            if (files.FirstOrDefault() is not { } file)
            {
                return;
            }

            await using var stream = await file.OpenReadAsync();
            await ViewModel.ImportSessionAsync(stream);
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Session import failed: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        _windowLifetime.Cancel();

        if (_cleanupComplete)
        {
            return;
        }

        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        _replayTimer.Stop();
        var cleanup = ViewModel.DisposeAsync();

        if (cleanup.IsCompletedSuccessfully)
        {
            _cleanupComplete = true;
            return;
        }

        _closing = true;
        e.Cancel = true;
        _ = CloseAfterCleanupAsync(cleanup);
    }

    private async Task CloseAfterCleanupAsync(ValueTask cleanup)
    {
        try
        {
            await cleanup;
        }
        finally
        {
            _cleanupComplete = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private void Navigation_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is FANavigationViewItem { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.NavigationIndex = index;
        }
    }

    private async void ExportSession_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sessionType = new FilePickerFileType("JSON session")
            {
                Patterns = ["*.json"]
            };
            var options = new FilePickerSaveOptions
            {
                Title = "Export diagnostics session",
                SuggestedFileName = $"heap-process-{ViewModel.ActiveProcess?.ProcessId}.json",
                DefaultExtension = "json",
                FileTypeChoices = [sessionType]
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);

            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await ViewModel.ExportSessionAsync(stream);
            ViewModel.Status = "Diagnostics session exported.";
        }
        catch (Exception exception)
        {
            ViewModel.Status = $"Export failed: {exception.Message}";
        }
    }
}
