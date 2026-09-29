#Requires -Version 7.2
<#
.SYNOPSIS
    Publishes self-contained, single-file Windows builds and packages them for a GitHub Release.

.DESCRIPTION
    Dependencies: PowerShell 7.2+, .NET SDK per global.json (no .NET runtime needed by end users).
    Run from anywhere:
        pwsh build/Publish.ps1                          # win-x64 and win-arm64, version from Directory.Build.props
        pwsh build/Publish.ps1 -Version 1.2.0 -Runtimes win-x64
    Output (artifacts/):
        ScreenshotHelper-<version>-<rid>.exe   single-file app, runs without installing anything
        ScreenshotHelper-<version>-<rid>.zip   the same exe plus README and LICENSE (portable)
        SHA256SUMS.txt                          checksums for verifying downloads
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string[]] $Runtimes = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $Version) {
    $Version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not semantic (e.g. 1.2.0)." }

$artifacts = Join-Path $root 'artifacts'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts | Out-Null

foreach ($rid in $Runtimes) {
    Write-Host "==> Publishing $rid ($Version)" -ForegroundColor Cyan
    $publishDir = Join-Path $artifacts "publish-$rid"
    # Single-file + self-contained: one exe, no runtime install. Native Skia/HarfBuzz libraries are bundled and extracted on first run.
    dotnet publish "$root/src/ScreenshotHelper.App/ScreenshotHelper.App.csproj" `
        --configuration Release --runtime $rid --self-contained true --output $publishDir `
        -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
    if ($LASTEXITCODE -ne 0) { throw "Publish for $rid failed." }

    $exe = Join-Path $artifacts "ScreenshotHelper-$Version-$rid.exe"
    Copy-Item (Join-Path $publishDir 'ScreenshotHelper.exe') $exe

    $staging = Join-Path $artifacts "zip-$rid"
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item (Join-Path $publishDir 'ScreenshotHelper.exe') $staging
    Copy-Item "$root/README.md", "$root/LICENSE" $staging
    Compress-Archive -Path "$staging/*" -DestinationPath (Join-Path $artifacts "ScreenshotHelper-$Version-$rid.zip")
    Remove-Item $staging, $publishDir -Recurse -Force
}

# Checksums in the standard `sha256sum` format so users can verify with common tools.
Get-ChildItem $artifacts -File -Include *.exe, *.zip -Recurse |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding utf8NoBOM

Get-ChildItem $artifacts | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
