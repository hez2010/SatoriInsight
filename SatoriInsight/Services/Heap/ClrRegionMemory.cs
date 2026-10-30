using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

public interface IRegionMemory
{
    int PointerSize { get; }

    Architecture Architecture { get; }

    IEnumerable<(ulong Address, ulong Size)> ReadableRanges();

    int Read(ulong address, Span<byte> destination);
}

public sealed class ClrRegionMemory : IRegionMemory, IDisposable
{
    private readonly IDataReader reader;
    private readonly WindowsRegionMemory? _windows;
    private readonly bool _linuxProcessRanges;

    public ClrRegionMemory(IDataReader reader, bool useProcessRanges = false)
    {
        this.reader = reader;
        // A Linux snapshot/dump can already expose its captured map. Never pair
        // those saved bytes with the original process's current mappings.
        _linuxProcessRanges = useProcessRanges && OperatingSystem.IsLinux() && reader is not IMemoryRegionReader;

        if (useProcessRanges && OperatingSystem.IsWindows())
        {
            _windows = new(reader.ProcessId, reader.PointerSize, reader.Architecture);
        }
    }

    public int PointerSize => reader.PointerSize;

    public Architecture Architecture => reader.Architecture;

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

        if (_linuxProcessRanges)
        {
            foreach (var range in LinuxProcessMemoryMap.ReadableRanges(reader.ProcessId))
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
    bool Recognized, HeapRegion[] Regions, int SkippedRegions, bool Complete, long BytesRead, double ReadMilliseconds, string? Error = null,
    GCLatencyMode? LatencyMode = null);
