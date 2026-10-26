using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

/// <summary>
/// Reads only page maps and headers, without constructing ClrRuntime or loading a DAC.
/// Layout facts are defined by SatoriPage, SatoriRegion, SatoriQueue, SatoriHeap, SatoriAllocator and SatoriRecycler.
/// The x64 and ARM64 layouts are compiler-verified against source revision 2e8439c701c7502aa45e326202fdb2403d4ae1d0.
/// Each pass validates address geometry and independently rereads mutable metadata.
/// </summary>
public sealed class SatoriRegionReader
{
    public const ulong PageSize = 1UL << 30;
    public const ulong RegionSize = 1UL << 21;
    public const ulong FirstObjectOffset = 36888;
    private const int HeaderSize = 256;
    private const int HeaderReadAttempts = 3;
    private const int MaxRegions = 65536;
    private const long ByteBudget = 16 * 1024 * 1024;

    private const int UsedOffset = 88;
    private const int CommittedOffset = 96;
    private const int QueueOffset = 160;
    private const int OccupancyOffset = 168;
    private const int ObjectCountOffset = 220;
    private const int PinnedOffset = 228;
    private const int LowLatencyModeOffset = 388;

    private sealed record Page(ulong Address, ulong End, ulong First, ulong Map, byte[] Bitmap, byte[] Header);

    private sealed record Region(Page Page, ulong Address, byte[] Header);

