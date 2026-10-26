# Troubleshooting

## The process is missing from the picker

Confirm the application is running, has diagnostics enabled, and is accessible to your account. Choose **Refresh** to update the list. Only local .NET processes are listed.

Use **Launch process** to record an application that exits too quickly to attach to.

## Launch or connection fails

Check the executable path, arguments, and working directory. For a .NET DLL, confirm `dotnet` is available on PATH and that the required runtime is installed.

Remove environment overrides that disable diagnostics or add extra startup suspension. After a timeout or process exit, refresh the list and retry.

## A measurement or region is unavailable

Available information depends on the target runtime. Live generation ranges may appear only after a collection. Capture the heap for additional details where supported.

If the app reports lost data, the affected interval is incomplete. A later collection may restore the region map. Start another recording if losses continue.

## Heap capture fails

Use a profiler build matching the target architecture and check inspection permissions. Close unnecessary captures and ensure enough memory and disk space are available.

Detailed inspection requires a DAC matching the target runtime build. Region-only captures may still be available without it.

## A recording will not open

Use **Open session** for recordings and **Open dump** for dumps. If a recording is incomplete or invalid, export it again from the original session.

## The profiler uses increasing memory

Session history grows while recording. Export it before starting another connection, and remove captures you no longer need. **Pause view** does not stop collection; use **Disconnect** to stop recording.
