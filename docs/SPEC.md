# Screenshot Helper — Functional Specification

What the app does, kept current. The reasoning behind these behaviours lives in the [decision log](DECISIONS.md). Section numbers
are referenced from code comments (e.g. "spec §5"), so keep them stable.

**Stack:** C# / .NET 10 LTS + Avalonia 12. **Platform:** Windows 10 (2004+) and 11, x64 and ARM64; the core is OS-independent (§12).

---

## 1. Concepts & filename format

| Term | Meaning | Example |
|---|---|---|
| **Group** | All shots sharing a main number | `5`, `5-1`, `5-2` |
| **Solo** | A group containing one shot, stored without a sub number | `5.png` |
| **Member** | A shot with a sub number | `5-3.png` |
| **Tail** | Everything after the number prefix | ` (2026-09-27 10.05.33 UTC+13) (login page)` |

**Format:** single spaces between the parts; timestamp and caption each in round brackets and each optional.
```
{numbering} ({timestamp}) ({caption}).png
5.png
5-2 (2026-09-27 10.05.33 UTC+13).png
5-3 (login page).png
5-4 (2026-09-27 10.06.02 UTC+13) (login page).png
```

**Parsing:**
- Only the numeric prefix `^([0-9]+)(?:-([0-9]+))?` is parsed or rewritten. The tail is copied exactly by every rename (solo→member, insert, undo, Renumber), e.g. `3-1(blah)` → `1-1(blah)`.
- Zero-padding width is preserved (`05` stays two digits).
- A `.png` is recognised only if its tail is empty or starts with a space or `(`; anything else (e.g. `20250101_123456.png`) is foreign: never renamed, never counted for numbering, but still respected for collisions.
- Tail edits by the app (§6) only add or remove the app's own timestamp/caption segments.

## 2. Session model

The **Session** tab (the Start Session screen):

| Mode | Defaults (from a folder scan) | Main key | Sub key |
|---|---|---|---|
| **New group** (default) | main = highest + 1 | saves `M` (solo) | saves `M-1` |
| **Continue group** | main = highest; sub = highest sub in that group + 1 | opens the next free group | saves `M-S` |

- Hint under the inputs: *"Highest in folder: `7-4 (login page).png`"*.
- Live collision check with a one-click fix: *"`7-5` already exists — next free is `7-6` [Use]"*. Starting on a colliding number is allowed; §5 then applies.
- Capture target dropdown (§4), pre-filled with the last used value.
- A key guide shows the session keys as key-cap chips.

**Solo → member rename:**
- When a sub shot is saved into group M and group M on disk consists of exactly one solo file, that file is renamed `M-1` (tail kept) first, and the new shot is saved as `M-2`. This is decided from disk state, including for groups continued from an earlier session.
- If the save then fails, the folder stays consistent (`M-1` alone); the next sub shot becomes `M-2`.
- Rename blocked (file open elsewhere): 3 retries over ~300 ms; then the shot is saved as `M-2` anyway, with a caveat.
- Group with both a solo and members: no rename; the shot takes the next free sub, with a caveat.
- Setting **"Always number the first shot `M-1`"** (default off): no solo is ever created, so this rename never happens.

## 3. Keys & input

