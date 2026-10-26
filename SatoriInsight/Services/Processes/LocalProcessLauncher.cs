using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;

namespace SatoriInsight.Services.Processes;

public interface ILocalProcessLauncher
{
    Task<ILaunchedTelemetrySession> LaunchAsync(ProcessLaunchOptions options, CancellationToken token);
}

public interface ILaunchedTelemetrySession : ILiveTelemetrySession
{
    // Until accepted by the UI, disposal rolls back the newly launched process.
    void Accept();

    Task StopProcessAsync();
}

public sealed class LocalProcessLauncher(ILiveTelemetrySessionFactory sessionFactory) : ILocalProcessLauncher
{
    public Task<ILaunchedTelemetrySession> LaunchAsync(ProcessLaunchOptions options, CancellationToken token) => Task.Run(() => LaunchCoreAsync(options, token), token);

    private async Task<ILaunchedTelemetrySession> LaunchCoreAsync(ProcessLaunchOptions options, CancellationToken token)
    {
        var start = CreateStartInfo(options);
        token.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new InvalidOperationException("The process could not be started.");
        ILiveTelemetrySession? session = null;

        // Killing only this owned process on cancellation also unblocks a pending
        // synchronous ResumeRuntime call; the diagnostics library exposes no public async overload.
        using var canceled = token.Register(() => StopOwnedProcess(process));

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The process exited before recording could start (exit code {process.ExitCode}).");
                }

                if (DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
                {
                    break;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            var name = Path.GetFileNameWithoutExtension(options.Application.Trim());
            var target = new LocalDotNetProcess(
                process.Id, name, process.StartTime.ToUniversalTime(),
                QuoteArgument(start.FileName) + (start.Arguments.Length == 0 ? "" : " " + start.Arguments));
            session = await sessionFactory.StartAsync(target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            new DiagnosticsClient(process.Id).ResumeRuntime();
            token.ThrowIfCancellationRequested();

            return new LaunchedSession(process, session);
        }
        catch
        {
            StopOwnedProcess(process);

            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the launch failure.
                }
            }

            process.Dispose();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    public static ProcessStartInfo CreateStartInfo(ProcessLaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Application))
        {
            throw new ArgumentException("Choose an application to launch.");
        }

        if (options.Arguments.Contains('\0'))
        {
            throw new ArgumentException("Arguments cannot contain a null character.");
        }

        var directory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Environment.CurrentDirectory : Path.GetFullPath(options.WorkingDirectory.Trim());

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException("The working directory does not exist.");
        }

        var start = new ProcessStartInfo
        {
            UseShellExecute = false
        };

        ApplyEnvironmentOverrides(start, options.EnvironmentVariables);
        ConfigureStartupDiagnostics(start, options.EnvironmentVariables);

        var application = ResolveApplication(options.Application.Trim(), directory, start.Environment);
        start.WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Path.GetDirectoryName(application)! : directory;

        if (Path.GetExtension(application).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = ResolveApplication("dotnet", directory, start.Environment);
            start.Arguments = QuoteArgument(application) + (options.Arguments.Length == 0 ? "" : " " + options.Arguments);
        }
        else
        {
            start.FileName = application;
            start.Arguments = options.Arguments;
        }

        return start;
    }

    private static void ApplyEnvironmentOverrides(ProcessStartInfo start, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                throw new ArgumentException("Environment variables must have a name and cannot contain null characters.");
            }

            start.Environment[name] = value;
        }
    }

    private static void ConfigureStartupDiagnostics(ProcessStartInfo start, IReadOnlyDictionary<string, string> overrides)
    {
        string[] enableSettings =
        [
            "DOTNET_EnableDiagnostics", "COMPlus_EnableDiagnostics",
            "DOTNET_EnableDiagnostics_IPC", "COMPlus_EnableDiagnostics_IPC",
            "DOTNET_EnableDiagnostics_EventPipe", "COMPlus_EnableDiagnostics_EventPipe"
        ];

        foreach (var key in enableSettings)
        {
            if (start.Environment.TryGetValue(key, out var value) && value is not null &&
                (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"{key} disables recording. Remove it or set it to 1.");
            }
        }

        string[] portSettings = ["DOTNET_DiagnosticPorts", "COMPlus_DiagnosticPorts"];

        foreach (var key in portSettings)
        {
            if (!start.Environment.TryGetValue(key, out var ports) || string.IsNullOrWhiteSpace(ports))
            {
                continue;
            }

            var descriptions = ports.Split(';', StringSplitOptions.RemoveEmptyEntries);

            if (descriptions.Any(RequiresStartupResume))
            {
                throw new ArgumentException($"Ports in {key} must use nosuspend when launching from this app.");
            }
        }

        string[] suspendSettings = ["DOTNET_DefaultDiagnosticPortSuspend", "COMPlus_DefaultDiagnosticPortSuspend"];
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var key in suspendSettings)
        {
            var explicitlyConfigured = overrides.Keys.Any(name => name.Equals(key, comparison));

            if (explicitlyConfigured && start.Environment[key] != "1")
            {
                throw new ArgumentException($"{key} must be 1 to record application startup.");
            }
        }

        foreach (var key in suspendSettings)
        {
            start.Environment[key] = "1";
        }
    }

    private static bool RequiresStartupResume(string portDescription)
    {
        var flags = portDescription.Split(',').Skip(1);
        return !flags.Any(flag => flag.Trim().Equals("nosuspend", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveApplication(string name, string directory, IDictionary<string, string?> environment)
    {
        string[] candidates = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? [name, name + ".exe"] : [name];

        foreach (var candidate in candidates.Select(file => Path.GetFullPath(file, directory)))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var hasDirectory = Path.IsPathRooted(name) || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;

        if (!hasDirectory)
        {
            var searchPath = environment.TryGetValue("PATH", out var value) ? value ?? "" : "";

            foreach (var path in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var candidate in candidates.Select(file => Path.Combine(path.Trim('"'), file)))
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        throw new FileNotFoundException($"Application not found: {name}");
    }

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed class LaunchedSession(Process process, ILiveTelemetrySession session) : ILaunchedTelemetrySession
    {
        private bool _accepted;
        private Task? _dispose;
        private readonly object _gate = new();

        public LocalDotNetProcess Target => session.Target;

        public Task<CollectionEnd> Completion => session.Completion;

        public event Action? FrameReady
        {
            add => session.FrameReady += value;
            remove => session.FrameReady -= value;
        }

        public DiagnosticFrame Read() => session.Read();

        public void Accept() => _accepted = true;

        public async Task StopProcessAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the check and the stop request.
            }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                return new(_dispose ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            if (!_accepted)
            {
                StopOwnedProcess(process);
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
