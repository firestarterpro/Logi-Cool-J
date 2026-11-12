# Logi Cool J

Tools for the Logitech MX Creative Console keypad (Logi Options+, Loupedeck engine). Two tracks share one YAML contract (`lp5-build\HANDOUT-keypad-addon.md`):

- **Track A (works on its own):** lay out pages in a browser, build an `.lp5` profile, import it in Options+.
- **Track B (`v2\`):** a .NET 8 Logi Actions SDK plugin whose keys read their label, icon and steps from `v2\keys.yaml` and update a moment after you save the file, with an HTML editor and a device test page. Needs the plugin installed once.

The `.lp5` files are build output (git-ignored, `*.lp5`); rebuild them with the commands below. Everything has been checked on disk or in tests where possible; what only the device can settle is listed in `FINISH_REPORT.md` with an owner checklist.

## Track A: YAML to `.lp5`

```
logi-cool-j-builder.html  ->  lp5-build\keypad-layout.yaml  ->  yaml2lp5.py  ->  lp5-build\MX 10-Page Layout.lp5  ->  Options+ import
```

| Path | What it is |
|---|---|
| `logi-cool-j-builder.html` | Single-file builder (Keypad Layout Studio). Open it in Edge or Chrome. Works offline (js-yaml is loaded from `lib\`). Pages, 3x3 keys, steps, Copy/Move/Swap/Clear/Undo, library, profiles, AI assist. |
| `lp5-build\keypad-layout.yaml` | The live layout: 10 pages of 9 keys (1 AI Scripts, 2 Home, 3 DeepSeek, 4 Cursor, 5 Terminal, 6 Research, 7 Capture, 8 Files, 9 Windows, 10 Index). Git-ignored as personal, but committed once as the safety net. |
| `lp5-build\keypad-layout.v1-backup.yaml` | The layout from before the 2026-11-11 refresh. |
| `lp5-build\yaml2lp5.py` | Converter: YAML to `.lp5`. It checks the layout first and builds nothing if a rule fails. Needs Python 3 with `pyyaml` and `pillow`. |
| `lp5-build\template.lp5` | Empty template the converter builds on. Do not edit it. |
| `lp5-build\example-layout.yaml` | Small two-page example. |
| `lp5-build\HANDOUT-keypad-addon.md` | The YAML contract and rules, for people and LLMs. |
| `lib\js-yaml.min.js`, `lib\ai-assist.js` | Local YAML parser and the AI-assist code shared by the builder and the v2 editor. |

### Use it

1. Open `logi-cool-j-builder.html`. **Import** an existing `keypad-layout.yaml` or start fresh.
2. Edit pages and keys.
3. Click **Build .lp5**. Edge and Chrome ask once per session where to save; pick `lp5-build\keypad-layout.yaml` and later saves overwrite it. Other browsers download to Downloads.
4. Build the profile (reads `keypad-layout.yaml` next to the script):
   ```
   python lp5-build\yaml2lp5.py
   ```
   To build from a downloaded file instead:
   ```
   python lp5-build\yaml2lp5.py "%USERPROFILE%\Downloads\keypad-layout.yaml"
   ```
   To check a layout without building:
   ```
   python lp5-build\yaml2lp5.py --check lp5-build\keypad-layout.yaml
   ```
5. Import `lp5-build\MX 10-Page Layout.lp5` in Options+. Rename the old profile first (for example "MX v1") so you can go back.

**IDs and re-import:** the converter derives its IDs from `profile_id` (or `profile_name` when blank), so a rebuild produces the same IDs. Options+ does not keep them on import: it assigns its own, so importing again adds a second profile and does not replace the first. Delete or rename the old one by hand.

### The AI Scripts page (page 1)

Open DL, Open Clip, Copy Result and Recycle Run run `AiScript.ps1` from `C:\Templar Archives\stacks-attacks\tools\ai-scripts\` through `pwsh.exe -WindowStyle Hidden`. Run Script is Ctrl+Alt+Shift+R (the Cursor task), Ask Cursor is Ctrl+L, To Cursor opens Cursor. The script must exist locally: run `stacks-attacks-pull.bat` first if the folder is empty.

### AI assist (builder and v2 editor)

Build or copy a prompt, paste the model's YAML answer back, and **Apply to button** (undoable; it refuses secrets and unknown steps). Optionally set an endpoint: Ollama (`http://localhost:11434`, `/api/generate`, `/api/chat`) or any OpenAI-compatible URL such as `http://localhost:8000/v3/chat/completions`. It is off by default, has no API-key field, and the copy-prompt / paste-answer path always works. Apart from that explicit endpoint call the pages make no network requests.

The builder keeps its data in the browser's localStorage under `mx-console-v3`; use **JSON** for backups.

### Known gaps (Track A)

- Key names for arrows, `` ` ``, and media keys (`MediaPlayPause`, `VolumeMute`, and so on) are unconfirmed on the device. `v2\device-test.lp5` settles them (below).
- Dials are set by hand in Options+.
- Whether `.lp5` import works as expected on this console is for the owner's test.

## Track B: `v2\` (keys that update from a file)

```
v2\keys.yaml  ->  LogiCoolJ plugin (watches the file)  ->  slot keys on the console
v2\keys.yaml  ->  yaml2lp5.py --slots  ->  slots.lp5 (assigns the plugin's slots, imported once)
v2\editor\keys-editor.html  ->  edits and saves v2\keys.yaml
```

| Path | What it is |
|---|---|
| `v2\keys.yaml` | The v1 contract plus a `paths:` block of `${name}` variables (moving `C:\Templar Archives` is one edit), an optional `color` per key (`#RRGGBB`), and `text_mode: type` or `paste` (top level or per key). Made from `keypad-layout.yaml` by `v2\migrate_keys.py`. |
| `v2\src\LogiCoolJ.Core` | Plugin logic without the SDK: loader and checker (every problem reports line, page and key), path variables, step planning and running, file watcher, slot presenter. |
| `v2\src\LogiCoolJPlugin` | The plugin `LogiCoolJ`, targeting .NET 8 and Logi `PluginApi` 6.1.4. |
| `v2\tests\LogiCoolJ.Core.Tests` | xUnit tests. |
| `v2\Install-Plugin.ps1` | Builds, packs, verifies and installs the plugin into the Logi Plugin Service. |
| `v2\editor\keys-editor.html` | The editor. |
| `v2\device-test.yaml`, `v2\device-test.lp5` | One-page test layout, a key per step type. |
| `v2\slots.lp5`, `v2\slots-actions.lp5`, `v2\slots-10pages.lp5` | The three slot profiles (copies also in `lp5-build\`). |

### What the plugin does

- **Live file:** it watches `keys.yaml` (file events plus a 2 s poll), checks it, and swaps in the new layout only if it is valid. A bad file keeps the last good layout, shows `keys.yaml / line N / reason` on the failing key (or on key 1), and raises a Windows notification and an Options+ plugin status. Pressing the error key dismisses it.
- **Steps:** `key`, `text` (typed, or pasted through the clipboard with `text_mode: paste`), `wait`, `url`, `open` (program with arguments, folder or file), `run`, `go_to_page`. Paths and `%ENV%` variables are expanded, and `.ps1` always runs through `pwsh`.
- **Where it finds `keys.yaml`:** the `LOGICOOLJ_KEYS` environment variable, then `keys-path.txt` beside the DLL, then parent folders.

### The two slot forms

Only the device can settle which one works, so both are built:

- **Parameters form (default, primary):** one `SlotCommand` action with 108 parameters. `k0..k8` follow the plugin's current page (virtual paging: the plugin keeps the page number, a `go_to_page` step changes it, and the labels redraw). `p01k0..p10k8` are fixed slots for the fallback, and `t0..t8` read `device-test.yaml`. Also `StatusCommand` (shows OK or the error line; pressing reloads) and `PageCommand` (next, previous, page 1..10).
- **Actions form:** one class per slot (`KeyK0..KeyK8`). Build it with `-SlotForm Actions` (below).

### The three slot profiles

Each one is an `.lp5` that assigns plugin slots instead of macros:

| Profile | Device pages | Slots | Use |
|---|---|---|---|
| `slots.lp5` | 1 | `k0..k8` (SlotCommand) | Primary: virtual paging. |
| `slots-actions.lp5` | 1 | `k0..k8` (KeyK0..KeyK8) | For the Actions form. |
| `slots-10pages.lp5` | 10 | fixed `p01k0..p10k8` | Fallback if virtual paging fails; `go_to_page` keys become Options+ native page changes. |

They have distinct names (`MX 10-Page Layout (Slots)`, `(Slots - Actions)`, `(Slots - 10 Pages)`), so they can sit side by side in Options+. They carry no static key icons, so the plugin's live labels are not frozen. Rebuild them:

```
python lp5-build\yaml2lp5.py --slots v2\keys.yaml v2
```

### Install

`Install-Plugin.ps1` restores the local `LogiPluginTool`, builds Release, packs and verifies `v2\dist\LogiCoolJ.lplug4`, refreshes the three slot profiles, installs through `logiplugintool`, and falls back to opening the `.lplug4`. Run it only when you want the plugin installed (it changes your Logi Plugin Service); `.ps1` files run through `pwsh`:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File "C:\Templar Archives\My Apps\Logi.Cool.J\v2\Install-Plugin.ps1"
```

One class per slot instead:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File "C:\Templar Archives\My Apps\Logi.Cool.J\v2\Install-Plugin.ps1" -SlotForm Actions
```

A plain `dotnet build v2\LogiCoolJ.sln -c Release` installs nothing; linking into the Plugin Service happens only with `-p:LinkToPluginService=true`, which the installer sets. After installing, import `v2\slots.lp5` in Options+.

### The editor

Open `v2\editor\keys-editor.html` in Edge or Chrome: 3x3 grid with page tabs 1..10; click a key to edit it in one panel (steps can be added and reordered). Quick picks: App, Website, Shortcut (recorded by pressing the keys), Text, Script, Folder, Path variable, Go to page. A paths drawer edits the `${name}` variables, and the AI box is the same assist as the builder. **Save** writes straight to `v2\keys.yaml` (the browser asks once per session; other browsers download). Saving from the editor rewrites the file and drops its comments.

### Device test page

`v2\device-test.yaml` has nine keys on one page, one per step type, each with a comment saying what to expect: Notepad (press first), arrows, backtick, media keys, text plus wait, paste, URL, run a file, and `go_to_page`. Import `v2\device-test.lp5` for the native route; the plugin's `t0..t8` slots read the same file for the plugin route. A key that works in one route and not the other shows which key name to fix. Rebuild:

```
python lp5-build\yaml2lp5.py v2\device-test.yaml v2\device-test.lp5
```

### Known gaps (Track B)

- The C# tests pass (44 of 44, run 2026-11-12 after Smart App Control was turned off). Whether the Plugin Service may load the plugin DLLs is device-only.
- Everything involving the Logi Plugin Service and Options+ (install, import, live labels, paging, typing) is untested until the owner's device pass.
- No API exists to switch device pages from a plugin, which is why virtual paging is primary and the 10-page profile is the fallback.
- Out of scope: the dialpad, direct USB/HID control, other Logitech devices, automatic re-import.

## Test

Python (converter, migration, installer checks):

```
python -m unittest discover -s tests
```

Browser tests (the builder and the v2 editor; `PYTHON=python` overrides the interpreter):

```
node tests\ui-smoke.js
```

C# unit tests (44 cases; they need Smart App Control off, or a machine without enforcement, because it blocks the unsigned test DLL):

```
dotnet test v2\LogiCoolJ.sln -c Release
```

Playwright is pinned in `tests\package.json`; run `npm install` in `tests` if `node_modules` is missing.
