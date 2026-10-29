using System.Buffers.Binary;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SatoriInsight.Services.Heap;

// Read-only probing primitives. Source-specific offsets and feature combinations
// belong to LayoutVariant##, not to this class.
internal sealed class SatoriHeapProbe(ulong heap, Architecture architecture, SatoriHeapProbe.ReadMemory read)
{
    internal delegate bool ReadMemory(ulong address, Span<byte> destination);

    private readonly Dictionary<int, Recycler?> recyclers = [];

    internal Architecture Architecture => architecture;

    internal bool TryReadRecycler(int offset, out Recycler recycler)
    {
        if (!recyclers.TryGetValue(offset, out var result))
        {
            result = ReadRecycler(offset);
            recyclers[offset] = result;
        }

        recycler = result!;
        return result is not null;
    }

    private Recycler? ReadRecycler(int offset)
    {
        const int size = 1024;

        if (architecture is not (Architecture.X64 or Architecture.Arm64) || !Pointer(heap)
            || heap > ulong.MaxValue - (ulong)offset - size)
        {
            return null;
        }

        Span<byte> owner = stackalloc byte[8];
        Span<byte> repeatedOwner = stackalloc byte[8];
        var header = new byte[size];
        var repeated = new byte[size];

        if (!read(heap, owner) || !read(heap, repeatedOwner) || U64(owner, 0) != heap || !owner.SequenceEqual(repeatedOwner)
            || !read(heap + (ulong)offset, header) || !read(heap + (ulong)offset, repeated)
            || U64(header, 0) != heap || U64(repeated, 0) != heap)
        {
            return null;
        }

        return new(header, repeated, architecture, read);
    }

    internal sealed class Recycler(byte[] header, byte[] repeated, Architecture architecture, ReadMemory read)
    {
        internal bool HasOrdinaryRegionQueues() => HasRegionQueues(sharded: false);

        internal bool HasShardedRegionQueues() => HasRegionQueues(sharded: true);

        internal bool HasPerformanceCounterClock(int ticksPerMillisecondOffset) => HasClocks(ticksPerMillisecondOffset, 2);

        internal bool HasIndependentTimeStampClock(int osTicksPerMillisecondOffset) => HasClocks(osTicksPerMillisecondOffset, 3);

        internal bool TryReadLatencyMode(int offset, out GCLatencyMode mode)
        {
            mode = default;

            if (header[offset] > 1 || header[offset] != repeated[offset])
            {
                return false;
            }

            mode = header[offset] == 1 ? GCLatencyMode.LowLatency : GCLatencyMode.Interactive;
            return true;
        }

        private bool HasClocks(int offset, int count)
        {
            // The first two values are integer divisions of the same frequency,
            // including when .NET 10 and .NET 11 use different clock APIs.
            var millis = U64(header, offset);
            var micros = U64(header, offset + 8);
            return millis is >= 1000 and <= 1_000_000_000 && millis / 1000 == micros
                && (count == 2 || U64(header, offset + 16) is > 0 and <= 1_000_000_000)
                && Pointer(U64(header, offset + count * 8)) // m_workerGate follows the clocks
                && Same(offset, count * 8 + 8);
        }

        private bool HasRegionQueues(bool sharded)
        {
            // These roots are initialized once. Reusable queues, which can swap
            // during a collection, deliberately are not used as anchors.
            return Same(32, 40) && Same(184, 8)
                && QueueIs(U64(header, 32), 2, sharded)
                && QueueIs(U64(header, 40), 3, sharded)
                && QueueIs(U64(header, 48), 16, sharded)
                && QueueIs(U64(header, 56), 4, sharded)
                && QueueIs(U64(header, 64), 5, sharded)
                && QueueIs(U64(header, 184), 12, false);
        }

        private bool QueueIs(ulong address, int kind, bool sharded)
        {
            if (!Pointer(address))
            {
                return false;
            }

            if (!sharded)
            {
                return KindIs(address, kind);
            }

            Span<byte> value = stackalloc byte[8];
            Span<byte> repeatedValue = stackalloc byte[8];

            if (!read(address, value) || !read(address, repeatedValue) || !value.SequenceEqual(repeatedValue))
            {
                return false;
            }

            var mask = U64(value, 0);
            var cacheLine = architecture == Architecture.Arm64 ? 128UL : 64UL;

            // Power-of-two shard count, with each shard occupying two cache lines.
            if (mask > 1023 || (mask & (mask + 1)) != 0 || address > ulong.MaxValue - cacheLine * (2 * mask + 1) - 4)
            {
                return false;
            }

            return KindIs(address + cacheLine, kind)
                && (mask == 0 || KindIs(address + cacheLine * (2 * mask + 1), kind));
        }

        private bool KindIs(ulong address, int kind)
        {
            Span<byte> value = stackalloc byte[4];
            Span<byte> repeatedValue = stackalloc byte[4];
            return read(address, value) && read(address, repeatedValue) && value.SequenceEqual(repeatedValue)
                && BinaryPrimitives.ReadInt32LittleEndian(value) == kind;
        }

        private bool Same(int offset, int length) => header.AsSpan(offset, length).SequenceEqual(repeated.AsSpan(offset, length));
    }

    private static bool Pointer(ulong address) => address >= 65536 && address % 8 == 0 && address <= ulong.MaxValue - 8;

    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}