Default keys: the key table in the [README's How to use](../README.md#-how-to-use) (the user guide is their single source).

| Action | Registered while |
|---|---|
| Main shot | session active, not paused |
| Sub shot | session active, not paused |
| Timestamp (§6) | session active, not paused |
| Caption (§6) | session active, not paused |
| Pause / resume | whole session, including while paused |
| Undo last shot | session active, not paused |
| End session | session active, not paused |

- **Mechanism:** `RegisterHotKey`, exclusive. Only the exact key + modifier pairs above are withheld from other apps. `MOD_NOREPEAT` is always set: holding a key takes one shot. Nothing is registered outside a session.
- **Only the main and sub keys take screenshots.** No other key or action captures.
- **Rebinding (Settings):** press the new key; stored as a physical key (W3C code, e.g. `KeyT`) + modifiers, labelled per the active layout, with punctuation named (`` ` (backtick) ``).
  - Rejected: keys that can't be registered (e.g. F12), duplicates, and a pause key without Ctrl/Alt/Win.
  - Warned: a combination that also types a character on the current layout.
  - Registration conflicts with other apps are reported per key when a session starts; the session doesn't start.
  - **Restore default keys** resets every key to its default.
- Why these defaults: [decision log, 2026-09-26](DECISIONS.md#2026-09-26-utc).

## 4. Capture pipeline

```
Hotkey thread ──► Channel<Command> ──► Session actor (single consumer; owns all state + file ops)
                                           │ 1. capture to memory
                                           │ 2. resolve name / collisions (image already in memory)
                                           │ 3. solo→member rename if needed (atomic, no-clobber)
                                           │ 4. encode PNG → hidden temp file in the target folder → flush
                                           │ 5. File.Move(temp, final, overwrite:false)   (atomic, no-clobber)
                                           │ 6. update session ledger; optional clipboard copy
                                           └ 7. sound + toast + tray state (async, never blocks)
```

- All state and file operations run on the session actor, in key-press order; the hotkey thread only enqueues.
- The final rename is atomic and never overwrites. Orphaned temp files are removed at session start and end.
- Capture happens before any prompt; the toast is excluded from capture (§7).
- **Capture target:** monitor under cursor (default) / primary monitor / all monitors / active window. Chosen on the Session tab (remembered) and switchable from the tray mid-session.
- **Active window:** captured over a white and then a black backdrop placed directly behind it, and the edge band (12 px at 100 % scaling) is matted to true transparency, so rounded corners and the translucent border contain nothing from behind the window. Maximized windows are captured plainly. If matting fails, a plain capture is saved and the failure is logged.
- Per-monitor-V2 DPI awareness; captures are at physical resolution.
- **Copy to clipboard as well:** setting, default off (CF_DIB + PNG formats).

## 5. Collision policy

| Trigger | Behaviour |
|---|---|
| **Main key**, next main is taken | Skip to the next free main; saved with a caveat ("5 was taken — saved as group 6"). No prompt. |
| **Sub key**, `M-S` is taken | Prompt (image already captured): **Append** as `M-{highest+1}` (default) · **Insert** (all members `M-S…` shift up by one as a single planned set, then save as `M-S`) · **Overwrite** (old file to the Recycle Bin) · **Discard**. Checkbox: *"Do the same for the rest of this session"* (not for Overwrite). |
| Insert can't be planned (a shifted name would exceed the 260-character path limit, or a hidden file holds a target name) | The shot is appended instead, with a caveat. Files that already share a number shift together. |
| Solo→member target `M-1` exists | See §2 (mixed group). |

Settings → "When a sub number is taken": **Ask** (default) / Append / Insert.

- Keyboard: the prompt opens with **Append** highlighted; the choices can be moved between and picked from the keyboard, and closing
  it without choosing discards (keys: [README, Features](../README.md#-features)).

## 6. In-session modes

- **Pause:** unregisters every key except pause/resume, so everything else types normally. Tray click also toggles pause.
- **Timestamp and caption target** — one setting for both keys: **the screenshot just taken** (default) or **the next screenshot**.
  - *Just taken:* `t` adds the shot's capture time to its name, or removes it if present; `c` opens the caption box pre-filled with the shot's caption, and saving renames the shot (empty text removes the caption). With no shot yet in the session, both warn and do nothing. A shot renamed outside the app is left unchanged, with a warning.
  - *Next:* `t` arms/disarms a timestamp for the next shot; `c` arms a caption for it (empty text cancels).
  - In both modes the timestamp is the moment the main/sub key took the shot: the clock is read once per shot, at capture. Arming `t` beforehand only sets a flag, and toggling it afterwards, any number of times, re-applies that same stored time.
- **Always timestamp** setting (default off): every shot is timestamped; `t` then only reports that.
- **Timestamp format** (filename-safe, second precision): Local (default) `2026-09-27 10.05.33 UTC+13` — offset from the OS, so daylight saving is right; half-hour zones render as `UTC+5.30` — or UTC `2026-09-26 21.05.33 UTC`.
- **Caption box:** opens with the capture keys released, and gives focus back to the previously active window when closed. Enter saves, Esc cancels (nothing changes).
  - The text box stops accepting input at the budget (typing and paste), and drops `< > : " / \ | ? *` and control characters as they arrive, with a note.
  - Budget = min(255 − name length, 259 − full path length) − `" ()"` − 8 characters of growth reserve (a later `-1` plus renumbering digits), also reserving room for a timestamp that may still be added. Long paths are always treated as disabled.
  - Leading and trailing whitespace is trimmed on save. A zero budget disables the box with an explanation.

## 7. Feedback: sound + visual for every event

Every event has a sound and a toast; persistent states also show on the tray icon. The toast is small, click-through, never takes
focus, is excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`), and its corner is configurable. It appears in that corner of the
screen under the mouse and disappears after 2.2 s (errors: 5 s); a new message replaces the current one and restarts the timer.

| Event | Sound | Toast | Tray icon |
|---|---|---|---|
| Session started | rising chime | "Session started — next: 7 (main) or 7-1 (sub)" | active |
| Main saved | low tone | "✓ 6" (amber if with a caveat) | — |
| Sub saved (incl. solo→member rename) | high tone | "✓ 6-2" (amber if with a caveat) | — |
| Timestamp added / removed (or armed / disarmed) | two-note rise / fall | "⏱ Timestamp added to 6-2" | badge while armed |
| Caption set / removed or cancelled | soft tick / soft fall | "✎ Caption added to 6-2: …" | badge while armed |
| Paused / resumed | low / high click | "⏸ Paused" / "▶ Resumed" | paused (grey) / active |
| Undo | reverse sweep | "↶ Removed 6-2" | — |
| Warning (nothing saved or changed) | double blip | explanation | — |
| App launched again during a session | double blip (warning) | "Screenshot Helper is already running a session — use the tray icon, or *end-session key* to end it" | — |
| Error (capture/save failed, folder gone) | low buzz | error text | error (red) until the next success |
| Session ended | falling chime | "Session ended — 3 screenshots" | — |

- **"Paused" stays on screen** until the session resumes or ends; any message shown meanwhile disappears back to it. The capture keys
  do nothing while paused (§6), so the state must not be missable.
- The toast window has a fixed size and is placed before it appears; only the message card inside it changes, so it never moves or
  resizes while visible. It keeps clear of an auto-hide taskbar (or other auto-hide bar) on any edge and of any thickness, which would otherwise cover it
  whenever it slides out
  ([why](DECISIONS.md#2026-10-02-utc)).
- **The main/sub sound plays if and only if the PNG is flushed and in place.** Caveats keep that sound and only tint the toast.
- The warning sound means attention is needed and nothing was saved or changed (discarded shot, nothing to undo/annotate, keys in use).
- Sounds are synthesised in code at start-up and re-rendered when the volume changes. Settings: sounds on/off, volume, per-event on/off, toasts on/off and corner.

## 8. Window lifecycle & end of session

- Session start → the main window hides; the tray icon offers Pause/Resume, Caption, Undo, Capture target ▸, End session, Show, Exit.
- **End session:** if at least one shot was saved, Explorer opens with all this session's files selected, lowest number first (fallback: select the lowest-numbered file, then open the folder). The window then shows the **Session Summary** (count, files, Show in Explorer, Renumber these…, New session).
- Settings: **Open the folder when a session ends** (default on), **Exit the app when a session ends** (default off).
- **Session ledger:** tracks each file the session created through every rename the app makes, for Reveal and "lowest numbered". Files changed outside the app are skipped.
- **Recent folders:** last 10, on the Session tab; missing paths are marked and removable. A path too long for the window is shortened
  in the middle (drive and last folder kept); its tooltip, and the folder boxes' tooltips, show the full path.
- **Windows appear only once drawn:** the main window, the caption box, the collision prompt and the toast are shown cloaked, then
  fade in (180 ms) once their first frame is drawn, so no empty or stale frame is ever visible ([why](DECISIONS.md#2026-10-02-utc)).
- **Navigation:** app-bar tabs **Session** (setup + summary), **Renumber**, **Settings**; the current tab is underlined.
- **Start session** sits in an action bar pinned below the Session tab's scrolling content, with any start error above it, so it stays in
  view however long the page gets. It's the tab's default button and has an access key (keys: [README, How to use](../README.md#-how-to-use)).
- **Window size:** the last normal size and maximized flag are remembered; the position is not (always centred). Default 900×860, clamped to 95 % of the working area; minimum 720×560, reduced on smaller screens.
- **Single instance:** a second launch activates the running one. During a session the window stays hidden, so the running one shows
  the "already running" warning toast instead (§7).

## 9. Renumber tool

Files are listed by group with multi-select (whole-group toggle) and a thumbnail preview.
- **Which folder:** switching to the tab opens the Session tab's folder if that changed since the last visit; otherwise the tab keeps
  the folder it shows, including one chosen with its own Browse. Opening it from a session summary shows that folder with the
  session's files pre-selected.
- **Staying current:** the list is checked against the folder (one directory listing) on every switch to the tab and whenever the
  window is activated again while the tab is shown (e.g. after renaming files in Explorer). It's rebuilt, with the selection carried
  over, only if the folder's screenshots changed. Apply, Undo and **Reload** always rebuild it. Changes made while the app stays in
  front aren't watched for; **Reload** picks them up.
- Only the rows in view are built, so a large folder opens quickly.

| Operation | Example |
|---|---|
| **Shift** (Δmain, Δsub) | `1-1,1-2,1-3` (Δm+2, Δs−1) → `3-0,3-1,3-2` |
| **Close gaps** (groups from X and/or subs from 1) | `3-1,3-2,3-4` → `3-1,3-2,3-3` |
| **Move to group** (group X from sub Y) | `4, 4-*` → `3-5…` |
| **Normalise solos** | lone `M-1` → `M`; solo `M` beside members → `M-1` |

- **Preview** shows old → new names with per-row status; **Apply** is enabled only when the whole plan is valid:
  - no two *different* source numbers map to one target number, and no two files to one name (case-insensitive);
  - no target number or name is used by a file outside the plan;
  - no negative numbers (sub 0 allowed, with a warning);
  - every name fits the 260-character path limit.
- Only numbers change; tails are copied exactly (§1).
- **Order:** a file moves only once its target name is free; cycles go through a temporary name. For a shift this is highest-first for positive deltas and lowest-first for negative ones.
- **Journal:** the plan is written atomically before the first rename and each completed step is flushed. A failure mid-run rolls back automatically; an interrupted run (crash) is offered for Finish / Undo on the next start. **Undo last renumber** reverses the last completed plan.
- Single-file sub, caption or timestamp edits are left to Explorer (F2).

## 10. Other features & settings

- Undo last shot: sends the file to the Recycle Bin, reverts the solo→member rename it caused, rewinds the numbering.
- Log (`%LOCALAPPDATA%\ScreenshotHelper\logs`): one file per UTC day, pruned to 30 days at start-up. Corrupt-settings backups: newest 3 kept.
- Settings are saved immediately; **Reset everything to defaults** (recent folders and window size are kept). A settings file missing
  some keys (written by an older version, or edited by hand) gets the defaults for those keys.
- Light/dark theme; app icon generated by `tools/generate-icon.cs`.
- `SCREENSHOTHELPER_HOME` redirects all app data to one folder.
- Every setting and its default: the [README's Settings table](../README.md#%EF%B8%8F-settings) (the user guide is their single source).

## 11. Roadmap

Planned work is listed in the [README's Roadmap](../README.md#-roadmap).

## 12. Architecture

```
src/ScreenshotHelper.Core/              net10.0          naming grammar, session engine + actor, collision policy, renumber planner
                                                         + journal, ledger, settings, sound synthesis, edge matting — no UI, no OS APIs
src/ScreenshotHelper.Platform.Windows/  net10.0-windows  hotkeys, capture (+ backdrop), Recycle Bin, Explorer reveal, clipboard,
                                                         sound playback, focus/overlay helpers, single instance
src/ScreenshotHelper.App/               net10.0-windows  Avalonia 12 MVVM: views, view-models, tray, toast, session coordinator
tests/                                  one xUnit v3 project per source project; Core also runs on Linux in CI
tools/                                  icon generator, window-capture helper (index in tools/README.md)
build/*.ps1                             build / publish scripts, also used by CI
.github/workflows/                      ci.yml, release.yml
```

A port to another OS adds one `Platform.<OS>` project implementing the Core interfaces (`IHotkeyService`, `IScreenCapture`,
`IRecycleBin`, `IFileRevealer`, `ISoundPlayer`, `IClipboardImage`, `IKeyboardLayout`) plus packaging; UI and Core are reused.
Background: [decision log, 2026-09-26](DECISIONS.md#2026-09-26-utc).

## 13. Distribution

- **`ci.yml`:** build + test on every push and PR (full suite on Windows, Core on Ubuntu).
- **`release.yml`:** on a `v*.*.*` tag, **only in a public repository** — tests, then self-contained, ReadyToRun-precompiled single-file exes for win-x64 and win-arm64, portable zips, `SHA256SUMS.txt`, a build-provenance attestation, and a GitHub Release with generated notes.
- Builds are unsigned; the README covers the SmartScreen prompt and how to verify a download.
