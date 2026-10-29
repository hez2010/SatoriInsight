using System.Buffers.Binary;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// Shared bounds and stability checks. Offsets are supplied by the variant's
// Read implementation; this helper does not choose a layout.
internal sealed class SatoriRegionProbe(
    ulong page, ulong pageEnd, ulong address, byte[] header, byte[] repeated, Func<ulong, RegionQueueKind?> queueKind)
{
    internal RegionHeaderInfo? Read(
        int usedEndOffset, int committedEndOffset, int containingQueueOffset,
        int occupancyOffset, int objectCountOffset, int hasPinnedObjectsOffset)
    {
        var end = U64(24);
        var used = U64(usedEndOffset);
        var committed = U64(committedEndOffset);
        var allocationStart = U64(64);
        var allocationEnd = U64(72);
        var queue = U64(containingQueueOffset);
        var generation = I32(16);
        var objectCount = I32(objectCountOffset);
        var occupancy = U64(occupancyOffset);

        if (U64(32) != page || end <= address || end > pageEnd
            || (end - address) % SatoriRegionReader.RegionSize != 0 || generation is < -1 or > 3)
        {
            return null;
        }

        var capacity = end - address - SatoriRegionReader.FirstObjectOffset;

        if (used < address + SatoriRegionReader.FirstObjectOffset - 8 || used > committed || committed > end
            || committed % 4096 != 0 || occupancy > capacity || objectCount < 0 || (ulong)objectCount > capacity / 24
            || header[hasPinnedObjectsOffset] > 1 || header[40] > 2)
        {
            return null;
        }

        if ((allocationStart != 0 || allocationEnd != 0)
            && (allocationStart < address + SatoriRegionReader.FirstObjectOffset || allocationStart > allocationEnd || allocationEnd > end))
        {
            return null;
        }

        // Compare only fields interpreted by this variant. Queue links, locks,
        // and unrelated collector bookkeeping can change independently.
        if (!Same(16, 4) || !Same(24, 17) || !Same(64, 16)
            || !Same(usedEndOffset, 8) || !Same(committedEndOffset, 8)
            || !Same(containingQueueOffset, 8) || !Same(occupancyOffset, 8)
            || !Same(objectCountOffset, 4) || !Same(hasPinnedObjectsOffset, 1))
        {
            return null;
        }

        var kind = queueKind(queue);

        if (queue != 0 && kind is null)
        {
            return null;
        }

        return new(page, pageEnd, generation, kind, (RegionReuseLevel)header[40], used, committed,
            allocationStart, allocationEnd, (long)occupancy, objectCount, header[hasPinnedObjectsOffset] != 0);
    }

    private bool Same(int offset, int length) => header.AsSpan(offset, length).SequenceEqual(repeated.AsSpan(offset, length));

    private ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset));

    private int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset));
}
