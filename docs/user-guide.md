# Using Satori Insight

## Connect to an application

Choose **Attach process**, search by name or PID, select the application, and connect. Hover over a process name to see its command line. Use **Refresh** if an application was started after opening the picker.

To start an application from the profiler, choose **Launch process**. Select an executable or .NET DLL and provide any arguments, working directory, and environment overrides. Enter environment variables as `NAME=value`, one per line; other variables are inherited. DLLs require `dotnet` on PATH.

After a successful launch, the button becomes a red **Stop process** button. It terminates the launched application and its children while preserving the recording. **Disconnect** stops monitoring and leaves the application running. Disconnecting, switching targets, or target exit restores **Launch process**. Canceling an unfinished launch stops the newly created process.

## Explore memory activity

The overview shows heap sizes, allocation rate, generation counts, CPU time, elapsed time, and RSS. RSS includes resident managed and native memory. CPU time includes all threads and can exceed wall-clock time.

The collection log includes generation, mode, reason or outcome, duration, pause, and before/after sizes where available. Select a row for more detail. Filter by generation, or click a histogram bar to select collections in a pause interval.

The pause histogram and p50, p95, p99, p99.9, and maximum describe process-wide GC pauses. Thread-local collections count toward Gen0 activity but do not contribute to that histogram. Collection duration and suspension latency are separate measurements.

Generation sizes describe the latest reported measurement and can lag current allocation. A missing value is shown as `—`.

## Select a time point or period

Click **Heap & collection timeline** to inspect a time point, or drag across it to select a period. Counts, statistics, the collection log, and the live region map follow the selection. The full timeline remains visible.

A point shows the latest measurements available at that time. A period shows collections within the interval, memory values at its end, and process-time statistics for the interval. CPU estimates require measurements around both boundaries.

Choose **Reset** to clear the selection. **Pause view** freezes the display while recording continues; resuming shows the latest data. Export always includes the complete recording.

## Read the region map

Choose **Generation**, **Occupancy**, **Fragmentation**, or **Queue** from the Color selector. The legend identifies categories; continuous bars show percentage scales. **Unknown** or **Unavailable** means the necessary information was not reported. **Unassigned** means the region has no assigned managed generation, and does not necessarily mean it is free.

A white marker with a dark ring identifies a region containing pinned objects. Pinned objects cannot move while pinned and can limit compaction.

Each cell represents an observed address range. Cell area is not proportional to memory size; use the cell slider for readability and select a cell to inspect its address and size. The detail strip is proportional to the selected range. A reported used range is not a count of live-object bytes.

The map on **Memory activity** follows live or replayed measurements. It is independent of captures in Heap inspector.

## Inspect and compare captures

Choose **Capture heap** or **Open dump** in **Heap inspector**. Capture can briefly pause an application. Large or partly unreadable heaps may produce partial results; check the capture's coverage message.

- **Regions:** explore the selected capture's map. **View regions** returns to this tab.
- **Types:** search type names and inspect their objects.
- **Objects:** select an object and follow references.
- **Root path:** find a path connecting an object to a captured root. A missing path in a partial capture does not prove the object is unrooted.
- **Compare:** select a baseline and compare shallow memory usage by type. These values are not retained sizes.

Some captures provide region information only; object-related tabs are disabled for those captures. Up to four captures can be kept at once. Use **Remove capture** to free memory or make room for another capture.

## Save and replay

Choose **Export session** to save a recording, then **Open session** to reopen it. Use playback controls or the timeline to inspect history. Captured heaps remain associated with their capture time.

Session history has no automatic retention limit, so memory use and recording size grow over time. Export important recordings before starting a new connection or closing the app. Recordings can contain type names, object addresses, and other application information; share them accordingly. Process command lines and launch environment values are excluded from exports.
