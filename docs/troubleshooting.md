# Troubleshooting

## The process is missing from the picker

Confirm the application is running and choose **Refresh**. The picker shows accessible local .NET processes with diagnostics enabled. Ordinary .NET Framework applications and remote processes are not listed.

Run the profiler under an account allowed to inspect the target. An application that exits quickly may be easier to record with **Launch process**.

## Launch or connection fails

Check the executable path, arguments, and working directory. For a .NET DLL, confirm `dotnet` is available on PATH and that the required runtime is installed.

Remove environment overrides that disable .NET diagnostics or introduce extra startup suspension. The launch dialog reports invalid environment entries. After a timeout or process exit, refresh the list and retry.

## A measurement or region is unavailable

Different runtimes expose different information. A dash indicates an unavailable measurement. Live generation ranges may appear only after a collection; captures can provide additional details, but some runtimes support region-only captures.

If the app reports lost data, treat the affected interval as incomplete. A later collection may restore the region map. Reduce the workload or start a new recording if losses continue.

## Heap capture fails

Use a profiler build matching the target architecture and check inspection permissions. Close unnecessary captures and ensure enough memory and disk space are available.

A matching DAC is required for detailed inspection of supported runtimes. A custom runtime build may require its matching DAC; a DAC from a different runtime build is not interchangeable. Region-only captures may still be available.

## A recording will not open

Use a complete, unmodified recording from the current app version. Older recording formats are not converted. A failed import leaves the current recording available.

## The profiler uses increasing memory

Session history is retained until the session is replaced or the app closes. Export the recording when needed, then start a new connection. Remove captured heaps that are no longer needed. Pausing the view does not stop collection.
