# Using Satori Insight

## Connect to an application

Choose **Attach process**, search by name or PID, select the application, and connect. Hover over a process name to see its command line. Choose **Refresh** to update the list.

Choose **Launch process** to start an executable or .NET DLL with arguments, a working directory, and environment overrides. Enter environment variables as `NAME=value`, one per line; other variables are inherited. DLLs require `dotnet` on PATH.

**Stop process** terminates a launched application and its children while preserving the recording. **Disconnect** stops monitoring and leaves the application running.

## Explore memory activity

The overview shows heap sizes, allocation rate and its recorded maximum, generation counts, CPU time, elapsed time, and RSS. RSS includes resident managed and native memory. CPU time includes all threads and can exceed wall-clock time.

**GC mode** identifies the collector and its last reported latency mode. Generation sizes reflect reported measurements and can lag current allocation. Unavailable measurements appear as **—** or **Unknown**.

Select a collection to inspect its timing, latency mode, and before/after memory sizes. Filter by generation or click a histogram bar to select a pause interval. A latency mode prefixed with **≈** is the last sampled mode before the collection completed; it may have changed between samples.

The histogram and pause statistics describe process-wide GC pauses. Thread-local collections count toward Gen0 activity but are excluded from pause statistics. Collection duration and suspension latency are separate measurements.

## Select a time point or period

Click **Heap & collection timeline** to inspect a time point, or drag across it to select a period. Counts, statistics, the collection log, and the region map follow the selection.

A point shows the measurements available at that time. A period shows collections and process-time statistics within the interval, with memory values and GC mode at its end. The allocation maximum follows the selected time or period.

Choose **Reset** to clear the selection. **Pause view** freezes the display while recording continues.

## Read the region map

Choose **Generation**, **Occupancy**, **Fragmentation**, or **Queue** from the Color selector and use the legend to interpret the colors. **Unknown** or **Unavailable** means the information was not reported. **Unassigned** means the region has no assigned managed generation; it does not necessarily mean the region is free.

White dots mark regions containing pinned objects.

Each cell represents an observed address range. Select a cell to inspect its address, size, and layout. Cell area is not proportional to memory size; the detail strip is. A reported used range is not a count of live-object bytes.

Use **Memory activity** for live or replayed regions and the region map in **Heap inspector** for a capture.

## Inspect and compare captures

Choose **Capture heap** or **Open dump** in **Heap inspector**. Capture can briefly pause an application. Check the coverage message for partial results.

Select a region to filter its types and objects and see its layout and free space. Select a type to filter the object list. **Clear filters** returns to all captured objects.

Selecting an object displays its details, references, and root path alongside the lists. Double-click a reference or root-path entry, or press Enter, to inspect that object. **Back** returns from a followed reference. A missing root path in a partial capture does not prove the object is unrooted.

Expand **Compare captures** and choose **Baseline** and **Compare with** to compare shallow memory usage by type. Both captures must contain object data. These values are not retained sizes.

Region-only captures show the map and region details. Keep up to four captures at once; use **Remove capture** to discard those you no longer need.

## Save and replay

Choose **Export session** to save the complete recording, including captures. Export includes the full session regardless of the timeline selection. Save recordings before starting another connection or closing the app.

Choose **Open session** to reopen a recording, then use playback controls or the timeline to inspect it.
