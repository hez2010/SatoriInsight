using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Full-session GC, counter and topology history. No automatic eviction.</summary>
public sealed partial class LiveTelemetryBuffer
{
    private readonly object _gate = new();
    private readonly GcEventStore _events = new();
    private long _gen0Count;
    private long _gen1Count;
    private long _gen2Count;

    private GcCollectionCounts Counts => new(_gen0Count, _gen1Count, _gen2Count);

    private readonly Queue<GcPauseCounterPoint> _pauseCounters = new();
    private readonly List<MetricPoint> _history = [];
    private readonly List<ProcessMetricPoint> _processHistory = [];
    private RuntimeHeapMetrics _metrics = new();
    private CollectionHealth _health = new();
    private double _latestSeconds;

    public void ProcessSample(ProcessMetricPoint sample)
    {
        if (!double.IsFinite(sample.Seconds) || sample.Seconds < 0 || !double.IsFinite(sample.WallClockSeconds) || sample.WallClockSeconds < 0 ||
            sample.CpuSeconds is { } cpu && (!double.IsFinite(cpu) || cpu < 0) || sample.ResidentBytes < 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_processHistory.Count > 0 && sample.Seconds <= _processHistory[^1].Seconds)
            {
                return;
            }

            Observe(sample.Seconds);
            _processHistory.Add(sample);
        }
    }

    public void GcEnd(GcEvent gc)
    {
        lock (_gate)
        {
            if (gc.IsThreadLocal || gc.Generation is < 0 or > 2 || !double.IsFinite(gc.EndSeconds) || gc.EndSeconds < 0
                || !double.IsFinite(gc.DurationMilliseconds) || gc.DurationMilliseconds < 0 || !double.IsFinite(gc.PauseMilliseconds)
                || gc.PauseMilliseconds < 0 || !double.IsFinite(gc.SuspendMilliseconds) || gc.SuspendMilliseconds < 0)
            {
                _health = _health with
                {
                    DiscardedCollections = _health.DiscardedCollections + 1
                };
                return;
            }

            Observe(gc.EndSeconds);

            if (gc.Generation == 0)
            {
                _gen0Count++;
            }
            else if (gc.Generation == 1)
            {
                _gen1Count++;
            }
            else
            {
                _gen2Count++;
            }

            _events.Add(gc);
        }
    }

    public void ThreadLocalCollection(GcEvent? gc)
    {
        lock (_gate)
        {
            if (gc is not { IsThreadLocal: true, Generation: 0, Kind: "ThreadLocal" } || !double.IsFinite(gc.EndSeconds) || gc.EndSeconds < 0
                || !double.IsFinite(gc.DurationMilliseconds) || gc.DurationMilliseconds < 0)
            {
                _health = _health with
                {
                    DiscardedCollections = _health.DiscardedCollections + 1
                };
                return;
            }

            Observe(gc.EndSeconds);
            _gen0Count++;
            _events.Add(gc);
        }
    }

    public void Counter(RuntimeCounterSample sample, double at)
    {
        lock (_gate)
        {
            Observe(at);

            if (sample.Name == "total-pause-time-by-gc")
            {
                if (sample.IntervalSeconds > 0)
                {
                    _pauseCounters.Enqueue(new(at, sample.IntervalSeconds, sample.Value));
                }

                return;
            }

            _metrics = sample.Name switch
            {
                "gc-heap-size" => _metrics with
                {
                    HeapBytes = sample.Value
                },
                "gc-committed" => _metrics with
                {
                    CommittedBytes = sample.Value
                },
                "alloc-rate" => _metrics with
                {
                    AllocationBytesPerSecond = sample.Value
                },
                "gen-0-size" => _metrics with
                {
                    Gen0Bytes = sample.Value
                },
                "gen-1-size" => _metrics with
                {
                    Gen1Bytes = sample.Value
                },
                "gen-2-size" => _metrics with
                {
                    Gen2Bytes = sample.Value
                },
                "loh-size" => _metrics with
                {
                    LohBytes = sample.Value
                },
                "poh-size" => _metrics with
                {
                    PohBytes = sample.Value
                },
                "gc-fragmentation" => _metrics with
                {
                    FragmentationPercent = sample.Value
                },
                _ => _metrics
            };
            _health = _health with
            {
                LastCounterSeconds = at
            };
            var point = new MetricPoint(
                at, _metrics.HeapBytes / 1_048_576, _metrics.CommittedBytes / 1_048_576, _metrics.AllocationBytesPerSecond / 1_048_576, _metrics, Counts);

            if (_history.Count > 0 && Math.Floor(_history[^1].Seconds) == Math.Floor(at))
            {
                _history[^1] = point;
            }
            else
            {
                _history.Add(point);
            }
        }
    }

    public void HeapCount(int count)
    {
        if (count > 0)
        {
            lock (_gate)
            {
                _metrics = _metrics with
                {
                    HeapCount = count
                };
            }
        }
    }

    public void RuntimeVersion(string version)
    {
        lock (_gate)
        {
            _health = _health with
            {
                RuntimeVersion = version
            };
        }
    }

    public void StackwalkSuppressed(bool suppressed)
    {
        lock (_gate)
        {
            _health = _health with
            {
                StackwalkSuppressed = suppressed
            };
        }
    }

    public void ThreadLocalEventsEnabled(bool enabled)
    {
        lock (_gate)
        {
            _health = _health with
            {
                ThreadLocalEventsEnabled = enabled
            };
        }
    }

    public void EventsLost(long total, double? at = null)
    {
        lock (_gate)
        {
            if (total > _health.EventsLost)
            {
                _segments.Clear();
                _generationRanges.Clear();

                if (at is { } time)
                {
                    Observe(time);
                }

                // Event loss invalidates event-derived segments. Independently read
                // region metadata does not depend on the EventPipe event sequence.
                var changeTime = TopologyTimestamp(at ?? _latestSeconds);

                if (_regions.Values.All(r => r.Header is null))
                {
                    _regions.Clear();
                    _topologyChanges.Enqueue(new(changeTime, 0, 0, Reset: true));
                }

                foreach (var region in _regions.Values.Where(r => r.Header is null).ToArray())
                {
                    _regions.Remove((region.RuntimeId, region.StartAddress));
                    _topologyChanges.Enqueue(new(changeTime, region.RuntimeId, region.StartAddress));
                }
            }

            _health = _health with
            {
                EventsLost = Math.Max(_health.EventsLost, total)
            };
        }
    }

    public DiagnosticFrame Read(double at)
    {
        lock (_gate)
        {
            Observe(at);
            return new(
                _latestSeconds, _regions.Values.OrderBy(r => r.StartAddress).ToArray(), _events.Snapshot(), _history.ToArray(),
                _metrics.AllocationBytesPerSecond / 1_048_576, _metrics, _health, _pauseCounters.ToArray(),
                new(0, [], _topologyChanges.ToArray(), _regionSamples.ToArray()), Counts, _processHistory.ToArray());
        }
    }

    private void Observe(double at)
    {
        if (double.IsFinite(at) && at >= 0)
        {
            _latestSeconds = Math.Max(_latestSeconds, at);
        }
    }

    private double TopologyTimestamp(double at) => _lastTopologySeconds = Math.Max(_lastTopologySeconds, double.IsFinite(at) && at >= 0 ? at : _latestSeconds);
}
