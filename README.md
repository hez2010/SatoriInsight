# Satori Insight

A desktop profiler for exploring local .NET memory and garbage collection.

- Follow heap usage, allocation rate, CPU time, elapsed time, and resident memory.
- Inspect GC events, generation counts, pause distributions, and tail latency.
- Explore live memory regions and replay a selected point or period.
- Capture heaps, browse types and objects, follow root paths, and compare captures.
- Launch an application with custom arguments and environment variables, or attach to a running process.
- Save recordings and reopen them for offline analysis.

Windows x64 is the tested platform, with .NET 8 and .NET 10 targets. Available measurements depend on the target runtime. Remote processes and ordinary .NET Framework applications are not supported.

## Build and run

Install the .NET 10 SDK. Building requires an Avalonia Pro Charts license supplied through the `AvaloniaUILicenseKey` environment variable.

```powershell
dotnet run --project SatoriInsight/SatoriInsight.csproj -c Release
```

To create a self-contained Windows build:

```powershell
dotnet publish SatoriInsight/SatoriInsight.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -o artifacts/publish
```

For NativeAOT, also install the Visual Studio C++ desktop build tools and Windows SDK:

```powershell
dotnet publish SatoriInsight/SatoriInsight.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish-aot
```

Keep the published executable and its companion files together.

## Start profiling

Open the app and choose **Attach process** or **Launch process**. Use **Memory activity** for live measurements and **Heap inspector** for captures. Monitoring continues while you inspect a selected time range or pause the view.

Monitoring can affect target performance. Heap capture can briefly pause the target and use additional memory. Measurements that are unavailable appear as `—`.

See the [user guide](docs/user-guide.md) for the complete workflow and [troubleshooting](docs/troubleshooting.md) for connection, capture, and recording problems.

## Verify a source build

```powershell
dotnet test SatoriInsight.slnx -c Release
```

The feature tests require the .NET 8 and .NET 10 runtimes. They build and launch their own test applications.
