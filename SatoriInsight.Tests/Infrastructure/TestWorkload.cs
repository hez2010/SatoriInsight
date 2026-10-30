using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Xunit;

namespace SatoriInsight.Tests;

internal sealed class TestWorkload(Process process) : IDisposable
{
    public Process Process { get; } = process;

    public static Task<TestWorkload> StartAsync(string framework, bool? serverGc = null)
    {
        var info = new ProcessStartInfo("dotnet");
        info.ArgumentList.Add(TestPaths.ManagedWorkload(framework));

        if (serverGc is { } server)
        {
            info.Environment["DOTNET_gcServer"] = server ? "1" : "0";
            info.Environment["DOTNET_GCHeapCount"] = "2";
            info.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        }

        return StartAsync(info);
    }

    public static Task<TestWorkload> StartSatoriAsync(GCLatencyMode? latencyMode = null)
    {
        var info = new ProcessStartInfo(TestPaths.SatoriWorkload);

        if (latencyMode is { } mode)
        {
            info.Environment["DOTNET_GCLatencyMode"] = ((int)mode).ToString(CultureInfo.InvariantCulture);
        }

        return StartAsync(info);
    }

    private static async Task<TestWorkload> StartAsync(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the test workload.");
        var workload = new TestWorkload(process);

        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            if (ready is null || !ready.StartsWith("READY "))
            {
                throw new InvalidOperationException($"Workload did not start: {ready}");
            }

            return workload;
        }
        catch
        {
            workload.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }

        Process.Dispose();
    }
}
