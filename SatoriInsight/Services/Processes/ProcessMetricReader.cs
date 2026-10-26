using System.ComponentModel;
using System.Diagnostics;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Processes;

public sealed class ProcessMetricReader : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _processStartTimeUtc;
    private readonly double _initialAgeSeconds;
    private readonly Stopwatch _ageClock;

    public ProcessMetricReader(LocalDotNetProcess target)
    {
        _process = Process.GetProcessById(target.ProcessId);

        try
        {
            if (_process.HasExited || _process.StartTime.ToUniversalTime() != target.StartTimeUtc)
            {
                throw new InvalidOperationException("The selected process exited or its PID was reused.");
            }

            _initialAgeSeconds = Math.Max(0, (DateTime.UtcNow - target.StartTimeUtc).TotalSeconds);
            _processStartTimeUtc = target.StartTimeUtc;
            _ageClock = Stopwatch.StartNew();
        }
        catch
        {
            _process.Dispose();
            throw;
        }
    }

    public ProcessMetricPoint? Read(double seconds)
    {
        try
        {
            _process.Refresh();

            if (_process.HasExited || _process.StartTime.ToUniversalTime() != _processStartTimeUtc)
            {
                return null;
            }

            var cpu = ReadOptional(() => _process.TotalProcessorTime.TotalSeconds);
            var resident = ReadOptional(() => _process.WorkingSet64);

            if (_process.HasExited)
            {
                return null;
            }

            var age = _initialAgeSeconds + _ageClock.Elapsed.TotalSeconds;

            return new ProcessMetricPoint(seconds, cpu, age, resident);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    private static T? ReadOptional<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException;

    public void Dispose() => _process.Dispose();
}
