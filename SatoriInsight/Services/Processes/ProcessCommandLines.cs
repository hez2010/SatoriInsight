using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Processes;

/// <summary>Read-only OS metadata; never starts an EventPipe session on unselected processes.</summary>
public static class ProcessCommandLines
{
    public static async Task<IReadOnlyList<LocalDotNetProcess>> EnrichAsync(IReadOnlyList<LocalDotNetProcess> processes, CancellationToken cancellationToken)
    {
        if (processes.Count == 0)
        {
            return processes;
        }

        if (OperatingSystem.IsWindows())
        {
            return await WindowsProcessCommandLine.ReadAllAsync(processes, cancellationToken);
        }

        var result = new List<LocalDotNetProcess>(processes.Count);

        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? commandLine = null;

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    var arguments = (await File.ReadAllTextAsync($"/proc/{process.ProcessId}/cmdline", cancellationToken)).Split('\0');

                    if (arguments.Length > 0 && arguments[^1].Length == 0)
                    {
                        arguments = arguments[..^1];
                    }

                    commandLine = string.Join(" ", arguments.Select(QuoteArgument));
                }
                else if (OperatingSystem.IsMacOS())
                {
                    commandLine = await ReadMacAsync(process.ProcessId, cancellationToken);
                }

                using var identity = Process.GetProcessById(process.ProcessId);

                if (identity.HasExited || identity.StartTime.ToUniversalTime() != process.StartTimeUtc)
                {
                    commandLine = null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
            {
                // Metadata is optional when the target exits or denies access.
            }

            result.Add(process with
            {
                CommandLine = commandLine
            });
        }

        return result;
    }

    private static async Task<string?> ReadMacAsync(int pid, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[] { "-ww", "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command=" })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static string QuoteArgument(string argument) => argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
        ? $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
        : argument;
}
