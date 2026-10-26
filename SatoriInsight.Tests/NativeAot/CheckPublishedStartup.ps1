param([Parameter(Mandatory)][string] $Executable)

$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $Executable).Path
$errorLog = Join-Path ([System.IO.Path]::GetTempPath()) "satori-startup-$([Guid]::NewGuid()).log"

if (-not ('SatoriStartupWindow' -as [type]))
{
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class SatoriStartupWindow
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr Find(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var owner);

            if (owner != processId)
            {
                return true;
            }

            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);

            if (title.ToString() != "Satori Insight")
            {
                return true;
            }

            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }
}
'@
}

$application = Start-Process -FilePath $appPath -WindowStyle Hidden -RedirectStandardError $errorLog -PassThru

try
{
    $timeout = [System.Diagnostics.Stopwatch]::StartNew()
    $window = [IntPtr]::Zero

    while ($timeout.Elapsed.TotalSeconds -lt 20)
    {
        $application.Refresh()
        $errorText = Get-Content -LiteralPath $errorLog -Raw

        if ($application.HasExited -or -not [string]::IsNullOrWhiteSpace($errorText))
        {
            throw "Published app failed during startup: $errorText"
        }

        $window = [SatoriStartupWindow]::Find($application.Id)

        if ($window -ne [IntPtr]::Zero)
        {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    if ($window -eq [IntPtr]::Zero)
    {
        throw 'Published app did not create the Satori Insight window.'
    }

    $null = [SatoriStartupWindow]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)

    if (-not $application.WaitForExit(10000) -or $application.ExitCode -ne 0)
    {
        throw 'Published app did not close successfully.'
    }

    Write-Output 'PASS: published executable loaded its XAML, created the main window, and exited cleanly.'
}
finally
{
    if (-not $application.HasExited)
    {
        $application.Kill()
        $application.WaitForExit()
    }

    $application.Dispose()
    Remove-Item -LiteralPath $errorLog
}
