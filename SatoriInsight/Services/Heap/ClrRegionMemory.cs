using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

public interface IRegionMemory
{
    int PointerSize { get; }

    IEnumerable<(ulong Address, ulong Size)> ReadableRanges();

    int Read(ulong address, Span<byte> destination);
}

public sealed class ClrRegionMemory : IRegionMemory, IDisposable
{
    private readonly IDataReader reader;
    private readonly WindowsRegionMemory? _windows;

    public ClrRegionMemory(IDataReader reader, bool useProcessRanges = false)
    {
        this.reader = reader;

        if (useProcessRanges && OperatingSystem.IsWindows())
        {
            _windows = new(reader.ProcessId, reader.PointerSize);
        }
    }

    public int PointerSize => reader.PointerSize;

    public int Read(ulong address, Span<byte> destination) => _windows is null ? reader.Read(address, destination) : _windows.Read(address, destination);

    public IEnumerable<(ulong Address, ulong Size)> ReadableRanges()
    {
        if (_windows is not null)
        {
            foreach (var range in _windows.ReadableRanges())
            {
                yield return range;
            }

            yield break;
        }

        if (reader is not IMemoryRegionReader map)
        {
            throw new NotSupportedException("Region data is unavailable for this process or dump.");
        }

        foreach (var range in map.EnumerateMemoryRegions())
        {
            if (range.State == MemoryRegionState.Commit && (range.Protect & (MemoryRegionProtect.NoAccess | MemoryRegionProtect.Guard)) == 0)
            {
                yield return (range.BaseAddress, range.Size);
            }
        }
    }

    public void Dispose() => _windows?.Dispose();
}

public sealed record RegionReadResult(
    bool Recognized, HeapRegion[] Regions, int SkippedRegions, bool Complete, long BytesRead, double ReadMilliseconds, string? Error = null);
