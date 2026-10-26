using FluentAvalonia.UI.Controls;
using SatoriInsight.Controls;
using SatoriInsight.Services.Updates;

namespace SatoriInsight;

public partial class MainWindow
{
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _updateCheckStarted;

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;

        try
        {
            var cancellationToken = _windowLifetime.Token;
            var currentVersion = typeof(App).Assembly.GetName().Version;

            if (currentVersion is null)
            {
                return;
            }

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            var update = await new UpdateChecker(client).CheckAsync(currentVersion, cancellationToken);

            if (update is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var dialog = new UpdateAvailableDialog { DataContext = update };

            if (await ShowDialogAsync(dialog) == FAContentDialogResult.Primary && !cancellationToken.IsCancellationRequested)
            {
                await Launcher.LaunchUriAsync(update.ReleaseUri);
            }
        }
        catch (Exception)
        {
            // An unavailable update service, dialog, or browser must not interrupt the profiler.
        }
    }

    private async Task<FAContentDialogResult> ShowDialogAsync(FAContentDialog dialog)
    {
        if (_closing || _cleanupComplete)
        {
            return FAContentDialogResult.None;
        }

        try
        {
            await _dialogGate.WaitAsync(_windowLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return FAContentDialogResult.None;
        }

        try
        {
            if (_closing || _cleanupComplete || !IsVisible)
            {
                return FAContentDialogResult.None;
            }

            return await dialog.ShowAsync(this);
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}
