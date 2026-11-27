using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

/// <summary>
/// Reads only page maps and headers, without constructing ClrRuntime or loading a DAC.
/// Layout facts are defined by SatoriPage, SatoriRegion, SatoriQueue, SatoriHeap, SatoriAllocator and SatoriRecycler.
/// Probes recent x64 and ARM64 layout variants.
/// Each pass validates address geometry and independently rereads mutable metadata.
/// </summary>
public sealed class SatoriRegionReader
{
    public const ulong PageSize = 1UL << 30;
    public const ulong RegionSize = 1UL << 21;
    public const ulong FirstObjectOffset = 36888;
    private const int CommonHeaderSize = 256;
    private const int HeaderSize = LayoutVariant13.PinnedByMarkOffset + 1;
    private const int HeaderReadAttempts = 3;
    private const int MaxRegions = 65536;
    private const long ByteBudget = 16 * 1024 * 1024;

    private sealed record Page(SatoriPageInfo Info, byte[] Bitmap, byte[] FreeBitmap, byte[] Header, int Layouts)
    {
        public ulong Address => Info.Address;
        public ulong End => Info.End;
        public ulong First => Info.FirstRegion;
        public ulong Map => Info.RegionMap;
    }

    private sealed record Region(Page Page, ulong Address, byte[] Header, SatoriRegionProbe Probe, int Layouts);

    public RegionReadResult Read(IRegionMemory memory, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        long readBytes = 0;
        var pages = new List<Page>();
        var regions = new List<Region>();
        var queues = new Dictionary<ulong, RegionQueueKind>();
        var skipped = 0;
        var complete = true;
        var foundPage = false;
        GCLatencyMode? latencyMode = null;
        int ReadBytes(ulong address, Span<byte> destination)
        {
            token.ThrowIfCancellationRequested();

            if (readBytes + destination.Length > ByteBudget || watch.ElapsedMilliseconds > 1000)
            {
                throw new SamplingBudgetException();
            }

            // Charge attempted reads as well, so unreadable addresses cannot bypass the budget.
            readBytes += destination.Length;
            return memory.Read(address, destination);
        }

        bool Read(ulong address, Span<byte> destination) => ReadBytes(address, destination) == destination.Length;

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

        Region? ReadHeader(Page page, ulong address)
        {
            var header = new byte[HeaderSize];
            var repeated = new byte[HeaderSize];

            for (var attempt = 0; attempt < HeaderReadAttempts; attempt++)
            {
                var length = ReadBytes(address, header);
                if (length < CommonHeaderSize)
                {
                    continue;
                }

                length = Math.Min(length, ReadBytes(address, repeated));
                if (length < CommonHeaderSize)
                {
                    continue;
                }

                var candidates = 0;

                var probe = new SatoriRegionProbe(page.Address, page.End, address, header, repeated, length, Queue);

                for (var index = 0; index < SatoriLayouts.All.Length; index++)
                {
                    if ((page.Layouts & (1 << index)) != 0 && SatoriLayouts.All[index].Read(probe) is not null)
                    {
                        candidates |= 1 << index;
                    }
                }

                if (candidates != 0)
                {
                    return new(page, address, header, probe, candidates);
                }
            }

            return null;
        }

        if (memory.PointerSize != 8 || memory.Architecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return Result(false, [], "Region maps require an x64 or ARM64 process.");
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

                if (range.Size < SatoriPageProbe.HeaderSize || range.Address > ulong.MaxValue - range.Size || range.Address > ulong.MaxValue - PageSize)
                {
                    continue;
                }

                var end = range.Address + range.Size;
                var candidate = (range.Address + PageSize - 1) & ~(PageSize - 1);

                for (; candidate < end && end - candidate >= SatoriPageProbe.HeaderSize; candidate += PageSize)
                {
                    if (candidate < coveredUntil)
                    {
                        continue;
                    }

                    var header = new byte[SatoriPageProbe.HeaderSize];

                    if (!Read(candidate, header))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var repeatedHeader = new byte[SatoriPageProbe.HeaderSize];

                    if (!Read(candidate, repeatedHeader) || !header.AsSpan(8).SequenceEqual(repeatedHeader.AsSpan(8)))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var probe = new SatoriPageProbe(candidate, header);
                    SatoriPageInfo? info = null;
                    var layouts = 0;

                    for (var index = 0; index < SatoriLayouts.All.Length; index++)
                    {
                        if (SatoriLayouts.All[index].ReadPage(probe) is { } match)
                        {
                            info ??= match;
                            layouts |= 1 << index;
                        }
                    }

                    if (info is null)
                    {
                        continue;
                    }

                    foundPage = true;
                    coveredUntil = info.End;
                    var bitmap = new byte[info.RegionCount];
                    var freeBitmap = new byte[info.FreeBitmapAddress.HasValue ? info.RegionCount / 8 : 0];

                    if (!Read(info.RegionMap, bitmap) || info.FreeBitmapAddress is { } free && !Read(free, freeBitmap))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var page = new Page(info, bitmap, freeBitmap, header, layouts);
                    pages.Add(page);

                    for (var i = (int)((info.FirstRegion - candidate) / RegionSize); i < bitmap.Length; i++)
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
                        var region = ReadHeader(page, address);

                        if (region is null)
                        {
                            complete = false;
                            skipped++;
                            continue;
                        }

                        regions.Add(region);
                    }
                }
            }

