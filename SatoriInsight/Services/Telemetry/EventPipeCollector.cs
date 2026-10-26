using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;
using SatoriInsight.Services.Processes;
using GCSettings = System.Runtime.GCSettings;

namespace SatoriInsight.Services.Telemetry;

public interface ILiveTelemetrySession : IAsyncDisposable
{
    event Action? FrameReady
    {
        add
        {
        }
        remove
        {
        }
    }

    LocalDotNetProcess Target { get; }

    Task<CollectionEnd> Completion { get; }

    DiagnosticFrame Read();
}

public interface ILiveTelemetrySessionFactory
{
    Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default);
}

public sealed class EventPipeSessionFactory : ILiveTelemetrySessionFactory
{
    public async Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default) => await EventPipeCollector.StartAsync(target, cancellationToken).ConfigureAwait(false);
}

/// <summary>GC events and 1 Hz counters. Never requests a GC, dump, or suspension.</summary>
public sealed class EventPipeCollector : ILiveTelemetrySession
{
    public const int BufferSizeMiB = 16;
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
    private readonly EventPipeSession _session;
    private readonly Process _process;
    private readonly LiveTelemetryBuffer _buffer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private int _stopping;
    private double _clockOffset;
    private DiagnosticFrame? _publishedFrame;
    private readonly CancellationTokenSource _samplingCancellation = new();
    private int _threadLocalObserved;

    public event Action? FrameReady;

    public LocalDotNetProcess Target { get; }

    public Task<CollectionEnd> Completion { get; }

