param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64')]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publishDirectory = Join-Path $repository "artifacts/publish/$RuntimeIdentifier"
$packageDirectory = Join-Path $repository 'artifacts/packages'
$windowsTarget = $RuntimeIdentifier.StartsWith('win-')
$executable = $windowsTarget ? 'SatoriInsight.exe' : 'SatoriInsight'

if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $executable)))
{
    throw "The published application was not found for $RuntimeIdentifier."
}

if (Test-Path -LiteralPath (Join-Path $publishDirectory 'SatoriInsight.dll'))
{
    throw 'Expected a NativeAOT executable, but a managed application DLL was published.'
}

Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination $publishDirectory -Recurse -Force
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

$extension = $windowsTarget ? 'zip' : 'tar.gz'
$archive = Join-Path $packageDirectory "SatoriInsight-$RuntimeIdentifier.$extension"
$tar = $IsWindows ? (Join-Path $env:WINDIR 'System32/tar.exe') : 'tar'
$format = $windowsTarget ? '-a' : '-z'

# Archive Unix binaries with their executable bits, excluding symbols on every platform.
$rootEntries = @(Get-ChildItem -LiteralPath $publishDirectory -Force | Select-Object -ExpandProperty Name)
& $tar $format -cf $archive '--exclude=*.[pP][dD][bB]' '--exclude=*.dbg' '--exclude=*.debug' '--exclude=*.dSYM' -C $publishDirectory -- @rootEntries

if ($LASTEXITCODE -ne 0)
{
    throw "Could not package $RuntimeIdentifier."
}

$entries = & $tar -tf $archive

if ($LASTEXITCODE -ne 0 -or ($entries | Where-Object { $_ -match '(?i)\.(pdb|dbg|debug)(/|$)|\.dSYM(/|$)' }))
{
    throw 'The application archive could not be verified or contains debug symbols.'
}

if ($entries | Where-Object { $_ -match '(^|/)\.(?:/|$)' })
{
    throw 'The application archive contains a dot directory.'
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM
Write-Output "Packaged $([IO.Path]::GetFileName($archive)) without debug symbols."
