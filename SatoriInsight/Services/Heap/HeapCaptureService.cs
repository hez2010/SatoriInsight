using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;
using SatoriInsight.Models;
using SatoriInsight.Services.Recording;

namespace SatoriInsight.Services.Heap;

public sealed class HeapCaptureService
{
    public async Task<HeapProfile> CaptureAsync(HeapCaptureRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the application executable.");
        var useAppHost = !RuntimeFeature.IsDynamicCodeSupported || Assembly.GetEntryAssembly() == typeof(HeapCaptureService).Assembly
            && !string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(useAppHost ? host : "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!useAppHost)
        {
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SatoriInsight.dll"));
        }

        info.ArgumentList.Add("--heap-worker");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start heap worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, ProfilerJsonContext.Default.HeapCaptureRequest).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var response = JsonSerializer.Deserialize(output, ProfilerJsonContext.Default.HeapCaptureResponse);
            return response?.Profile ?? throw new InvalidOperationException(response?.Error ?? await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            await process.WaitForExitAsync(CancellationToken.None);

            if (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Heap capture exceeded two minutes. The worker was stopped.");
            }

            throw;
        }
    }

    public static int RunWorker()
    {
        try
        {
            var request = JsonSerializer.Deserialize(Console.In.ReadToEnd(), ProfilerJsonContext.Default.HeapCaptureRequest)
                ?? throw new InvalidDataException("Missing capture request.");
            Console.Write(JsonSerializer.Serialize(new HeapCaptureResponse(Capture(request), null), ProfilerJsonContext.Default.HeapCaptureResponse));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Write(JsonSerializer.Serialize(new HeapCaptureResponse(null, ex.Message), ProfilerJsonContext.Default.HeapCaptureResponse));
            return 1;
        }
    }

    public static HeapProfile Capture(HeapCaptureRequest request)
    {
        if (request.MaxObjects is < 1 or > 2000000 || request.MaxReferences is < 1 or > 8000000)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.Process is { } identity)
        {
            using var process = Process.GetProcessById(identity.ProcessId);

            if (process.HasExited || process.StartTime.ToUniversalTime() != identity.StartTimeUtc)
            {
                throw new InvalidOperationException("Process exited or PID was reused.");
            }
        }

        var captured = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        using var target = request.DumpPath is { } path ? DataTarget.LoadDump(path) : DataTarget.CreateSnapshotAndAttach(request.Process!.ProcessId);
        var captureMs = watch.Elapsed.TotalMilliseconds;

        if (target.ClrVersions.Length == 0)
        {
            throw new InvalidOperationException("No managed runtime was found.");
        }

        if (target.ClrVersions.Length > 1)
        {
            throw new NotSupportedException("This capture contains multiple CLR runtimes; select a single-runtime target.");
        }

        if (request.DumpPath is null && OperatingSystem.IsWindows()
            || target.DataReader is Microsoft.Diagnostics.Runtime.DataReaders.Implementation.IMemoryRegionReader)
        {
            using var memory = new ClrRegionMemory(target.DataReader, useProcessRanges: request.DumpPath is null);
            var map = new SatoriRegionReader().Read(memory);

            if (map.Recognized)
            {
                if (map.Error is not null)
                {
                    throw new InvalidOperationException(map.Error);
                }

                var coverage = new List<string>
                {
                    "Region view only. Object and root details are unavailable."
                };

                if (!map.Complete)
                {
                    coverage.Add($"{map.SkippedRegions:N0} regions could not be read.");
                }

                var runtimeVersion = target.ClrVersions[0].Version.ToString();
                var analysisMilliseconds = watch.Elapsed.TotalMilliseconds - captureMs;

                return new HeapProfile(
                    Guid.NewGuid(), captured, request.Process, runtimeVersion,
                    captureMs, analysisMilliseconds, map.Regions, [], [], [], [],
                    coverage.ToArray(), MetadataOnly: true);
            }
        }

        using var runtime = target.ClrVersions[0].CreateRuntime();
        var heap = runtime.Heap;

        if (!heap.CanWalkHeap)
        {
            throw new InvalidOperationException("Heap was captured during a non-walkable GC phase. Try capturing again.");
        }

        var warnings = new List<string>();
        var roots = heap.EnumerateRoots().Take(100001).Select(root => new HeapRootInfo(root.Address, root.Object.Address, root.RootKind.ToString(), root.IsPinned)).ToArray();

