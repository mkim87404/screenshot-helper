<#
.SYNOPSIS
    Regenerates every README screenshot in .github/media from demo data, so no personal paths, names or files appear.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows, the .NET 10 SDK (to build the app), tools/Capture-Window.ps1. No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Update-ReadmeScreenshots.ps1
        pwsh tools/Update-ReadmeScreenshots.ps1 -DriveLetter T      # if S: is already in use

    DISRUPTIVE: this drives the real app on your desktop for about a minute. It opens its windows, starts a session that registers
    the global session keys, and sends keystrokes with SendKeys. Don't touch the keyboard or mouse until it finishes. It sends session
    keys only after the app's log confirms that the session started. Without that check, a failed start would send the keys to
    whatever app has focus.

    What it does:
      1. Builds the app (Release), creates a scratch folder with demo screenshots (copies of the app icon) and maps it to a drive
         letter with `subst`, so the app shows a neutral path such as S:\Project screenshots.
      2. Points SCREENSHOTHELPER_HOME at a scratch settings folder (your real settings and logs aren't touched).
      3. Captures home-light, collision-prompt, caption, renumber and home-dark, staging them before copying into .github/media only
         when every capture succeeded.
      4. Always removes the drive mapping, the demo files and the scratch folder, and closes the app it started, even on failure.
    The one real screenshot the session takes (a main shot of your screen, needed for the caption box) is deleted with the demo data.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[D-Z]$')] [string] $DriveLetter = 'S'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$media = Join-Path $root '.github/media'
$exe = Join-Path $root 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'
$drive = "${DriveLetter}:"
$project = "$drive\Project screenshots"

# Starts the app against the scratch home and returns its process once the main window is up.
function Start-App {
    $process = Start-Process $exe -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'The app exited on start-up (is another instance running?).' }
        $window = $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)))
        if ($window) {
            Start-Sleep -Milliseconds 1500   # let the first layout and theme settle before capturing
            return @{ Process = $process; Window = $window }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'The app window did not appear within 20 s.'
}

function Stop-App($app) {
    if ($app -and -not $app.Process.HasExited) {
        Stop-Process -Id $app.Process.Id -Force
        $app.Process.WaitForExit(5000) | Out-Null
    }
}

function Find($app, [string] $name) {
    $element = $app.Window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))
    if (-not $element) { throw "UI element '$name' not found." }
    return $element
}

function Invoke-Element($app, [string] $name) {
    (Find $app $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}

# Sets a numeric box by its accessible name. Its label TextBlock has the same name, so Text elements are skipped.
function Set-Number($app, [string] $name, [string] $value) {
    $all = $app.Window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))
    foreach ($element in $all) {
        if ($element.Current.ControlType -eq $CT::Text) { continue }
        $edit = $element.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Edit)))
        if ($edit) {
            $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
            Start-Sleep -Milliseconds 400
            return
        }
    }
    throw "Number box '$name' not found."
}

function Save-Capture([string] $title, [string] $name) {
    $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Capture-Window.ps1'), '-ProcessName', 'Screenshot Helper', '-Output', (Join-Path $staging $name), '-DelayMs', '200')
    if ($title) { $arguments += @('-Title', $title) }
    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) { throw "Capturing $name failed." }
}