            if (!foundPage)
            {
                return Result(false, []);
            }

            // Validate each region against the final map. A split elsewhere on the
            // page must not discard unrelated regions. Partial passes retain old data.
            var pageRegions = regions.ToLookup(region => region.Page.Address);
            var stableRegions = new List<Region>();
            var stablePages = new List<Page>();
            var output = new List<HeapRegion>();

            foreach (var page in pages)
            {
                var bitmap = new byte[page.Bitmap.Length];
                var freeBitmap = new byte[page.FreeBitmap.Length];
                var header = new byte[SatoriPageProbe.HeaderSize];
                var members = pageRegions[page.Address];

                if (!Read(page.Map, bitmap)
                    || page.Info.FreeBitmapAddress is { } free && !Read(free, freeBitmap)
                    || !Read(page.Address, header) || !header.AsSpan(8).SequenceEqual(page.Header.AsSpan(8)))
                {
                    complete = false;
                    skipped += members.Count();
                    continue;
                }

                stablePages.Add(page);
                complete &= bitmap.AsSpan().SequenceEqual(page.Bitmap) && freeBitmap.AsSpan().SequenceEqual(page.FreeBitmap);
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
                        || page.Info.ContainsFreeGranules(page.FreeBitmap, firstIndex, range.Length)
                        || page.Info.ContainsFreeGranules(freeBitmap, firstIndex, range.Length)
                        || (endIndex < bitmap.Length && !page.Info.IsRegionEnd(bitmap[endIndex]))
                        || !Read(region.Address + 24, geometry) || !geometry.AsSpan().SequenceEqual(h.AsSpan(24, geometry.Length)))
                    {
                        complete = false;
                        skipped++;
                        continue;
                    }

                    var boundaryIndex = (int)((boundary - page.Address) / RegionSize);
                    complete &= page.Info.IsFreeRange(bitmap, freeBitmap, boundaryIndex, firstIndex - boundaryIndex);
                    boundary = end;
                    stableRegions.Add(region);
                }

                var lastIndex = (int)((boundary - page.Address) / RegionSize);
                complete &= page.Info.IsFreeRange(bitmap, freeBitmap, lastIndex, bitmap.Length - lastIndex);
            }

            var heapModes = new List<GCLatencyMode?>();

            var regionsByHeap = stableRegions.ToLookup(region => region.Page.Info.Heap);

            foreach (var heapPages in stablePages.GroupBy(page => page.Info.Heap))
            {
                // Use one interpretation for the whole heap, including other pages.
                // A changing or incompatible region must not silently select its own
                // unrelated layout. No candidate is cached across samples/processes.
                var heapRegions = regionsByHeap[heapPages.Key];
                var candidates = heapPages.Aggregate(SatoriLayouts.AllMask, (mask, page) => mask & page.Layouts);
                candidates = heapRegions.Aggregate(candidates, (mask, region) => mask & region.Layouts);
                var heap = new SatoriHeapProbe(heapPages.Key, memory.Architecture, Read);
                var matched = 0;
                var modes = new GCLatencyMode?[SatoriLayouts.All.Length];

                for (var index = 0; index < SatoriLayouts.All.Length; index++)
                {
                    if (SatoriLayouts.All[index].TryMatch(heap, out var mode))
                    {
                        matched |= 1 << index;
                        modes[index] = mode;
                    }
                }

                if (matched != 0)
                {
                    candidates &= matched;
                }

                if (candidates == 0)
                {
                    complete = false;
                    skipped += heapRegions.Count();
                    continue;
                }

                // The catalog is ordered newest first. Incomplete dumps can still
                // supply region metadata even when independent heap probes fail.
                var selected = System.Numerics.BitOperations.TrailingZeroCount((uint)candidates);
                var layout = SatoriLayouts.All[selected];
                heapModes.Add(modes[selected]);

                foreach (var region in heapRegions)
                {
                    var h = region.Header;
                    var end = U64(h, 24);
                    var metadata = layout.Read(region.Probe)!;
                    var committed = metadata.CommittedEnd;
                    output.Add(new(output.Count, -1, region.Address, (long)(end - committed), (long)(committed - region.Address), 0, 0,
                        metadata.HeapGeneration, 0, 0, [], false, end, Kind: "Region", RuntimeId: -1, Header: metadata));
                }
            }

            if (heapModes.Distinct().Count() == 1)
            {
                latencyMode = heapModes[0];
            }

            if (output.Count == 0 && (!complete || stablePages.Count == 0))
            {
                return Result(true, [], "No supported, consistent Satori region layout was found. Try capturing again.");
            }

            return Result(true, output.ToArray());
        }
        catch (SamplingBudgetException)
        {
            complete = false;
            return Result(foundPage, [], "The map could not be fully refreshed. Previous data is still shown.");
        }
    }

    private sealed class SamplingBudgetException : Exception;

    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}