        if (roots.Length > 100000)
        {
            roots = roots[..100000];
            warnings.Add("Some roots were omitted; root paths may be incomplete.");
        }

        var pinned = roots.Where(root => root.Pinned).Select(root => root.ObjectAddress).ToHashSet();
        var objects = new List<HeapObjectInfo>();
        var regions = new List<HeapRegion>();
        var holes = new List<FreeHole>();
        var refs = 0;
        var addresses = new HashSet<ulong>();

        foreach (var segment in heap.Segments)
        {
            var id = regions.Count;
            long used = 0;
            long free = 0;
            long largest = 0;
            var count = 0;
            var pins = 0;
            var types = new Dictionary<string, (int Count, long Bytes)>();
            var complete = true;

            foreach (var obj in segment.EnumerateObjects(carefully: true))
            {
                if (objects.Count >= request.MaxObjects)
                {
                    complete = false;
                    break;
                }

                if (!obj.IsValid)
                {
                    complete = false;
                    continue;
                }

                var size = checked((long)obj.Size);
                // Carefully walking can recover after unreadable objects. Never publish an
                // out-of-range record as an exact object or free hole in the region map.
                if (size <= 0 || obj.Address < segment.Start || obj.Address >= segment.End || (ulong)size > segment.End - obj.Address)
                {
                    complete = false;
                    continue;
                }

                if (obj.IsFree)
                {
                    free += size;
                    largest = Math.Max(largest, size);

                    if (holes.Count < request.MaxObjects)
                    {
                        holes.Add(new(obj.Address, size, id));
                    }
                    else
                    {
                        complete = false;
                    }

                    continue;
                }

                var name = obj.Type?.Name ?? "<unknown type>";

                if (!addresses.Add(obj.Address))
                {
                    complete = false;
                    continue;
                }

                used += size;
                count++;

                if (pinned.Contains(obj.Address))
                {
                    pins++;
                }

                var prior = types.GetValueOrDefault(name);
                types[name] = (prior.Count + 1, prior.Bytes + size);
                var edges = new List<ObjectReference>();

                if (refs < request.MaxReferences)
                {
                    foreach (var reference in obj.EnumerateReferencesWithFields(carefully: true, considerDependantHandles: true))
                    {
                        if (refs >= request.MaxReferences)
                        {
                            break;
                        }

                        edges.Add(new(reference.Object.Address, reference.Field?.Name ?? $"offset +{reference.Offset}"));
                        refs++;
                    }
                }

                objects.Add(new(obj.Address, name, size, id, MapGeneration(segment.GetGeneration(obj.Address).ToString()), edges.ToArray()));
            }

            var committed = checked((long)segment.CommittedMemory.Length);
            var reserved = checked((long)segment.ReservedMemory.Length);
            regions.Add(new(
                id, segment.SubHeap.Index, segment.Start, reserved, committed, used, free, MapGeneration(segment.Kind.ToString()), pins, count,
                types.Select(t => new TypeStat(t.Key, t.Value.Count, t.Value.Bytes)).OrderByDescending(t => t.Bytes).ToArray(), complete, segment.End, largest,
                segment.Kind.ToString()));
        }

        if (regions.Any(r => !r.DetailsKnown))
        {
            warnings.Add("Partial capture: object counts and free space may be incomplete.");
        }

        if (refs >= request.MaxReferences)
        {
            warnings.Add("Some references were omitted; root paths may be incomplete.");
        }

        var allTypes = regions.SelectMany(r => r.Types).GroupBy(t => t.Name).Select(g => new TypeStat(g.Key, g.Sum(t => t.Count), g.Sum(t => t.Bytes))).OrderByDescending(t => t.Bytes).ToArray();
        return new(
            Guid.NewGuid(), captured, request.Process, target.ClrVersions[0].Version.ToString(), captureMs, watch.Elapsed.TotalMilliseconds - captureMs,
            regions.ToArray(), allTypes, objects.ToArray(), roots, holes.ToArray(), warnings.ToArray());
    }

    private static HeapGeneration MapGeneration(string kind) => kind switch
    {
        "Generation0" => HeapGeneration.Gen0,
        "Generation1" => HeapGeneration.Gen1,
        "Generation2" => HeapGeneration.Gen2,
        "Large" => HeapGeneration.Loh,
        "Pinned" => HeapGeneration.Poh,
        "Frozen" => HeapGeneration.Frozen,
        "Ephemeral" => HeapGeneration.Mixed,
        _ => HeapGeneration.Unknown
    };
}