    public RegionReadResult Read(IRegionMemory memory, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        long readBytes = 0;
        var pages = new List<Page>();
        var regions = new List<Region>();
        var queues = new Dictionary<ulong, RegionQueueKind>();
        var skipped = 0;
        var complete = true;
        GCLatencyMode? latencyMode = null;
        bool Read(ulong address, Span<byte> destination)
        {
            token.ThrowIfCancellationRequested();

            if (readBytes + destination.Length > ByteBudget || watch.ElapsedMilliseconds > 1000)
            {
                throw new SamplingBudgetException();
            }

            // Charge attempted reads as well, so unreadable addresses cannot bypass the budget.
            readBytes += destination.Length;
            return memory.Read(address, destination) == destination.Length;
        }

        RegionReadResult Result(bool recognized, HeapRegion[] output, string? error = null) => new(
            recognized, output, skipped, complete && error is null, readBytes, watch.Elapsed.TotalMilliseconds, error, latencyMode);

        RegionQueueKind? Queue(ulong pointer)
        {
            if (pointer == 0 || pointer % 8 != 0)
            {
                return null;
            }

            if (queues.TryGetValue(pointer, out var existing))
            {
                return existing;
            }

            // Regions point to a queue shard, whose immutable QueueKind is at offset zero.
            Span<byte> bytes = stackalloc byte[4];

            if (!Read(pointer, bytes))
            {
                return null;
            }

            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            return value is >= 0 and <= 16 ? queues[pointer] = (RegionQueueKind)value : null;
        }

        bool Fits(Page page, ulong address, byte[] header)
        {
            var end = U64(header, 24);
            var used = U64(header, UsedOffset);
            var committed = U64(header, CommittedOffset);
            var allocationStart = U64(header, 64);
            var allocationEnd = U64(header, 72);
            var queue = U64(header, QueueOffset);

            if (U64(header, 32) != page.Address || end <= address || end > page.End || (end - address) % RegionSize != 0
                || I32(header, 16) is < -1 or > 3)
            {
                return false;
            }

            if (used < address + FirstObjectOffset - 8 || used > committed || committed > end || committed % 4096 != 0
                || U64(header, OccupancyOffset) > end - address - FirstObjectOffset
                || I32(header, ObjectCountOffset) < 0 || header[PinnedOffset] > 1 || header[40] > 2)
            {
                return false;
            }

            if ((allocationStart != 0 || allocationEnd != 0)
                && (allocationStart < address + FirstObjectOffset || allocationStart > allocationEnd || allocationEnd > end))
            {
                return false;
            }

            // Queue neighbors may be changing or unreadable independently. They are
            // not needed to identify this region or interpret its displayed metadata.
            return queue == 0 || Queue(queue).HasValue;
        }

        byte[]? ReadHeader(Page page, ulong address)
        {
            var header = new byte[HeaderSize];
            var repeated = new byte[HeaderSize];

            for (var attempt = 0; attempt < HeaderReadAttempts; attempt++)
            {
                if (Read(address, header) && Read(address, repeated) && SameMetadata(header, repeated) && Fits(page, address, header))
                {
                    return header;
                }
            }

            return null;
        }

        if (memory.PointerSize != 8)
        {
            return Result(false, [], "Region maps require a 64-bit process.");
        }

        try
        {
            ulong coveredUntil = 0;

            foreach (var range in memory.ReadableRanges())
            {
                token.ThrowIfCancellationRequested();

                if (watch.ElapsedMilliseconds > 1000)
                {
                    throw new SamplingBudgetException();
                }

                if (range.Size < 64 || range.Address > ulong.MaxValue - range.Size || range.Address > ulong.MaxValue - PageSize)
                {
                    continue;
                }

                var end = range.Address + range.Size;
                var candidate = (range.Address + PageSize - 1) & ~(PageSize - 1);

                for (; candidate < end && end - candidate >= 64; candidate += PageSize)
                {
                    if (candidate < coveredUntil)
                    {
                        continue;
                    }

                    var header = new byte[64];

                    if (!Read(candidate, header))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var repeatedHeader = new byte[64];

                    if (!Read(candidate, repeatedHeader) || !header.AsSpan(8).SequenceEqual(repeatedHeader.AsSpan(8)))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var pageEnd = U64(header, 8);

                    if (pageEnd <= candidate || pageEnd - candidate > (1UL << 40) || (pageEnd - candidate) % PageSize != 0)
                    {
                        continue;
                    }

                    var size = pageEnd - candidate;
                    var first = U64(header, 24);
                    var map = U64(header, 56);
                    // These relationships distinguish a page header from ordinary aligned memory.
                    if (first != candidate + size / 512 || U64(header, 40) != size / 512 || map != candidate + 128 + size / (RegionSize / 2) * 2
                        || U64(header, 32) == 0 || first >= pageEnd || first % RegionSize != 0 || map + size / RegionSize > first)
                    {
                        continue;
                    }

                    var bitmap = new byte[checked((int)(size / RegionSize))];
                    var page = new Page(candidate, pageEnd, first, map, bitmap, header);
                    pages.Add(page);
                    coveredUntil = pageEnd;

                    if (!Read(map, bitmap))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    for (var i = (int)((first - candidate) / RegionSize); i < bitmap.Length; i++)
                    {
                        if (bitmap[i] != 1)
                        {
                            continue;
                        }

                        if (regions.Count >= MaxRegions)
                        {
                            throw new SamplingBudgetException();
                        }

                        var address = candidate + (ulong)i * RegionSize;
                        var bytes = ReadHeader(page, address);

                        if (bytes is null)
                        {
                            complete = false;
                            skipped++;
                            continue;
                        }

                        regions.Add(new(page, address, bytes));
                    }
                }
            }

            if (pages.Count == 0)
            {
                return Result(false, []);
            }

            if (regions.Count == 0)
            {
                return Result(true, [], "Could not read the region data. Try capturing again.");
            }

            // Validate each region against the final map. A split elsewhere on the
            // page must not discard unrelated regions. Partial passes retain old data.
            var stablePages = new HashSet<ulong>();
            var pageRegions = regions.ToLookup(region => region.Page.Address);
            var output = new List<HeapRegion>();

            foreach (var page in pages)
            {
                var bitmap = new byte[page.Bitmap.Length];
                var header = new byte[64];
                var members = pageRegions[page.Address];

                if (!Read(page.Map, bitmap) || !Read(page.Address, header) || !header.AsSpan(8).SequenceEqual(page.Header.AsSpan(8)))
                {
                    complete = false;
                    skipped += members.Count();
                    continue;
                }

                stablePages.Add(page.Address);
                complete &= bitmap.AsSpan().SequenceEqual(page.Bitmap);
                var boundary = page.First;
                var geometry = new byte[16];

                foreach (var region in members)
                {
                    var h = region.Header;
                    var end = U64(h, 24);
                    var firstIndex = (int)((region.Address - page.Address) / RegionSize);
                    var endIndex = (int)((end - page.Address) / RegionSize);
                    var range = bitmap.AsSpan(firstIndex, endIndex - firstIndex);

                    if (region.Address < boundary || range[0] != 1 || range[1..].Contains((byte)1) || range.Contains((byte)0)
                        || !range.SequenceEqual(page.Bitmap.AsSpan(firstIndex, range.Length))
                        || (endIndex < bitmap.Length && bitmap[endIndex] != 1)
                        || !Read(region.Address + 24, geometry) || !geometry.AsSpan().SequenceEqual(h.AsSpan(24, geometry.Length)))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    complete &= region.Address == boundary;
                    boundary = end;

                    var generation = I32(h, 16);
                    var committed = U64(h, CommittedOffset);
                    var metadata = new RegionHeaderInfo(
                        page.Address, page.End, generation, Queue(U64(h, QueueOffset)), (RegionReuseLevel)h[40], U64(h, UsedOffset), committed, U64(h, 64), U64(h, 72),
                        (long)U64(h, OccupancyOffset), I32(h, ObjectCountOffset), h[PinnedOffset] != 0);
                    output.Add(new(output.Count, -1, region.Address, (long)(end - committed), (long)(committed - region.Address), 0, 0,
                        metadata.HeapGeneration, 0, 0, [], false, end, Kind: "Region", RuntimeId: -1, Header: metadata));
                }

                complete &= boundary == page.End;
            }

            if (output.Count == 0)
            {
                return Result(true, [], "No stable region data was available. Try again.");
            }

            if (pages.FirstOrDefault(page => stablePages.Contains(page.Address)) is { } stablePage)
            {
                // Recycler is embedded after Allocator. ARM64 uses 128-byte counter
                // padding; x64 uses 64. Both back-pointers must agree with the page.
                var recyclerOffset = memory.Architecture switch
                {
                    Architecture.X64 => 4736UL,
                    Architecture.Arm64 => 8960UL,
                    _ => 0UL
                };
                var heap = U64(stablePage.Header, 32);
                Span<byte> pointer = stackalloc byte[8];
                Span<byte> mode = stackalloc byte[1];
                Span<byte> repeatedMode = stackalloc byte[1];

                if (recyclerOffset != 0 && heap <= ulong.MaxValue - recyclerOffset - LowLatencyModeOffset &&
                    Read(heap, pointer) && BinaryPrimitives.ReadUInt64LittleEndian(pointer) == heap &&
                    Read(heap + recyclerOffset, pointer) && BinaryPrimitives.ReadUInt64LittleEndian(pointer) == heap &&
                    Read(heap + recyclerOffset + LowLatencyModeOffset, mode) && mode[0] <= 1 &&
                    Read(heap + recyclerOffset + LowLatencyModeOffset, repeatedMode) && mode.SequenceEqual(repeatedMode))
                {
                    latencyMode = mode[0] == 1 ? GCLatencyMode.LowLatency : GCLatencyMode.Interactive;
                }
            }

            return Result(true, output.ToArray());
        }
        catch (SamplingBudgetException)
        {
            complete = false;
            return Result(pages.Count > 0, [], "The map could not be fully refreshed. Previous data is still shown.");
        }
    }

    private sealed class SamplingBudgetException : Exception;

    // Compare only the fields displayed or validated above. Queue links, allocation
    // counters, locks, and other collector bookkeeping can change independently.
    private static bool SameMetadata(byte[] first, byte[] second) =>
        first.AsSpan(16, 4).SequenceEqual(second.AsSpan(16, 4))
        && first.AsSpan(24, 17).SequenceEqual(second.AsSpan(24, 17))
        && first.AsSpan(64, 16).SequenceEqual(second.AsSpan(64, 16))
        && first.AsSpan(UsedOffset, 16).SequenceEqual(second.AsSpan(UsedOffset, 16))
        && first.AsSpan(QueueOffset, 16).SequenceEqual(second.AsSpan(QueueOffset, 16))
        && first.AsSpan(ObjectCountOffset, 4).SequenceEqual(second.AsSpan(ObjectCountOffset, 4))
        && first[PinnedOffset] == second[PinnedOffset];

    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));

    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
}
