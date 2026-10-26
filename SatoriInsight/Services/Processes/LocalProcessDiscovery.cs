using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Processes;

public interface ILocalProcessDiscovery
{
    Task<IReadOnlyList<LocalDotNetProcess>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalProcessDiscovery : ILocalProcessDiscovery
{
    public async Task<IReadOnlyList<LocalDotNetProcess>> ListAsync(CancellationToken cancellationToken = default)
    {
        var processes = await Task.Run<IReadOnlyList<LocalDotNetProcess>>(() =>
        {
            var result = new List<LocalDotNetProcess>();

            foreach (var pid in DiagnosticsClient.GetPublishedProcesses().Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var process = Process.GetProcessById(pid);

                    if (!process.HasExited)
                    {
                        result.Add(new(pid, process.ProcessName, process.StartTime.ToUniversalTime()));
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
                { /* Processes can exit or become inaccessible between discovery and metadata lookup. */
                }
            }

            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return await ProcessCommandLines.EnrichAsync(processes, cancellationToken).ConfigureAwait(false);
    }
}
