using System.Globalization;

namespace SatoriInsight.Services.Heap;

// ClrMD's live Linux reader can read process memory, but does not expose
// IMemoryRegionReader. Dumps continue to use their own captured memory maps.
internal static class LinuxProcessMemoryMap
{
    internal static IEnumerable<(ulong Address, ulong Size)> ReadableRanges(int processId)
    {
        var path = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/maps";

        foreach (var line in File.ReadLines(path))
        {
            var dash = line.IndexOf('-');
            var separator = line.IndexOf(' ');

            if (dash <= 0 || separator <= dash || separator + 1 >= line.Length || line[separator + 1] != 'r')
            {
                continue;
            }

            if (ulong.TryParse(line.AsSpan(0, dash), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var start)
                && ulong.TryParse(line.AsSpan(dash + 1, separator - dash - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var end)
                && end > start)
            {
                yield return (start, end - start);
            }
        }
    }
}
