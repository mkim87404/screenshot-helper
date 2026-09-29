# tools/

Standalone helper scripts: they aren't part of the app build and are run by hand from the repo root.

| Script | What it does | Dependencies | Run |
| --- | --- | --- | --- |
| [`generate-icon.cs`](generate-icon.cs) | Draws the app icon in code and writes `src/ScreenshotHelper.App/Assets/app.ico` (16–256 px), `app-256.png` and `.github/media/icon.png`. | .NET 10 SDK; `SkiaSharp 4.152.1` (pinned by the script's `#:package` directive, restored automatically) | `dotnet run tools/generate-icon.cs` |
| [`Capture-Window.ps1`](Capture-Window.ps1) | Captures a window of a running process (or the primary screen) to PNG at physical resolution, with its rounded corners and border matted to true transparency (two captures over white/black backdrops) — used for README screenshots and UI checks. Captures exactly what the window shows, so run the app against demo data before publishing. | PowerShell 7 on Windows (uses the built-in System.Drawing / WinForms assemblies) | `pwsh tools/Capture-Window.ps1 -ProcessName ScreenshotHelper -Output out.png` |
| [`Update-ReadmeScreenshots.ps1`](Update-ReadmeScreenshots.ps1) | Regenerates every README image in `.github/media` from demo data: builds the app, seeds sample files on a `subst` drive, points `SCREENSHOTHELPER_HOME` at a scratch folder, drives the app with UI Automation and SendKeys, and captures each view. Cleans up everything it created, even on failure. **Disruptive:** it runs on your desktop for about a minute, so keep your hands off the keyboard and mouse. | PowerShell 7 on Windows, .NET 10 SDK, `Capture-Window.ps1` | `pwsh tools/Update-ReadmeScreenshots.ps1` |

## Dependencies

.NET scripts here are [file-based apps](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps): each one declares its NuGet
packages with `#:package Name@Version` in its header, which is this folder's equivalent of a `requirements.txt`.
[`Directory.Packages.props`](Directory.Packages.props) switches off the repo's central package management for this folder so those
inline versions apply. PowerShell scripts use only what ships with PowerShell 7.

## Tips

- Set `SCREENSHOTHELPER_HOME` to a scratch folder before launching the app for screenshots or experiments, so your real settings
  aren't touched.
- For one-off screenshots with a neutral path, map a scratch folder to a drive letter first (`subst S: <folder>`, remove with
  `subst S: /d`). `Update-ReadmeScreenshots.ps1` does this for the README images.