    // Include generation ranges (23), but not allocation ticks (10) or bulk
    // survival/movement records (21/22). The keyword can still add GC-side work.
    private static readonly uint[] GcEventIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 23, 25, 26, 27, 28, 35, 39, 187, 202, 204, 205, 208, 239];

    public static EventPipeSessionConfiguration Configuration(bool suppressStackwalk = true, bool includeThreadLocal = true)
    {
        var keywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.GCHeapSurvivalAndMovement);
        var runtimeProvider = includeThreadLocal
            ? new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                keywords, null, new EventPipeProviderEventFilter(true, GcEventIds))
            : new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, keywords);

        var counterOptions = new Dictionary<string, string>
        {
            ["EventCounterIntervalSec"] = UpdateInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture)
        };
        var counterProvider = new EventPipeProvider("System.Runtime", EventLevel.Informational, 0, counterOptions);

        return new EventPipeSessionConfiguration(
            [runtimeProvider, counterProvider], circularBufferSizeMB: BufferSizeMiB,
            requestRundown: false, requestStackwalk: !suppressStackwalk);
    }

    private EventPipeCollector(LocalDotNetProcess target, Process process, EventPipeSession session, bool stackwalkSuppressed, bool threadLocalEnabled)
    {
        Target = target;
        _process = process;
        _session = session;
        _buffer.StackwalkSuppressed(stackwalkSuppressed);
        _buffer.ThreadLocalEventsEnabled(threadLocalEnabled);
        var stream = Task.Factory.StartNew(ProcessStream, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sampling = Task.Run(SampleMetricsAsync);
        Completion = CompleteAsync(stream, sampling);
        _ = Completion.ContinueWith(_ => FrameReady?.Invoke(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static async Task<EventPipeCollector> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default)
    {
        var process = Process.GetProcessById(target.ProcessId);
        EventPipeSession? session = null;

        try
        {
            // A selection is a PID plus start time, not a PID that can silently refer to a replacement process.
            if (process.HasExited || process.StartTime.ToUniversalTime() != target.StartTimeUtc)
            {
                throw new InvalidOperationException("The selected process exited or its PID was reused. Refresh the process list.");
            }

            var client = new DiagnosticsClient(target.ProcessId);
            var suppressed = true;
            var threadLocalEnabled = true;

            try
            {
                session = await client.StartEventPipeSessionAsync(Configuration(), cancellationToken).ConfigureAwait(false);
            }
            catch (UnsupportedCommandException)
            {
                // Event-ID filters require CollectTracing5 (.NET 10+). Older targets keep
                // the informational profile; never silently enable all verbose GC events.
                // Their generation-range keyword also emits bulk survival/movement records.
                threadLocalEnabled = false;

                try
                {
                    session = await client.StartEventPipeSessionAsync(Configuration(includeThreadLocal: false), cancellationToken).ConfigureAwait(false);
                }
                catch (UnsupportedCommandException)
                {
                    suppressed = false;
                    session = await client.StartEventPipeSessionAsync(Configuration(false, false), cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException("The selected process exited while attaching.");
            }

            return new(target, process, session, suppressed, threadLocalEnabled);
        }
        catch
        {
            if (session is not null)
            {
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await session.StopAsync(stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DiagnosticsClientException or OperationCanceledException or IOException)
                {
                }

                session.Dispose();
            }

            process.Dispose();
            throw;
        }
    }

    private double CurrentSeconds => Math.Max(0, _clock.Elapsed.TotalSeconds + Volatile.Read(ref _clockOffset));

    public DiagnosticFrame Read() => Volatile.Read(ref _publishedFrame) ?? _buffer.Read(CurrentSeconds);

    private async Task<CollectionEnd> CompleteAsync(Task<CollectionEnd> stream, Task sampling)
    {
        var result = await stream.ConfigureAwait(false);
        await sampling.ConfigureAwait(false);
        PublishFrame(notify: false);
        return result;
    }

    private void PublishFrame(bool notify = true)
    {
        Volatile.Write(ref _publishedFrame, _buffer.Read(CurrentSeconds));

        if (notify)
        {
            FrameReady?.Invoke();
        }
    }

    private async Task SampleMetricsAsync()
    {
        var token = _samplingCancellation.Token;
        using var timer = new PeriodicTimer(UpdateInterval);
        using var processReader = CreateProcessMetricReader();
        var regionReader = new SatoriRegionReader();
        DataTarget? regionTarget = null;
        var regionsRecognized = false;
        var regionsAttempted = false;
        var gcModeAttempted = false;
        var selfCollector = Target.ProcessId == Environment.ProcessId ? CurrentCollectorKind() : (GcCollectorKind?)null;

        try
        {
            SampleProcessMetrics(processReader);

            if (selfCollector.HasValue)
            {
                _buffer.GcMode(CurrentSeconds, selfCollector, GCSettings.LatencyMode);
            }

            PublishFrame();

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                SampleProcessMetrics(processReader);
                SampleRegions(regionReader, ref regionTarget, ref regionsRecognized, ref regionsAttempted, token);

                if (selfCollector.HasValue)
                {
                    _buffer.GcMode(CurrentSeconds, selfCollector, GCSettings.LatencyMode);
                }
                else if (!gcModeAttempted && regionTarget is not null)
                {
                    gcModeAttempted = true;
                    SampleGcMode(regionTarget, regionsRecognized);
                }

                PublishFrame();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            // The final combined frame is published after EventPipe finishes draining.
            SampleProcessMetrics(processReader);
            regionTarget?.Dispose();
        }
    }

    private ProcessMetricReader? CreateProcessMetricReader()
    {
        try
        {
            return new ProcessMetricReader(Target);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or
            NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Optional process measurements must not interrupt EventPipe collection.
            return null;
        }
    }

    private void SampleProcessMetrics(ProcessMetricReader? reader)
    {
        if (reader?.Read(CurrentSeconds) is { } sample)
        {
            _buffer.ProcessSample(sample);
        }
    }

    private void SampleRegions(SatoriRegionReader reader, ref DataTarget? target, ref bool recognized, ref bool attempted, CancellationToken token)
    {
        // Probe once for an existing heap. Other runtimes need no further reads
        // unless the self-contained Gen 0 event is observed.
        if (attempted && !recognized && Volatile.Read(ref _threadLocalObserved) == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(Target.ProcessId);

            if (process.HasExited || process.StartTime.ToUniversalTime() != Target.StartTimeUtc)
            {
                return;
            }

            target ??= DataTarget.AttachToProcess(Target.ProcessId, suspend: false);
            target.DataReader.FlushCachedData();
            using var memory = new ClrRegionMemory(target.DataReader, useProcessRanges: true);
            var sample = reader.Read(memory, token);
            recognized |= sample.Recognized;
            attempted = true;

            if (sample.Recognized)
            {
                _buffer.GcMode(CurrentSeconds, GcCollectorKind.Satori, sample.LatencyMode);
            }

            if (recognized || Volatile.Read(ref _threadLocalObserved) != 0)
            {
                if (!sample.Recognized && sample.Error is null)
                {
                    sample = sample with
                    {
                        Complete = false,
                        Error = "No readable region headers in this sample; the previous map was retained."
                    };
                }

                _buffer.RegionSample(sample, CurrentSeconds);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            attempted = true;

            if (recognized || Volatile.Read(ref _threadLocalObserved) != 0)
            {
                var failure = new RegionReadResult(recognized, [], 0, false, 0, 0, $"Region sampling unavailable: {exception.Message}");
                _buffer.RegionSample(failure, CurrentSeconds);
            }

            target?.Dispose();
            target = null;
        }
    }

    private static GcCollectorKind CurrentCollectorKind()
    {
        if (GC.GetConfigurationVariables().TryGetValue("SatoriGC", out var enabled) && enabled is true)
        {
            return GcCollectorKind.Satori;
        }

        return GCSettings.IsServerGC ? GcCollectorKind.Server : GcCollectorKind.Workstation;
    }

    private void SampleGcMode(DataTarget target, bool regionsRecognized)
    {
        // A custom collector can report either server value. Identify it first.
        if (regionsRecognized || Volatile.Read(ref _threadLocalObserved) != 0)
        {
            _buffer.GcMode(CurrentSeconds, GcCollectorKind.Satori);
            return;
        }

        try
        {
            if (target.ClrVersions.Length != 1)
            {
                return;
            }

            using var runtime = target.ClrVersions[0].CreateRuntime();
            var kind = runtime.Heap.IsServer ? GcCollectorKind.Server : GcCollectorKind.Workstation;

            if (Volatile.Read(ref _threadLocalObserved) == 0)
            {
                _buffer.GcMode(CurrentSeconds, kind);
            }
        }
        catch (Exception)
        {
            // A missing DAC or inaccessible heap leaves the collector unknown.
            // This optional metadata lookup must not interrupt event collection.
        }
    }

    private CollectionEnd ProcessStream()
    {
        string? error = null;
        var exited = false;

        try
        {
            using var source = new EventPipeEventSource(_session.EventStream);
            // TraceEvent owns concurrency bookkeeping for process-wide GC. ThreadLocal
            // is a self-contained completed record and needs no start/end correlation.
            // Keep the analyzer's detailed completed records for two seconds, not for the full UI history.
            source.DataLifetimeMsec = 2000;
            source.NeedLoadedDotNetRuntimes();
            source.AddCallbackOnProcessStart(process => process.AddCallbackOnDotNetRuntimeLoad(runtime =>
            {
                runtime.GCEnd += (completedProcess, gc) =>
                {
                    if (completedProcess.ProcessID != Target.ProcessId || !gc.IsComplete)
                    {
                        return;
                    }

                    GCLatencyMode? latencyMode = gc.GlobalHeapHistory is { } history && Enum.IsDefined((GCLatencyMode)history.PauseMode)
                        ? (GCLatencyMode)history.PauseMode : null;

                    _buffer.GcEnd(new(
                        gc.Number, (gc.StartRelativeMSec + gc.DurationMSec) / 1000, gc.Generation, gc.Type.ToString(), gc.Reason.ToString(), gc.DurationMSec,
                        gc.PauseDurationMSec, gc.SuspendDurationMSec, Memory: ReadMemoryStats(gc), LatencyMode: latencyMode));
                };
            }));
            var sourceAge = (DateTime.UtcNow - source.SessionStartTime.ToUniversalTime()).TotalSeconds;

            if (sourceAge is >= 0 and < 60)
            {
                Volatile.Write(ref _clockOffset, sourceAge - _clock.Elapsed.TotalSeconds);
            }

            var payload = new byte[ThreadLocalGcParser.PayloadSize];
            var generationRanges = new GenerationRangeCollector(_buffer);
            long lastLost = 0;

            void CheckEventLoss(TraceEvent data)
            {
                if (source.EventsLost != lastLost)
                {
                    lastLost = source.EventsLost;
                    generationRanges.Reset();
                    _buffer.EventsLost(lastLost, data.TimeStampRelativeMSec / 1000);
                }
            }

            source.Clr.GCGenerationRange += data =>
            {
                CheckEventLoss(data);
                generationRanges.Add(data.ThreadID, data.ClrInstanceID,
                    new(data.Generation, data.RangeStart, data.RangeUsedLength, data.RangeReservedLength));
            };
            source.Clr.GCStop += data =>
            {
                CheckEventLoss(data);
                generationRanges.Complete(data.ThreadID, data.ClrInstanceID, data.TimeStampRelativeMSec / 1000);
            };
            source.AllEvents += data =>
            {
                CheckEventLoss(data);

                if (data.ProcessID == Target.ProcessId && data.ProviderGuid == ClrTraceEventParser.ProviderGuid)
                {
                    generationRanges.ObserveEvent(data.ThreadID, (int)data.ID);
                }

                if (data.ProcessID != Target.ProcessId || !ThreadLocalGcParser.Matches(data.ProviderGuid, (int)data.ID))
                {
                    return;
                }

                if (data.Version != 0 || data.EventDataLength != payload.Length)
                {
                    _buffer.ThreadLocalCollection(null);
                    return;
                }

                // Reuse the payload buffer: this event can arrive many thousands of times per second.
                data.EventData(payload, 0, 0, payload.Length);
                var collection = ThreadLocalGcParser.Parse(payload, data.Version, data.TimeStampRelativeMSec / 1000, data.ThreadID);

                if (collection is not null && Interlocked.Exchange(ref _threadLocalObserved, 1) == 0)
                {
                    _buffer.GcMode(collection.EndSeconds, GcCollectorKind.Satori);
                }

                _buffer.ThreadLocalCollection(collection);
            };
            source.Clr.GCGlobalHeapHistory += data =>
            {
                if (data.ProcessID != Target.ProcessId)
                {
                    return;
                }

                _buffer.HeapCount(data.NumHeaps);

                if (data.HasPauseMode && Enum.IsDefined((GCLatencyMode)data.PauseMode))
                {
                    _buffer.GcMode(data.TimeStampRelativeMSec / 1000, latencyMode: (GCLatencyMode)data.PauseMode);
                }
            };
            source.Clr.GCCreateSegment += data => _buffer.SegmentCreated(
                data.ClrInstanceID, data.Address, data.Size, (int)data.Type, data.TimeStampRelativeMSec / 1000);
            source.Clr.GCFreeSegment += data => _buffer.SegmentFreed(data.ClrInstanceID, unchecked((ulong)data.Address), data.TimeStampRelativeMSec / 1000);
            source.Clr.RuntimeStart += data => _buffer.RuntimeVersion($"{data.VMMajorVersion}.{data.VMMinorVersion}.{data.VMBuildNumber}.{data.VMQfeNumber}");
            source.Dynamic.All += data =>
            {
                if (data.ProviderName != "System.Runtime" || data.EventName != "EventCounters" || data.PayloadNames.Length == 0)
                {
                    return;
                }

                if (RuntimeCounterParser.Parse(data.PayloadValue(0)) is { } sample)
                {
                    _buffer.Counter(sample, data.TimeStampRelativeMSec / 1000);
                }
            };
            source.Process();
            _buffer.EventsLost(source.EventsLost);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                error = exception.Message;
            }
        }
        finally
        {
            try
            {
                exited = _process.HasExited;
                // The diagnostics pipe can close just before the OS publishes process termination.
                if (!exited && Volatile.Read(ref _stopping) == 0)
                {
                    exited = _process.WaitForExit(250);
                }
            }
            catch (InvalidOperationException)
            {
                exited = true;
            }

            _clock.Stop();
            _samplingCancellation.Cancel();
            _session.Dispose();
            _process.Dispose();
        }

        return new(exited, exited ? null : error);
    }

    public ValueTask DisposeAsync()
    {
        lock (_stopGate)
        {
            return new(_stopTask ??= StopCoreAsync());
        }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _samplingCancellation.Cancel();

        if (!Completion.IsCompleted)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _session.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DiagnosticsClientException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Closing the stream unblocks a parser waiting for a target that stopped responding.
                _session.EventStream.Dispose();
                await Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }

        await Completion.ConfigureAwait(false);
        _samplingCancellation.Dispose();
    }

    private static GcMemoryStats ReadMemoryStats(TraceGC gc)
    {
        Gens[] generations = [Gens.Gen0, Gens.Gen1, Gens.Gen2, Gens.GenLargeObj, Gens.GenPinObj];
        var sizes = generations.Select(gen => new GcGenerationStats(
            gen.ToString(), BytesFromMegabytes(gc.GenSizeBeforeMB[(int)gen]), BytesFromMegabytes(gc.GenSizeAfterMB(gen)),
            gc.HeapStats is null ? null : BytesFromMegabytes(gc.GenPromotedMB(gen)))).ToArray();
        // Copy the completed record immediately. Without per-heap histories, TraceEvent
        // estimates before sizes; preserve that distinction when presenting its fields.
        var estimated = gc.PerHeapHistories is not { Count: > 0 } || gc.PerHeapHistories.Any(heap => heap.GenData is null
            || heap.GenData.Length < generations.Length);
        return new(BytesFromMegabytes(gc.HeapSizeBeforeMB), BytesFromMegabytes(gc.HeapSizeAfterMB), BytesFromMegabytes(gc.PromotedMB), estimated, sizes);
    }

    private static long? BytesFromMegabytes(double megabytes) => double.IsFinite(megabytes) && megabytes >= 0 && megabytes < long.MaxValue / 1_000_000d
        ? (long)Math.Round(megabytes * 1_000_000)
        : null;
}