# Polls the scratch log for a line matching $pattern.
function Wait-Log([string] $pattern, [int] $seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $logs = Get-ChildItem (Join-Path $appHome 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue
        if ($logs -and (Select-String -Path $logs.FullName -Pattern $pattern -Quiet)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for the log line /$pattern/."
}

function Wait-File([string] $path, [int] $seconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while (-not (Test-Path -LiteralPath $path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $path." }
        Start-Sleep -Milliseconds 250
    }
}

function Write-DemoSettings([string] $theme) {
    Set-Content (Join-Path $appHome 'settings.json') (@{ theme = $theme; openFolderOnSessionEnd = $false } | ConvertTo-Json)
}

# Refuse to run where it could disturb real work: a running instance would own the hotkeys and the single-instance lock.
if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit).' }
if (Test-Path "$drive\") { throw "Drive $drive is already in use; pass a free letter with -DriveLetter." }

dotnet build (Join-Path $root 'src/ScreenshotHelper.App') --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Building the app failed.' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$work = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "sshelper-readme-$([Guid]::NewGuid().ToString('N'))")).FullName
$appHome = (New-Item -ItemType Directory -Path (Join-Path $work 'home')).FullName
$staging = (New-Item -ItemType Directory -Path (Join-Path $work 'out')).FullName
$demo = Join-Path $work 'drive'
$previousHome = $env:SCREENSHOTHELPER_HOME
$mapped = $false
$app = $null

try {
    # Demo data: a solo shot with timestamp and caption, a group of three, and a captioned solo, so the next main is 4.
    $sample = Join-Path $root 'src/ScreenshotHelper.App/Assets/app-256.png'
    New-Item -ItemType Directory -Path (Join-Path $demo 'Project screenshots'), (Join-Path $demo 'Bug reports') | Out-Null
    foreach ($name in '1 (2026-09-27 09.12.40 UTC+13) (welcome screen).png', '2-1.png', '2-2 (settings dialog).png', '2-3.png', '3 (checkout).png') {
        Copy-Item $sample (Join-Path $demo "Project screenshots/$name")
    }

    subst $drive $demo
    if ($LASTEXITCODE -ne 0) { throw "subst $drive failed." }
    $mapped = $true

    $env:SCREENSHOTHELPER_HOME = $appHome
    Write-DemoSettings 'Light'
    Set-Content (Join-Path $appHome 'state.json') (@{ recentFolders = @($project, "$drive\Bug reports") } | ConvertTo-Json)

    # 1. Session tab, light theme.
    $app = Start-App
    Save-Capture $null 'home-light.png'

    # 2. Collision prompt and caption box: continue group 2 at sub 2 (taken), then a main shot (4) and its caption.
    (Find $app 'Continue an existing group').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    Set-Number $app 'Main number' '2'
    Set-Number $app 'Next sub number' '2'
    Invoke-Element $app 'Start session'
    Wait-Log ([regex]::Escape("Session started in $project (ContinueGroup, main 2, sub 2)."))
    Start-Sleep -Milliseconds 800

    [System.Windows.Forms.SendKeys]::SendWait('-')
    Start-Sleep -Milliseconds 1500
    Save-Capture 'Number already taken' 'collision-prompt.png'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')   # Discard
    Start-Sleep -Milliseconds 800

    [System.Windows.Forms.SendKeys]::SendWait('`')
    Wait-File "$project\4.png"
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('c')
    Start-Sleep -Milliseconds 1200
    [System.Windows.Forms.SendKeys]::SendWait('login page')
    Start-Sleep -Milliseconds 300
    Save-Capture 'Caption for 4' 'caption.png'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')   # cancel: the caption isn't saved
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('^+q')
    Wait-Log ([regex]::Escape('Session ended with'))
    Stop-App $app

    # The real screenshot of the desktop goes before anything else is shown.
    Remove-Item -LiteralPath "$project\4.png"

    # 3. Renumber: shift groups 2 and 3 up by two, with a thumbnail preview.
    $app = Start-App
    Invoke-Element $app 'Renumber existing screenshots'
    Invoke-Element $app 'Group 2'
    Invoke-Element $app 'Group 3'
    Set-Number $app 'Main number change' '2'
    Invoke-Element $app 'Preview 2-2 (settings dialog).png'
    Save-Capture 'Screenshot Helper' 'renumber.png'
    Stop-App $app

    # 4. Session tab, dark theme.
    Write-DemoSettings 'Dark'
    $app = Start-App
    Save-Capture $null 'home-dark.png'
    Stop-App $app
    $app = $null

    Copy-Item (Join-Path $staging '*.png') $media -Force
    Get-ChildItem $staging -Name | ForEach-Object { Write-Host "Updated .github/media/$_" }
}
finally {
    Stop-App $app
    $env:SCREENSHOTHELPER_HOME = $previousHome
    if ($mapped) { subst $drive /d }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
