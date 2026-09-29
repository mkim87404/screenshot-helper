# Decision log

Decisions with their justification, empirical findings, hurdles and assumptions, newest first. Entries are history: a later
change is recorded as a new entry rather than by rewriting an old one. Current behaviour is specified in [SPEC.md](SPEC.md).

## 2026-09-29 (UTC)

### First public release: v1.0.0
- **Version 1.0.0**, not 0.x: the v1 scope is complete and the file-naming scheme, settings and keys are meant to stay stable, so
  later breaking changes will bump the major version per [SemVer 2.0.0](https://semver.org/).
- **One release path: the tag-driven `release.yml` workflow.** Pushing `v1.0.0` builds, tests, publishes, attests and creates the
  GitHub Release from CI. No release is created by hand, so assets are never published twice and every asset has a build-provenance
  attestation that `gh attestation verify` can check. The job's `visibility == 'public'` guard is satisfied because the repository is public.
- **CI unchanged:** `ci.yml` already builds and tests on push to `main` and on pull requests (full suite on Windows, Core on Linux),
  with no secrets or schedules. Action versions checked against their official releases: `actions/checkout` v7, `actions/setup-dotnet` v6,
  `actions/attest` v4.
- **Pre-release check:** `build/Build.ps1` passed from clean (194 tests: Core 134, App 35, Platform.Windows 25), and a local
  `build/Publish.ps1` run produced x64/ARM64 exes that report version 1.0.0 and launch cleanly. Those local artifacts were a smoke test
  only; the published ones are built by the release workflow from the tagged commit.
- **README:** added a latest-release badge.

## 2026-09-28 (UTC)

### Audit fixes
- **Log writes moved off the calling thread.** `AppLog` queues lines on a `Channel` and one background writer batches them into the day's
  file, grouped by UTC day, with a disk flush per batch. Before, every line did a synchronous append under a lock, from the session
  actor and the UI thread alike. `Flush(timeout)` drains the queue; `Dispose` (via `using` in `Program`) and the unhandled-exception
  hook both call it, so a crash still writes its stack trace. Lines logged after the flush are dropped rather than throwing. Accepted
  loss window: whatever is queued if the process is killed outright, which is at most one batch.
- **Shutdown can't strand the process.** `ShutdownAsync` wraps saving the window size and disposing the coordinator in try/catch, and
  always disposes the tray, closes the window and ends the lifetime in `finally`. Before, an exception in either step skipped the rest
  and left a tray-less process running. The synchronous wrapper logs a faulted task instead of dropping it.
- **Ending a session closes its open prompts.** Ending with a caption box or collision prompt open left the window on screen, and its
  pending task waited on an actor that was already finished. `ISessionUi.CloseOpenPrompts` closes them. The caption is treated as
  cancelled and the collision prompt returns its default (Discard), so nothing is written after the session ends.
  A caption request still queued when End begins no longer opens a box, because the session could no longer apply its answer. A
  collision prompt for a shot still queued does open: ending drains queued shots, and silently discarding a captured image would lose
  work. A guard that discarded it was tried and reverted after an existing test caught it.
- **Capture lock removed; error paths hardened.** `WindowsScreenCapture` is only called from the session actor, and `Dispose` runs after
  the actor completes, so its lock guarded nothing. The single-caller rule is documented on the class instead. The backdrop is now
  hidden in `finally`, and a half-built result bitmap is disposed if matting throws.
- **Tests leave the machine as they found it.** The Recycle Bin test deletes its own `$I`/`$R` entry, which it finds by parsing the
  original path in `$I` format 1 or 2. The clipboard test snapshots and restores every memory-based clipboard format. It used to be
  skipped locally behind an environment variable; it now runs everywhere. Tests that show windows or touch shared state are tagged
  `Category=Disruptive` and still run locally and in CI.
- **Coverage gaps closed:** the annotation-target setting is tested through a real session, the window size and maximized state are
  tested round-trip, and there are tests for the log writer (concurrency, stack traces, writes after flush).
- **Duplicate key-label formatting** in four places was consolidated into `AppServices.Describe`.
- **`tools/generate-icon.cs` updated to SkiaSharp 4.152.1.** `SKPath`'s mutating methods are obsolete in 4.x (CS0618), so the paths are
  built with `SKPathBuilder`. The regenerated icon differs from the old one only in anti-aliasing (max channel difference 34), and was
  checked visually.
- **README screenshots are now reproducible.** `tools/Update-ReadmeScreenshots.ps1` seeds demo data on a `subst` drive under a scratch
  `SCREENSHOTHELPER_HOME`, drives the app, and captures every image. It checks the log to confirm a session is running before sending
  any session keys. Without that check, an early run sent keys to whatever app had focus.
- **Docs:** the README is the user guide and the single source for default keys and settings, and SPEC §3 and §10 now link to it. The
  README gained update steps and a note on disruptive tests, and dropped a test count that would go stale.

### Timestamp time source (verified)
- A shot's timestamp is always its capture time: the engine reads the clock once, right after the screen grab, and stores it with the shot. Last-shot toggles re-format that stored value; next-shot arming only sets a flag, and the time is read when the shot is taken.
- Pinned by tests with a clock that moves between key presses: three on/off cycles 7 minutes apart always show the original time, and `t` armed at 10:00:00 before a shot at 10:05:33 stamps 10.05.33. Stated in SPEC §6, the README and the Settings hint.

## 2026-09-27 (UTC)

### Round 4: annotation target, clean window captures, caption box, docs layout
- **Timestamp/caption now annotate the shot just taken by default (setting: "Timestamp and caption keys apply to").**
  - Problem (owner): arming a caption *before* capturing makes the user type first while the moment passes. Capturing on the
    caption key instead would be wrong: only the main/sub keys may ever capture, or an unintended screenshot is taken.
  - Decision: capture first, label after. `t`/`c` rename the last shot of the session (add/remove the capture-time timestamp; add/edit/remove
    the caption). One setting governs both keys so they never behave differently; "next shot" remains available for users who prefer
    to set up first. Alternatives rejected: per-key settings (confusing: two keys, two mental models); capture-on-caption (violates the
    only-main/sub-capture rule).
  - Safety: the engine records the exact timestamp/caption text it wrote for each shot and only rewrites a tail that still matches, so a
    shot renamed outside the app is never overwritten. The caption budget for the last shot reserves room for a timestamp added later.
  - Default changed from the original scripts' "arm next shot" behaviour; the owner's old `t` habit still works by switching the setting.
- **Edge noise in window screenshots (found by the owner on README images):** Windows 11 windows have 8-px rounded corners and a
  semi-transparent 1-px border, so any capture of the window rectangle bakes in whatever is behind it (26 distinct colours along one
  README image's frame; the corners showed the editor behind).
  - Fix: difference matting. Capture over a white and then a black backdrop placed directly behind the window (`SetWindowPos` insert-after,
    never activated, class brush painted by `DefWindowProc`, `DwmFlush` to wait for composition); alpha = 255 − mean(white − black),
    colour = black ÷ alpha. Only a 12-px (DPI-scaled) edge band is matted; the interior is taken opaque from one capture, so content that
    changes between the two captures can't punch holes. Maximized windows (no corners/border) skip it; failures fall back to a plain
    capture and are logged (a silent fallback hid one early failure during verification).
  - Applied in both the app's Active window capture (`Core.Imaging.EdgeMatte` + `Platform.Windows.Interop.BackdropWindow`) and
    `tools/Capture-Window.ps1`. Verified: corners alpha ≈ 0, border a uniform translucent grey (5 shades along a side, from its own
    anti-aliasing), three consecutive live captures consistent.
  - Rejected: cropping the border (corners still leak), a single solid backdrop (corners become opaque white/black instead of transparent),
    Windows.Graphics.Capture (WinRT dependency for one mode).
- **Caption box allowed typing past the limit (bug):** the view-model truncated its `Text`, but a two-way binding doesn't push a value
  changed during its own update back to the TextBox, so the box kept the extra characters while the counter said 0. The headless test
  only checked the view-model, so it passed. Fix: enforce at input: `TextBox.MaxLength` (native, covers typing and paste) plus a
  tunnelled `TextInput` filter and a `TextChanged` backstop for invalid characters. `MaxLength = 0` means *unlimited* in Avalonia,
  so a zero budget disables the box instead. The test now asserts the text in the control itself.
- **Insert no longer refuses files that already share a number.** A plan now conflicts only when *different* source numbers would merge
  into one target number; `5-2` and `5-2 (dup)` shifting together to `5-3` doesn't make anything worse. Remaining refusals: a shifted name
  exceeding 260 characters (e.g. `5-9 (long…)` → `5-10`), or a hidden file already holding a target name (the move fails and rolls back).
- **Tab naming:** "Start" was ambiguous, and "Capture" read as an action button. Chose **Session**, which matches the app's existing
  vocabulary (Start session, New session, End session, Session Summary), over "Screenshot" (still verb-like). The nav items were also
  restyled as tabs with an underline on the current area, because the root cause was action-button styling.
- **Docs layout:** the decision log is `docs/DECISIONS.md` (this file) and the functional spec is `docs/SPEC.md`. SPEC states
  behaviour only and links here for reasons; rationale that lived only in the spec (key-combination reasoning, cross-platform
  explanation) is covered by the entries below and by this note:
  - *Cross-platform stack, in brief:* Avalonia draws its own UI (Skia) and supplies windowing, input, dialogs and tray on Windows, macOS
    and Linux, so views, view-models and Core run unchanged; WPF's UI layer is Windows-only. No UI framework covers the OS services this
    app needs (global hotkeys, capture, Recycle Bin, reveal, focus, capture-exclusion), which is why they sit behind Core interfaces.

### Round 3: fixes and audit answers
- **Stray Explorer windows (bug, found by the owner):** the platform test `Revealer_tolerates_missing_files` passed a non-existent folder, and `explorer.exe "<missing path>"` silently opens its default location (Documents). Every test run leaked one window.
  - The revealer now opens nothing for a missing folder, and only selects files that exist inside the folder.
  - The decision is a pure function (`FilesToReveal`) tested without launching Explorer.
  - Rule adopted: tests must never launch visible external processes.
- **Sound semantics tightened:** the main/sub sound ⇔ the file is on disk.
  - Previously a shot saved with a caveat played the *warning* sound, which is also used for "discarded" — ambiguous.
  - Caveats now keep the success sound and tint the toast (`FeedbackEvent.HasCaveat`).
  - Test `Saved_sounds_fire_only_once_the_file_is_on_disk` checks the file exists at the moment each saved event is raised.
- **Insert never cascades:** all members at or after the insertion point shift as *one* planned set (highest first). Gaps are preserved, not compacted.
  - If the set can't be shifted safely (e.g. two files already share a number), the whole insert is refused and the shot is appended, with a caveat. Both cases are tested.
- **Growth bounds:**
  - Logs: pruned to 30 days at start-up.
  - Corrupt-settings backups: newest 3 kept.
  - Journals: only in-flight plans + the last completed one.
  - Recent folders: 10.
  - Tray icon cache: one per state (5).
  - Orphaned temp shots: removed when a session starts or ends in that folder.
  - The Recycle Bin (overwrite/undo) is managed by Windows' own size limit.
- **Abrupt termination verified:** started a session, force-killed the process, relaunched. The single-instance object was gone and all 7 hotkeys registered again, because the OS releases hotkeys, window/GDI handles, the tray icon, the mutex and memory on process exit.
  - What a kill can leave: at most one hidden `.~sshelper-*.tmp` (cleaned next session in that folder) and an in-flight renumber journal (offered for Finish/Undo on the next start). Settings and journals are written atomically; the log is flushed per line.
  - Known Windows limitation: a killed process's tray icon can linger until the mouse passes over it.
- **Window size:** the last normal size + maximized flag are remembered; the position deliberately isn't, so the window is always centred and can't open off-screen after a monitor change. The size is clamped to the working area.
  - Default raised to 900×860 so the Start button fits with the two-row key guide.
- **"Start" tab renamed "Capture":** it was ambiguous next to the "Start session" button. The other tabs are nouns for areas (Renumber, Settings), and the summary page's "New session" button leads back to Capture, so there's no clash.
- **Key guide** is now wrapping key-cap chips instead of one dot-separated line.
- **Release workflow guarded:** it runs only when the repository is public, so tags pushed to a private copy or fork never create a release. `build/Publish.ps1` only builds files into `artifacts/` locally; it publishes nothing.

### v1 implemented (owner: "go ahead with the dev", plus dark mode and an app icon)
- **Solution:** `Core` (net10.0), `Platform.Windows`, `App` (Avalonia 12.1.3 + CommunityToolkit.Mvvm 8.4.2), one xUnit v3 test project each.
  154 tests pass (Core 110, Platform 20 + 1 opt-in, App 24).
- **Dependency pins:**
  - All in `Directory.Packages.props` (central package management).
  - xUnit is pinned to **3.2.2**, not the newer 4.x, because `Avalonia.Headless.XUnit` 12.1.3 is built against `xunit.v3.extensibility.core` 3.2.2.
  - SDK pinned in `global.json` (10.0.401, `rollForward: latestFeature`).
- **Build attestations** only run when the repo is public: GitHub Free/Pro/Team plans offer them for public repos only ([attest-build-provenance README](https://github.com/actions/attest-build-provenance)).

### Decisions made during implementation
- **Sounds are synthesised in code** (`ToneSynth`/`SoundBank`) instead of shipping WAVs generated by a tools script.
  - No binary assets or licensing questions.
  - Volume is applied by re-rendering.
  - Playback: `PlaySound(SND_MEMORY|SND_ASYNC)` reads the buffer *during* playback, so each sound is copied to unmanaged memory that is freed only after `PlaySound(NULL)` has stopped it.
- **Collision = same numbering, not the same filename.** `5-4.png` and `5-4 (caption).png` collide.
  - Exact-name races are additionally blocked by the no-clobber rename, which retries at the next free number.
- **Rescan before every decision.** `FolderIndex.Scan` is cheap (one directory enumeration), so the engine never trusts cached state that could drift from disk (other programs, Explorer edits).
- **Solo→member rename is decided from disk state** (the group currently consists of exactly one solo file), so it also covers groups continued from an earlier session.
- **Caption budget is worst-case:** longest numbering either key could produce + a full timestamp + an 8-character growth reserve.
  - Typed text is therefore never truncated later.
  - `FitToPath` remains as a safety net (never splits surrogate pairs).
- **Renumber ordering** uses "move when your target is free, park one file under a temp name on a cycle". For uniform shifts this reduces to highest-first (positive) and lowest-first (negative).
  - Randomised property tests (8 seeds × 40 rounds) check that every file and tail survives.
- **Renumber failures roll back automatically** (all-or-nothing for the user). The journal is kept only if the rollback also fails, and is then offered on next start (Home banner: Finish / Undo).
- **Journals are untrusted input:** steps must be bare file names; ids must be filename-safe; the folder must be fully qualified.
- **Single instance uses the "created new" mutex pattern (never owned).**
  - Owning a mutex is thread-affine: `ReleaseMutex` from another thread throws, which the tests hit after an `await`.
  - An owned mutex is also left abandoned after a crash.
  - The named object's existence is enough, and the OS deletes it when the last handle closes.
- **Settings store W3C physical key codes** (`KeyT`, `Backquote`). Windows maps them scan code → virtual key under the active layout, so bindings follow key *position*, with labels from `GetKeyNameText`.
  - Lone punctuation labels get a name, e.g. `` ` (backtick) ``, because a bare glyph is easy to miss.
- **Explorer isn't opened when a session saved nothing.**
- **`SCREENSHOTHELPER_HOME`** redirects settings, logs and journals to one folder (tests, screenshots). It's also the basis for the roadmap's portable mode.
- **Release artefact:** a self-contained single-file exe is **48 MB** (compressed; native Skia/HarfBuzz extracted on first run). Idle working set is about 170 MB. Trimming was not enabled: System.Drawing and reflection-based paths would need auditing first.

### Empirical findings (bugs and platform behaviour found by tests and live runs)
- **The Hidden attribute survives a rename.** Temp shot files are hidden, and `File.Move` kept the attribute, so finished screenshots were hidden and the folder scan skipped them. That broke the solo→member rename.
  - Found by the engine integration tests.
  - Fix: clear attributes before the final rename. The platform test now asserts the saved PNG isn't hidden.
- **System.Drawing rejects combined `CopyPixelOperation` flags** (`SourceCopy | CaptureBlt` → `InvalidEnumArgumentException`). Plain `SourceCopy` is used instead.
  - The desktop is always DWM-composed since Windows 8, so layered windows are included anyway.
  - CAPTUREBLT is known to make the cursor flicker.
- **Avalonia names letter keys `A`..`Z`, not the W3C `KeyA`.** Every letter rebinding would have been rejected as unsupported.
  - Found by the headless key-capture test.
  - Fix: map to `Key{letter}` when capturing.
- **Headless key events route to the focused element's ancestors.** A handler on the Settings view missed keys when focus was elsewhere.
  - The handler now sits on the window while the view is attached, and is removed on detach, so nothing leaks.
- **`net10.0-windows` defaults to TargetPlatformVersion 7.0**, so `SupportedOSPlatformVersion 10.0.19041` fails the build (NETSDK1135).
  - Dropped rather than switching to a `-windows10.0.x` TFM, which pulls in WinRT projections. Minimum Windows (10 2004) is documented in the README instead.
- **Central package management also applies to .NET 10 file-based apps** (`dotnet run tools/x.cs` → NU1008 for `#:package X@ver`).
  - `tools/Directory.Packages.props` turns CPM off there, so scripts keep their inline pins.
- **Capture exclusion verified live (Windows 11 26200):** the toast window reports affinity `0x11`, and screenshots taken while it was visible don't contain it.
- **Caption focus verified live:** after the `c` hotkey, the caption window became foreground (`SetForegroundWindow` allowed because our process received the last input). Typing `-`/`t` in the box didn't trigger captures, because the keys are unregistered while the box is open.
- **End-to-end session verified with injected keystrokes:**
  - Main → `4`; sub → `4` renamed `4-1` and `4-2` saved; `t` + sub → `4-3 (… UTC+13)`; main → `5`.
  - Caption + sub → `5` renamed `5-1` and `5-2 (login page -t-)`.
  - Pause blocks capture; undo recycled `5-2` and renamed `5-1` back to `5`.
  - End → Explorer showed the 4 session files selected (checked via `Shell.Application`).
- **Avalonia build telemetry:** `Avalonia.BuildServices` sends anonymous build telemetry unless `AVALONIA_TELEMETRY_OPTOUT=1`. The build scripts and CI set it. [Avalonia.BuildServices](https://github.com/AvaloniaUI/Avalonia.BuildServices)
- **UI Automation:** named controls are reachable (tools and screen readers). `NumericUpDown` exposes its value through an inner Edit element rather than on the control itself.

### Deviations from the spec (updated in `docs/SPEC.md`)
- Sound synthesis at runtime instead of a WAV-generator tool.
- Toast safety relies on verified capture exclusion rather than also hiding the toast before each capture: hiding would add a UI-thread round trip to every shot.
- The Explorer reveal is skipped for empty sessions.

## 2026-09-26 (UTC)

### Decisions (owner, round 2) → spec accepted
The functional spec is [`docs/SPEC.md`](SPEC.md) (renamed from `docs/spec.md`). It replaces the earlier `docs/design-proposal.md`, which was deleted as superseded.
- **D1 Stack:** C# / .NET 10 LTS + Avalonia 12.
- **D5 Platform:** Windows-only v1. `Core` targets plain `net10.0` with no OS APIs and is tested on Linux CI to prove it is portable.
- **D2 Hotkeys:** exclusive `RegisterHotKey` while a session is active and not paused. `MOD_NOREPEAT` always set (one shot per physical press).
- **D3 Meta controls use key combinations, not a double-tap.** Defaults: Pause `Ctrl+Shift+Space`, Undo `Ctrl+Shift+Z`, End session `Ctrl+Shift+Q`. Frequent keys stay bare: `` ` `` `-` `t` `c`.
- **D4 End of session:** the app stays open on the Summary screen. Explorer selects all session files (fallback: the lowest-numbered file, then just the folder). "Exit app when session ends" is a setting (default off).
- **Name:** "Screenshot Helper", with no platform in the name. The platform goes in the tagline and badges: a recruiter reading from a CV link wants a plain, descriptive name, and a platform suffix would be wrong once a port exists.
- **Timestamp:** second precision; Local (default, with an OS-derived `UTC±H[.mm]` label so daylight saving is correct) or UTC. Format `yyyy-MM-dd HH.mm.ss`.
- **Solo→member rename kept.** It is evaluated against disk state (not session memory), happens before the save, and falls back safely when the file is locked or the group is inconsistent (spec §2).
- **Distribution:** GitHub Releases via a tag-triggered Actions workflow: self-contained single-file exe (x64 + arm64), zips, SHA256 checksums, build provenance attestation. Unsigned; no store. winget/Scoop are on the roadmap (they only need a GitHub account but require a first release).

### Key-combination research (D3)
- **Microsoft guidance:** "Avoid CTRL+ALT combinations because the system interprets this combination in some language versions as an ALTGR key, which generates alphanumeric characters." Example: US-International AltGr+P = `ö`, AltGr+Z = `æ`, so registering `Ctrl+Alt+P` or `Ctrl+Alt+Z` would stop those users typing those characters while registered. → **Ctrl+Shift family chosen.** [Guidelines for Keyboard UI Design](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/dnacc/guidelines-for-keyboard-user-interface-design)
- **What exclusivity does and doesn't cover:** while registered, the exact key + modifier combination goes only to this app. It does **not** protect against collisions:
  - `RegisterHotKey` fails if another app already owns the combination, which we detect and report.
  - OS-reserved combinations can't be taken: `Ctrl+Shift+Esc` is Task Manager; `Ctrl+Esc` and `Alt+Esc` are system accelerators ([About Keyboard Accelerators](https://learn.microsoft.com/en-us/windows/win32/menurc/about-keyboard-accelerators)). So Esc can't be part of a Ctrl+Shift end-session combination; hence `Q`.
  - The combination also becomes unavailable to the foreground app (e.g. `Ctrl+Shift+Z` = Redo in some apps) for as long as it is registered.
- **Why pause got Space:** undo and end-session are unregistered during a pause, but pause/resume stays registered, so it is the combination most likely to hit a user who is typing.
  - `Ctrl+Shift+P` was rejected: it is VS Code's command palette and Firefox's private window.
  - `Ctrl+Shift+Space` is rarely bound (Word: non-breaking space), sits in the same place on every layout, and can be pressed with one hand (thumb on Space).
- **Runtime safeguard:** Settings warns when a chosen combination would type a character on the active layout. This is checked with the layout's key→character translation (ToUnicodeEx), and covers users who rebind to Ctrl+Alt anyway.
- To verify in the hotkey spike: a bare `t` registration must not swallow `Shift+t` (`T`); Caps Lock + `t` still has the same key code with no modifiers, so it *is* withheld.

### Distribution research
- winget: submission is a PR to the public `microsoft/winget-pkgs` repo (GitHub account only). Packages go through antivirus and installer validation, the installer URL must be the publisher's own release location (GitHub Releases qualifies), and code signing is not listed as a requirement. [Submit your manifest](https://learn.microsoft.com/en-us/windows/package-manager/package/repository)
- GitHub artifact attestations are available for public repositories on all current plans (private repos need Enterprise Cloud). They are created with `actions/attest-build-provenance` and verified with `gh attestation verify <file> -R <owner>/<repo>`. [Artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations), [attest-build-provenance](https://github.com/actions/attest-build-provenance)

### To verify in spikes (`tools/`)
- `WDA_EXCLUDEFROMCAPTURE` also hides the toast from our own capture API (BitBlt / DXGI). The pipeline hides the toast before capture regardless.
- A `SetForegroundWindow` call made after `WM_HOTKEY` succeeds, so the caption box can take focus.
- Exact-match semantics of bare-key registrations (above).

### Toolchain verified on the dev machine
- .NET SDK `10.0.401`, Node `v24.13.0`, Python `3.13.11`, PowerShell `7.6.6` (+ Windows PowerShell 5.1), git `2.52.0`, gh `2.101.0`. Rust is not installed.
- Windows 11 build 26200. `LongPathsEnabled = 0` (registry), so MAX_PATH (260) applies to this machine.

### Version / release status (verified against official sources, 2026-09-26)
- **.NET 10 is the current LTS**: released 2025-11-11, patch 10.0.12, supported until 2028-11-14. .NET 8 LTS and 9 STS both reach end of support on 2026-11-10. .NET 11 is at RC1 with GA expected Nov 2026 (STS, so the LTS rule rules it out). → Target `net10.0`. [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- **Avalonia 12.1.3** is the latest stable (2026-09-22). Avalonia 12 GA'd 2026-04-07 targeting .NET 10. MIT licence. Its paid products (dev tools, component libraries) aren't needed. Built-in `TrayIcon` (Windows full support) and `Avalonia.Headless` UI testing with xUnit/NUnit. [NuGet](https://www.nuget.org/packages/Avalonia), [Avalonia 12 blog](https://avaloniaui.net/blog/avalonia-12), [TrayIcon](https://docs.avaloniaui.net/docs/reference/controls/tray-icon), [Headless](https://docs.avaloniaui.net/docs/concepts/headless/)
- Windows App SDK (WinUI 3) latest stable is 2.4.0. [Downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)

### Findings in the original Python scripts (not included in this repository)
1. **Auto script, sub key pressed first:**
   - `main-num` is initialised to the highest existing main, and `sub-num` to 1.
   - The first sub press therefore writes `{highest}-2.png` into the **existing** last group (skipping `-1`). `mss` overwrites without asking, so an existing `{highest}-2.png` is **silently destroyed**.
   - It then tries to rename `latest-main-screenshot-filepath` (still `''`), which fails and is swallowed.
   - The owner remembered this as "starts from sub 0". The actual behaviour is sub 2 in the previous group, with data-loss risk.
2. **Custom script, main key pressed first:** `main-num += 1` runs before saving, so the configured main `X` is skipped and the first shot is `X+1`. Starting with sub works, but `sub-num == 2` triggers the rename of an empty path (it fails silently).
3. **Auto-detect regex `^(\d+)`** counts any PNG starting with digits (e.g. `20250101_123456.png` → main 20,250,101). Its sub numbers are never read.
4. **Silent failures:** every capture and rename sits in `try/except: pass`. A failed save still looks like success, because the sound never plays and nothing else signals the error.
5. **Hook timeout risk:** capture, PNG encode, disk write and blocking `winsound.Beep` all run inside the pynput keyboard-hook callback. Microsoft docs: if a low-level hook callback exceeds `LowLevelHooksTimeout` (at most 1000 ms on Win10 1709+), "on Windows 7 and later, the hook is silently removed … There is no way for the application to know". A slow disk or network folder could quietly kill the listener mid-session. [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
6. **Keys pass through:** the pynput hook doesn't suppress keys, so typing `-`, `` ` `` or `t` anywhere fires actions, and the keys also reach the app being captured. Holding a key auto-repeats captures.
7. **Monitor 1 only:** `mss.shot()` forces `mon=1` ("Helper to save the screenshot of the 1st monitor, by default"). [mss source](https://raw.githubusercontent.com/BoboTiG/python-mss/main/src/mss/base.py)
8. **Timestamp label hard-coded as "NZT"** while using the local time, which is wrong during NZ daylight time (NZDT, UTC+13).
9. **`explorer /select,` via command line** only selects one item and is fragile with commas in paths. `first-main-screenshot-filepath` only tracks *main* shots, so a session that starts with sub (the custom script's normal flow) falls back to opening the folder with no selection.
10. The solo→member rename uses `str.replace(main, main-1, 1)`. That is correct only because the name starts with the number. The new design rewrites only the parsed prefix.

### Platform facts that drive the design
- `RegisterHotKey`:
  - Supports `MOD_NOREPEAT` (no auto-repeat bursts).
  - Fails if the key is already registered elsewhere, so conflicts can be reported.
  - F12 is reserved for debuggers.
  - `WM_HOTKEY` goes to the registering thread's message loop, so no hook timeout applies.
  - [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- `SetForegroundWindow` is allowed when "the calling process received the last input event", among other conditions. This is relevant for bringing up the caption box from a hotkey, and a spike will confirm it with `RegisterHotKey`. [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (Win10 2004+) keeps the toast out of screenshots. [Docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- `SHOpenFolderAndSelectItems` selects **multiple** items in Explorer. It requires COM to be initialised on the calling thread. [Docs](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems)
- `File.Move(src, dst, overwrite:false)` throws `IOException` if the destination exists. Same-volume moves are renames, which gives an atomic no-clobber commit when the temp file is in the target folder. Note: it does **not** throw when source equals destination, so the planner must treat no-op renames explicitly. [File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)
- MAX_PATH is 260 and the per-component limit is typically 255. Long paths need both the registry value and a `longPathAware` manifest. The shell may not interpret long paths correctly. → The caption budget uses 260 for interoperability. [Maximum path length](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation)

### Cross-platform feasibility
- **Linux/Wayland:** the GlobalShortcuts portal has the *user* bind keys through a portal dialog, with the app only suggesting a trigger. The Rust `global-hotkey` crate (used by Tauri) supports "Linux (X11 Only)". [Portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html), [global-hotkey](https://docs.rs/global-hotkey/latest/global_hotkey/)
- **macOS:** notarisation requires the paid Apple Developer Program, which isn't available on free accounts. [Apple memberships](https://developer.apple.com/support/compare-memberships/)
- **Docker:** not applicable. Containers have no access to the host desktop session (keyboard, screen).
- **Windows distribution (free):**
  - Microsoft Store developer registration is free for individuals (since 2025-09-10) and companies (since 2026-05). [Windows Dev Blog](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)
  - SignPath Foundation offers free code signing for OSS projects. [signpath.org](https://signpath.org/)
- → Recommendation: Windows-only v1 with an OS-independent `Core` behind platform interfaces (see spec §12).

### Crash-safety applicability (long-running desktop app)
| Point | Applies | How |
|---|---|---|
| Deterministic disposal | Yes | Capture bitmaps/GDI handles and encoders scoped per shot (`using`). The hotkey registrations, message-loop thread and tray icon are long-lived and disposed on shutdown. COM PIDLs freed with `ILFree`/`CoTaskMemFree`. |
| Global crash net | Yes | `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, and Avalonia dispatcher exception handling → local log in `%LOCALAPPDATA%`, unregister hotkeys, exit cleanly. |
| Bound damage | Yes | Temp file + atomic no-clobber rename per shot. Journal for multi-file renumbering. Settings written atomically (temp + replace). |
| No leaked listeners/timers | Yes | Hotkeys unregistered on pause, end and exit. View-model subscriptions and toast timers disposed with their windows. |
| Shared-state safety | Yes | A single session actor (a `Channel` consumer) owns all state and file operations. The UI only receives immutable snapshots. |
| Lock discipline | Mostly N/A | No locks, by design (actor). |
| Idempotency | Yes | Journal steps are idempotent (skip a step whose source is gone and whose target exists). Undo checks the current state before acting. |
| Async pipeline cleanup | Yes | Cancellation on session end drains or completes the channel and deletes any orphaned temp file. |
| Graceful shutdown | Yes | App-closing and session-end hooks unregister hotkeys, flush the ledger and journal, and remove the tray icon. The Windows session-ending message is handled the same way. |
