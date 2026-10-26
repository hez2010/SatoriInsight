using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using SatoriInsight.Controls;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class ProcessLaunchTests
{
    [Fact]
    public void EnvironmentEditorPreservesValuesAndRejectsInvalidRows()
    {
        var vm = new LaunchProcessViewModel
        {
            Application = Environment.ProcessPath!,
            Arguments = "--name \"two words\"",
            EnvironmentVariables = "FIRST=has=equals\r\nEMPTY=\r\nPADDED=  keep spaces  \r\n"
        };
        var options = Assert.IsType<ProcessLaunchOptions>(vm.CreateOptions());
        Assert.Equal("has=equals", options.EnvironmentVariables["FIRST"]);
        Assert.Equal("", options.EnvironmentVariables["EMPTY"]);
        Assert.Equal("  keep spaces  ", options.EnvironmentVariables["PADDED"]);
        var start = LocalProcessLauncher.CreateStartInfo(options);
        Assert.False(start.UseShellExecute);
        Assert.False(start.CreateNoWindow);
        Assert.Equal(options.Arguments, start.Arguments);
        Assert.Equal("1", start.Environment["DOTNET_DefaultDiagnosticPortSuspend"]);
        vm.EnvironmentVariables = "INVALID";
        Assert.Null(vm.CreateOptions());
        Assert.Contains("NAME=value", vm.Error);
        vm.EnvironmentVariables = "FIRST=one\nFIRST=two";
        Assert.Null(vm.CreateOptions());
        Assert.Contains("more than once", vm.Error);
        vm.EnvironmentVariables = "DOTNET_EnableDiagnostics=0";
        Assert.Null(vm.CreateOptions());
        Assert.Contains("disables recording", vm.Error);
        vm.EnvironmentVariables = "DOTNET_DefaultDiagnosticPortSuspend=0";
        Assert.Null(vm.CreateOptions());
        Assert.Contains("startup", vm.Error);
        vm.EnvironmentVariables = "";
        vm.Application = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        Assert.Null(vm.CreateOptions());
        Assert.Contains("not found", vm.Error);
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchesAndRecordsStartupWithArgumentsAndChildEnvironment(bool useDll)
    {
        using var files = new LaunchFiles();
        var inheritedName = "INSIGHT_INHERITED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(inheritedName, "inherited-value");
        var beforeCustom = Environment.GetEnvironmentVariable("INSIGHT_LAUNCH_TEST_VALUE");
        Process? child = null;

        try
        {
            var editor = new LaunchProcessViewModel
            {
                Application = files.Application(useDll),
                WorkingDirectory = files.Directory,
                Arguments = $"--launch-probe \"{files.Report}\" {inheritedName} \"two words\" \"\" literal=equals",
                EnvironmentVariables = "INSIGHT_LAUNCH_TEST_VALUE=child-only-secret=42\nINSIGHT_LAUNCH_TEST_EMPTY="
            };
            var options = Assert.IsType<ProcessLaunchOptions>(editor.CreateOptions());
            await using var vm = new MainViewModel();
            await vm.LaunchAsync(options);
            Assert.True(vm.IsConnected, vm.Status);
            Assert.True(vm.HasLaunchedProcess);
            Assert.Equal("Stop process", vm.LaunchProcessButtonLabel);
            child = Process.GetProcessById(vm.ActiveProcess!.ProcessId);
            var clock = Stopwatch.StartNew();

            while ((!File.Exists(files.Report) || !vm.Events.Any(e => e.Generation == 2)) && clock.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                vm.Tick();
            }

            Assert.True(File.Exists(files.Report), vm.Status);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(files.Report, TestContext.Current.CancellationToken));
            var root = report.RootElement;
            Assert.Equal(new[] { "two words", "", "literal=equals" }, root.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(files.Directory, root.GetProperty("WorkingDirectory").GetString());
            Assert.Equal("child-only-secret=42", root.GetProperty("Custom").GetString());
            Assert.Equal("inherited-value", root.GetProperty("Inherited").GetString());
            Assert.True(root.GetProperty("Empty").ValueKind == JsonValueKind.Null || root.GetProperty("Empty").GetString() == "");
            Assert.Equal(beforeCustom, Environment.GetEnvironmentVariable("INSIGHT_LAUNCH_TEST_VALUE"));
            Assert.Contains(vm.Events, e => e.Generation == 2); // Includes the GC at the very beginning of Main.
            Assert.DoesNotContain("child-only-secret", vm.ExportJson());
            Assert.DoesNotContain("two words", vm.ExportJson());
            await vm.DisconnectAsync();
            Assert.False(vm.IsConnected);
            Assert.False(vm.HasLaunchedProcess);
            Assert.Equal("Launch process", vm.LaunchProcessButtonLabel);
            await vm.StopProcessAsync();
            Assert.False(child.HasExited);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedName, null);

            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(3000);
                }

                child.Dispose();
            }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StopTerminatesTheLaunchedProcessAndPreservesItsRecording()
    {
        using var files = new LaunchFiles();
        await using var vm = new MainViewModel();
        await vm.LaunchAsync(new(files.Application(true), $"--launch-probe \"{files.Report}\" UNUSED", "", new Dictionary<string, string>()));
        Assert.True(vm.IsConnected, vm.Status);
        using var child = Process.GetProcessById(vm.ActiveProcess!.ProcessId);

        try
        {
            var timeout = Stopwatch.StartNew();

            while (!vm.Events.Any() && timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                vm.Tick();
            }

            Assert.NotEmpty(vm.Events);
            await vm.StopProcessAsync();
            Assert.True(child.HasExited, vm.Status);
            Assert.False(vm.IsConnected);
            Assert.False(vm.HasLaunchedProcess);
            Assert.Equal("Launch process", vm.LaunchProcessButtonLabel);
            Assert.True(vm.CanSelectProcess);
            Assert.NotEmpty(SessionStorage.Read(vm.ExportJson()).Collections);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndingALaunchedSessionClearsItsStopAction(bool targetExited)
    {
        var launcher = new DeferredLauncher();
        var session = new FakeLaunchedSession();
        launcher.Result.SetResult(session);
        await using var vm = new MainViewModel(processLauncher: launcher);
        await vm.LaunchAsync(new("test", "", "", new Dictionary<string, string>()));
        Assert.Equal("Stop process", vm.LaunchProcessButtonLabel);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        if (targetExited)
        {
            session.End.SetResult(new(true));
            vm.Tick();
        }
        else
        {
            await vm.DisconnectAsync();
        }

        Assert.False(vm.HasLaunchedProcess);
        Assert.Equal("Launch process", vm.LaunchProcessButtonLabel);
        Assert.Contains(nameof(MainViewModel.LaunchProcessButtonLabel), notifications);
        await vm.StopProcessAsync();
    }

    [Fact]
    public async Task DisconnectWaitsForAnInProgressStopWithoutDisposingTheProcessEarly()
    {
        var launcher = new DeferredLauncher();
        var session = new FakeLaunchedSession
        {
            StopPending = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        launcher.Result.SetResult(session);
        await using var vm = new MainViewModel(processLauncher: launcher);
        await vm.LaunchAsync(new("test", "", "", new Dictionary<string, string>()));
        var stop = vm.StopProcessAsync();
        Assert.False(vm.CanSelectProcess);
        Assert.False(vm.CanOpenSession);
        Assert.False(vm.CanCaptureHeap);
        var disconnect = vm.DisconnectAsync();
        Assert.False(disconnect.IsCompleted);
        Assert.False(session.Disposed);
        await vm.StopProcessAsync();
        session.StopPending.SetResult();
        await Task.WhenAll(stop, disconnect);
        Assert.True(session.Disposed);
        Assert.False(vm.HasLaunchedProcess);
        Assert.Equal("Launch process", vm.LaunchProcessButtonLabel);
    }

    [Fact(Timeout = 15000)]
    public async Task CancelBeforeRecordingStopsOnlyTheNewlyLaunchedProcess()
    {
        using var files = new LaunchFiles();
        var factory = new WaitingFactory();
        var launcher = new LocalProcessLauncher(factory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = launcher.LaunchAsync(new(
            files.Application(true), $"--launch-probe \"{files.Report}\" UNUSED", "", new Dictionary<string, string>()), cancellation.Token);
        var target = await factory.Target.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var child = Process.GetProcessById(target.ProcessId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(files.Report)); // Main was never resumed.
    }

    [Fact(Timeout = 15000)]
    public async Task CollectionFailureCleansUpTheSuspendedChild()
    {
        using var files = new LaunchFiles();
        using var factory = new FailingFactory();
        var launcher = new LocalProcessLauncher(factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(new(
            files.Application(true), $"--launch-probe \"{files.Report}\" UNUSED", "", new Dictionary<string, string>()), TestContext.Current.CancellationToken));
        Assert.Equal("Test connection failure", error.Message);
        Assert.NotNull(factory.Child);
        await factory.Child.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(files.Report));
    }

    [Fact]
    public async Task CanceledLaunchDoesNotAdoptALateSession()
    {
        var launcher = new DeferredLauncher();
        await using var vm = new MainViewModel(processLauncher: launcher);
        var pending = vm.LaunchAsync(new("test", "", "", new Dictionary<string, string>()));
        Assert.True(vm.IsConnecting);
        Assert.Equal("Cancel launch", vm.DisconnectButtonLabel);
        var canceled = vm.DisconnectAsync();
        var session = new FakeLaunchedSession();
        launcher.Result.SetResult(session);
        await Task.WhenAll(pending, canceled);
        Assert.False(session.Accepted);
        Assert.True(session.Disposed);
        Assert.False(vm.IsConnected);
        Assert.True(vm.CanSelectProcess);
    }

    [AvaloniaFact]
    public async Task LaunchDialogEditsArgumentsEnvironmentAndValidation()
    {
        var vm = new LaunchProcessViewModel();
        var view = new LaunchProcessDialog
        {
            DataContext = vm
        };
        var window = new Window
        {
            Width = 700,
            Height = 600
        };
        window.Show();
        var result = view.ShowAsync(window);

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var launch = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Launch"));
            Assert.False(launch.IsEnabled);
            view.FindControl<TextBox>("LaunchApplication")!.Text = Environment.ProcessPath!;
            view.FindControl<TextBox>("LaunchArguments")!.Text = "--name \"two words\"";
            view.FindControl<TextBox>("LaunchEnvironment")!.Text = "ONE=1\nTWO=two";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(vm.CanLaunch);
            Assert.True(launch.IsEnabled);
            Assert.Equal("--name \"two words\"", vm.Arguments);
            view.FindControl<TextBox>("LaunchEnvironment")!.Text = "bad row";
            Dispatcher.UIThread.RunJobs();
            launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(result.IsCompleted);
            Assert.True(view.FindControl<TextBlock>("LaunchError")!.IsVisible);
            view.FindControl<TextBox>("LaunchEnvironment")!.Text = "ONE=1\nTWO=two";
            Dispatcher.UIThread.RunJobs();
            launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(FAContentDialogResult.Primary, await result);
            Assert.NotNull(view.Options);
            Assert.Equal("two", view.Options.EnvironmentVariables["TWO"]);
            Assert.Equal("--name \"two words\"", view.Options.Arguments);
        }
        finally
        {
            if (!result.IsCompleted)
            {
                view.Hide();
                await result;
            }

            view.DataContext = null;
            window.Close();
        }
    }

    private sealed class WaitingFactory : ILiveTelemetrySessionFactory
    {
        public TaskCompletionSource<LocalDotNetProcess> Target { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default)
        {
            Target.TrySetResult(target);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class FailingFactory : ILiveTelemetrySessionFactory, IDisposable
    {
        public Process? Child;

        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default)
        {
            Child = Process.GetProcessById(target.ProcessId);
            throw new InvalidOperationException("Test connection failure");
        }

        public void Dispose() => Child?.Dispose();
    }

    private sealed class DeferredLauncher : ILocalProcessLauncher
    {
        public TaskCompletionSource<ILaunchedTelemetrySession> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ILaunchedTelemetrySession> LaunchAsync(ProcessLaunchOptions options, CancellationToken token) => Result.Task;
    }

    private sealed class FakeLaunchedSession : ILaunchedTelemetrySession
    {
        public bool Accepted;
        public bool Disposed;
        public TaskCompletionSource? StopPending;

        public TaskCompletionSource<CollectionEnd> End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LocalDotNetProcess Target => TestData.Target;

        public Task<CollectionEnd> Completion => End.Task;

        public DiagnosticFrame Read() => TestData.Frame(1);

        public void Accept() => Accepted = true;

        public Task StopProcessAsync()
        {
            return StopPending?.Task ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            End.TrySetResult(new(false));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LaunchFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Insight launch " + Guid.NewGuid().ToString("N"));

        public string Report => Path.Combine(Directory, "result.json");

        public string Application(bool dll) => Path.Combine(Directory, "SatoriInsight.Workload" + (dll ? ".dll" : OperatingSystem.IsWindows() ? ".exe" : ""));

        public LaunchFiles()
        {
            var source = Path.GetDirectoryName(TestPaths.ManagedWorkload("net10.0"))!;

            System.IO.Directory.CreateDirectory(Directory);

            foreach (var file in System.IO.Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(Directory, Path.GetFileName(file)));
            }
        }

        public void Dispose()
        {
            foreach (var file in System.IO.Directory.GetFiles(Directory))
            {
                File.Delete(file);
            }

            System.IO.Directory.Delete(Directory);
        }
    }
}
