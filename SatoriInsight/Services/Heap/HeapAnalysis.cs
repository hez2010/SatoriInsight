using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

public static class HeapAnalysis
{
    public static RootPathStep[] FindRootPath(HeapProfile profile, ulong target, CancellationToken token)
    {
        var objects = profile.Objects.ToDictionary(o => o.Address);
        var parents = new Dictionary<ulong, (ulong Parent, string Link)>();
        var queue = new Queue<ulong>();

        foreach (var root in profile.Roots)
        {
            if (parents.TryAdd(root.ObjectAddress, (0, $"{root.Kind} @ 0x{root.Address:X}")))
            {
                queue.Enqueue(root.ObjectAddress);
            }
        }

        while (queue.TryDequeue(out var address))
        {
            token.ThrowIfCancellationRequested();

            if (address == target)
            {
                var path = new List<RootPathStep>();

                for (var current = target; current != 0; current = parents[current].Parent)
                {
                    path.Add(new(0, parents[current].Link, current, objects.GetValueOrDefault(current)?.Type ?? "<uncaptured object>"));
                }

                path.Reverse();
                return path.Select((step, index) => step with { Depth = index }).ToArray();
            }

            if (!objects.TryGetValue(address, out var obj))
            {
                continue;
            }

            foreach (var reference in obj.References)
            {
                if (reference.Address != 0 && parents.TryAdd(reference.Address, (address, reference.Field)))
                {
                    queue.Enqueue(reference.Address);
                }
            }
        }

        return [];
    }

    public static TypeGrowth[] Compare(HeapProfile baseline, HeapProfile current)
    {
        var before = baseline.Types.ToDictionary(t => t.Name);
        var after = current.Types.ToDictionary(t => t.Name);
        return before.Keys.Union(after.Keys).Select(name => new TypeGrowth(
            name, before.GetValueOrDefault(name)?.Bytes ?? 0, after.GetValueOrDefault(name)?.Bytes ?? 0, after.GetValueOrDefault(name)?.Count ?? 0)).OrderByDescending(t => t.DeltaBytes).ToArray();
    }
}
