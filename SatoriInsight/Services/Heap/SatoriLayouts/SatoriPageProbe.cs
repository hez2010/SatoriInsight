using System.Buffers.Binary;

namespace SatoriInsight.Services.Heap;

internal sealed record SatoriPageInfo(
    ulong Address, ulong End, ulong FirstRegion, ulong Heap, ulong RegionMap, int RegionCount, ulong? FreeBitmapAddress = null)
{
    internal bool IsRegionEnd(byte marker) => marker == 1 || FreeBitmapAddress.HasValue && marker == 0;

    internal bool IsFreeRange(ReadOnlySpan<byte> map, ReadOnlySpan<byte> freeBitmap, int start, int count)
    {
        if (count == 0)
        {
            return true;
        }

        if (!FreeBitmapAddress.HasValue)
        {
            return false;
        }

        for (var index = start; index < start + count; index++)
        {
            if (map[index] != 0 || (freeBitmap[index / 8] & (1 << (index % 8))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    internal bool ContainsFreeGranules(ReadOnlySpan<byte> freeBitmap, int start, int count)
    {
        if (FreeBitmapAddress.HasValue)
        {
            for (var index = start; index < start + count; index++)
            {
                if ((freeBitmap[index / 8] & (1 << (index % 8))) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed class SatoriPageProbe(ulong address, byte[] header)
{
    internal const int HeaderSize = 104;

    internal SatoriPageInfo? ReadContiguousRegions()
    {
        // Contiguous-page headers reserve this space as zeroed padding.
        return !header.AsSpan(64, 40).ContainsAnyExcept((byte)0) ? ReadGeometry() : null;
    }

    internal SatoriPageInfo? ReadFreeGranules()
    {
        var page = ReadGeometry();

        if (page is null)
        {
            return null;
        }

        var count = (ulong)page.RegionCount;
        var bitmapBytes = count / 8;
        var free = (page.RegionMap + count + 63) & ~63UL;
        var warm = free + bitmapBytes;
        var committed = warm + bitmapBytes;
        var dirty = committed + count;
        var seen = dirty + count;

        // These tables occupy the card-table prefix that describes the card table itself.
        if (U64(64) != free || U64(72) != warm || U64(80) != committed || U64(88) != dirty || U64(96) != seen
            || seen + bitmapBytes > address + (page.FirstRegion - address) / 512 - 64)
        {
            return null;
        }

        return page with { FreeBitmapAddress = free };
    }

    private SatoriPageInfo? ReadGeometry()
    {
        var end = U64(8);

        if (end <= address || end - address > (1UL << 40)
            || (end - address) % SatoriRegionReader.PageSize != 0)
        {
            return null;
        }

        var size = end - address;
        var first = U64(24);
        var heap = U64(32);
        var map = U64(56);
        var count = size / SatoriRegionReader.RegionSize;

        if (first != address + size / 512 || U64(40) != size / 512 || map != address + 128 + count * 4
            || heap == 0 || first >= end || first % SatoriRegionReader.RegionSize != 0 || map + count > first)
        {
            return null;
        }

        return new(address, end, first, heap, map, (int)count);
    }

    private ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset));
}
