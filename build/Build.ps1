#Requires -Version 7.2
<#
.SYNOPSIS
    Restores, builds and tests the solution (what CI runs on every push).

.DESCRIPTION
    Dependencies: PowerShell 7.2+, .NET SDK per global.json.
    Run from anywhere:
        pwsh build/Build.ps1                  # Debug build + all tests
        pwsh build/Build.ps1 -Configuration Release -CoreOnly   # e.g. on Linux: only the OS-independent Core tests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    # Build and test only ScreenshotHelper.Core (portable), for non-Windows runners.
    [switch] $CoreOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

# Avalonia's build task sends anonymous usage telemetry unless opted out.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step([string] $Name, [scriptblock] $Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

$targets = if ($CoreOnly) {
    @("$root/tests/ScreenshotHelper.Core.Tests/ScreenshotHelper.Core.Tests.csproj")
} else {
    @("$root/ScreenshotHelper.slnx")
}

foreach ($target in $targets) {
    Invoke-Step "Restore $(Split-Path -Leaf $target)" { dotnet restore $target }
    Invoke-Step "Build $(Split-Path -Leaf $target)" { dotnet build $target --no-restore --configuration $Configuration }
    Invoke-Step "Test $(Split-Path -Leaf $target)" { dotnet test $target --no-build --configuration $Configuration }
}

Write-Host 'Build and tests succeeded.' -ForegroundColor Green
