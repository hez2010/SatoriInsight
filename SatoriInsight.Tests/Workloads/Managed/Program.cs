// Only this controlled test target induces collections. The observer never requests a GC.
if (args is ["--launch-probe", var reportPath, var inheritedName, ..])
{
    // This first collection must already be visible when launch profiling resumes Main.
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    File.WriteAllText(
        reportPath,
        System.Text.Json.JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId,
            Arguments = args.Skip(3).ToArray(),
            WorkingDirectory = Environment.CurrentDirectory,
            Custom = Environment.GetEnvironmentVariable("INSIGHT_LAUNCH_TEST_VALUE"),
            Inherited = Environment.GetEnvironmentVariable(inheritedName),
            Empty = Environment.GetEnvironmentVariable("INSIGHT_LAUNCH_TEST_EMPTY")
        }));
    await Task.Delay(TimeSpan.FromMinutes(1));
    return;
}

var pinned = GC.AllocateArray<byte>(256 * 1024, pinned: true);
var large = new byte[1024 * 1024];
var queue = new Queue<byte[]>();
var fragmented = new List<byte[]?>();
var generations = new List<byte[]>();
using var stop = new CancellationTokenSource();
var allocation = Task.Run(async () =>
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            for (var i = 0; i < 40; i++)
            {
                var bytes = new byte[16 * 1024];
                bytes[0] = 42;
                queue.Enqueue(bytes);

                if (queue.Count > 128)
                {
                    queue.Dequeue();
                }
            }

            await Task.Delay(20, stop.Token);
        }
    }
    catch (OperationCanceledException)
    {
    }
});
Console.WriteLine($"READY {Environment.ProcessId}");
while (await Console.In.ReadLineAsync() is { } command && command != "exit")
{
    if (command == "localgc")
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();

        while (timer.Elapsed < TimeSpan.FromSeconds(2))
        {
            for (var i = 0; i < 1000; i++)
            {
                var local = new byte[1024 + i % 1024];
                local[0] = 1;
                GC.KeepAlive(local);
            }
        }

        Console.WriteLine("LOCAL_GC_DONE");
    }

    if (command == "runtime")
    {
        var settings = GC.GetConfigurationVariables();
        Console.WriteLine("RUNTIME " + System.Text.Json.JsonSerializer.Serialize(new
        {
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            IsServerGC = System.Runtime.GCSettings.IsServerGC,
            IsSatoriGC = settings.TryGetValue("SatoriGC", out var enabled)
            && enabled is true,
            LatencyMode = System.Runtime.GCSettings.LatencyMode.ToString()
        }));
    }

    if (command == "segment")
    {
        fragmented.Add(new byte[40 * 1024 * 1024]);
    }

    if (command == "generations")
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        generations.Add(new byte[32 * 1024]);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        Console.WriteLine("GENERATIONS_READY");
    }

    if (command == "fragment")
    {
        for (var i = 0; i < 40; i++)
        {
            fragmented.Add(new byte[100000]);
        }

        for (var i = 0; i < 40; i += 2)
        {
            fragmented[i] = null;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        Console.WriteLine("FRAGMENTED");
    }

    if (command == "gc")
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Console.WriteLine($"GC {GC.CollectionCount(0)} {GC.CollectionCount(1)} {GC.CollectionCount(2)}");
    }
}

stop.Cancel();
await allocation;
GC.KeepAlive(pinned);
GC.KeepAlive(large);
GC.KeepAlive(fragmented);
GC.KeepAlive(generations);
