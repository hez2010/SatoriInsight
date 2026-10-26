using System.Buffers.Binary;
using System.Diagnostics;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

/// <summary>
/// Reads only page maps and headers, without constructing ClrRuntime or loading a DAC.
/// Layout facts are defined by the runtime's SatoriPage.h / SatoriRegion.h / SatoriQueue.h.
/// The current layout is compiler-verified against source revision 11290f7f407ae87309dbdcb75e5937c10c3b7186.
/// Each pass validates address geometry and independently rereads mutable metadata.
/// </summary>
public sealed class SatoriRegionReader
{
    public const ulong PageSize = 1UL << 30;
    public const ulong RegionSize = 1UL << 21;
    public const ulong FirstObjectOffset = 36888;
    private const int HeaderSize = 256;
    private const int MaxRegions = 65536;
    private const long ByteBudget = 16 * 1024 * 1024;

    private const int UsedOffset = 88;
    private const int CommittedOffset = 96;
    private const int PreviousOffset = 144;
    private const int NextOffset = 152;
    private const int QueueOffset = 160;
    private const int OccupancyOffset = 168;
    private const int ObjectCountOffset = 220;
    private const int PinnedOffset = 228;

    private sealed record Page(ulong Address, ulong End, ulong First, ulong Map, byte[] Bitmap, byte[] Header);

    private sealed record Region(Page Page, ulong Address, byte[] Header);

    public RegionReadResult Read(IRegionMemory memory, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        long readBytes = 0;
        var pages = new List<Page>();
        var regions = new List<Region>();
        var skipped = 0;
        var complete = true;
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
            recognized, output, skipped, complete && error is null, readBytes, watch.Elapsed.TotalMilliseconds, error);

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
                        var bytes = new byte[HeaderSize];
                        var again = new byte[HeaderSize];

                        if (!Read(address, bytes) || !Read(address, again) || !bytes.AsSpan().SequenceEqual(again) || U64(bytes, 32) != candidate
                            || U64(bytes, 24) <= address || U64(bytes, 24) > pageEnd || (U64(bytes, 24) - address) % RegionSize != 0
                            || I32(bytes, 16) is < -1 or > 3)
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

            var addresses = regions.Select(r => r.Address).ToHashSet();
            var queues = new Dictionary<ulong, RegionQueueKind?>();
            RegionQueueKind? Queue(ulong pointer)
            {
                if (pointer == 0)
                {
                    return null;
                }

                if (queues.TryGetValue(pointer, out var existing))
                {
                    return existing;
                }

                var bytes = new byte[4];
                var value = pointer % 8 == 0 && Read(pointer, bytes) ? I32(bytes, 0) : -1;
                return queues[pointer] = value is >= 0 and <= 16 ? (RegionQueueKind)value : null;
            }

            bool Fits(Region region)
            {
                var h = region.Header;
                var end = U64(h, 24);
                var used = U64(h, UsedOffset);
                var committed = U64(h, CommittedOffset);
                var occupancy = U64(h, OccupancyOffset);
                var previous = U64(h, PreviousOffset);
                var next = U64(h, NextOffset);
                var queue = U64(h, QueueOffset);
                var allocationStart = U64(h, 64);
                var allocationEnd = U64(h, 72);
                return used >= region.Address + FirstObjectOffset - 8 && used <= committed && committed <= end && committed % 4096 == 0
                    && occupancy <= end - region.Address - FirstObjectOffset && I32(h, ObjectCountOffset) >= 0 && h[PinnedOffset] <= 1 && h[40] <= 2
                    && (allocationStart == 0
                    && allocationEnd == 0 || allocationStart >= region.Address + FirstObjectOffset
                    && allocationStart <= allocationEnd && allocationEnd <= end) && (previous == 0
                    || addresses.Contains(previous)) && (next == 0
                    || addresses.Contains(next)) && (queue == 0
                    || !addresses.Contains(queue & ~(RegionSize - 1))
                    && Queue(queue).HasValue);
            }

            var valid = regions.Where(Fits).ToArray();

            if (valid.Length * 10 < regions.Count * 9)
            {
                return Result(true, [], "The region format could not be recognized.");
            }

            skipped += regions.Count - valid.Length;
            complete &= valid.Length == regions.Count;
            // Drop a whole page if its region map changed during traversal. Incomplete
            // passes never remove previously observed regions from the live topology.
            var stablePages = new HashSet<ulong>();

            foreach (var page in pages)
            {
                var bitmap = new byte[page.Bitmap.Length];
                var header = new byte[64];

                if (Read(page.Map, bitmap) && bitmap.AsSpan().SequenceEqual(page.Bitmap) && Read(page.Address, header)
                    && header.AsSpan(8).SequenceEqual(page.Header.AsSpan(8)))
                {
                    var members = valid.Where(r => r.Page.Address == page.Address).OrderBy(r => r.Address).ToArray();
                    var boundary = page.First;
                    var overlaps = false;

                    foreach (var member in members)
                    {
                        if (member.Address < boundary)
                        {
                            overlaps = true;
                            break;
                        }

                        if (member.Address != boundary)
                        {
                            complete = false;
                        }

                        boundary = U64(member.Header, 24);
                    }

                    if (boundary != page.End)
                    {
                        complete = false;
                    }

                    if (!overlaps)
                    {
                        stablePages.Add(page.Address);
                    }
                    else
                    {
                        complete = false;
                    }
                }
                else
                {
                    complete = false;
                }
            }

            var output = new List<HeapRegion>();

            foreach (var r in valid)
            {
                if (!stablePages.Contains(r.Page.Address))
                {
                    skipped++;
                    continue;
                }

                var h = r.Header;
                var generation = I32(h, 16);
                var end = U64(h, 24);
                var committed = U64(h, CommittedOffset);
                var header = new RegionHeaderInfo(
                    r.Page.Address, r.Page.End, generation, Queue(U64(h, QueueOffset)), (RegionReuseLevel)h[40], U64(h, UsedOffset), committed, U64(h, 64), U64(h, 72),
                    (long)U64(h, OccupancyOffset), I32(h, ObjectCountOffset), h[PinnedOffset] != 0);
                output.Add(new(output.Count, -1, r.Address, (long)(end - committed), (long)(committed - r.Address), 0, 0,
                    header.HeapGeneration, 0, 0, [], false, end, Kind: "Region", RuntimeId: -1, Header: header));
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

    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));

    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
}
