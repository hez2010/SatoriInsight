using System.Collections;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Append-only, unlimited history with stable cheap snapshots for the UI.</summary>
internal sealed class GcEventStore
{
    private const int ChunkSize = 2048;
    private readonly List<GcEvent[]> _chunks = [];
    private readonly List<(double Min, double Max)> _times = [];
    private int _count;

    public void Add(GcEvent gc)
    {
        var offset = _count % ChunkSize;

        if (offset == 0)
        {
            _chunks.Add(new GcEvent[ChunkSize]);
            _times.Add((gc.EndSeconds, gc.EndSeconds));
        }

        _chunks[^1][offset] = gc;
        var time = _times[^1];
        _times[^1] = (Math.Min(time.Min, gc.EndSeconds), Math.Max(time.Max, gc.EndSeconds));
        _count++;
    }

    public GcEventSnapshot Snapshot() => new(_chunks.ToArray(), _times.ToArray(), _count, ChunkSize);
}

internal sealed class GcEventSnapshot(GcEvent[][] chunks, (double Min, double Max)[] times, int count, int chunkSize) : IReadOnlyList<GcEvent>
{
    public int Count => count;

    public GcEvent this[int index] => index >= 0 && index < count
        ? chunks[index / chunkSize][index % chunkSize]
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerable<GcEvent> InWindow(double start, double end)
    {
        for (var c = 0; c < chunks.Length; c++)
        {
            if (times[c].Max < start || times[c].Min > end)
            {
                continue;
            }

            var length = Math.Min(chunkSize, count - c * chunkSize);

            for (var i = 0; i < length; i++)
            {
                var gc = chunks[c][i];

                if (gc.EndSeconds >= start && gc.EndSeconds <= end)
                {
                    yield return gc;
                }
            }
        }
    }

    public IEnumerator<GcEvent> GetEnumerator()
    {
        for (var i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
