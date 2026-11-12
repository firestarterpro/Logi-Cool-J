# Independent Code Review — Logi Cool J (`be180f8..e02b563`)

## Summary

- **Overall state:** Both Track A (`logi-cool-j-builder.html` + `lp5-build\yaml2lp5.py`) and Track B (`v2\keys.yaml`, `LogiCoolJ.Core`, `LogiCoolJPlugin`, `keys-editor.html`, `Install-Plugin.ps1`, `device-test.yaml`) build cleanly and pass all 40 Python unit tests, 65 Playwright UI assertions, and 44 .NET xUnit tests, with action strings verified against `PluginApi.dll` 6.1.4.
- **Worst finding:** Three Medium findings — `v2\editor\keys-editor.html` can save a `keys.yaml` that fails plugin validation (and drops `icon`, `profile_id`, and `dials` on save), `LayoutHost` reads `keys.yaml` twice per watcher event without serializing concurrent reloads or initializing `_lastStamp` on `Reload()`, and missing `.ps1` targets in `open`/`run` steps launch `pwsh.exe` silently instead of firing `StepRunner.Failed`.
- **What was run:** `python -m unittest discover -s tests` (40/40 pass), `node tests\ui-smoke.js` (65/65 pass: 50 Track A + 15 Track B), `dotnet build v2\LogiCoolJ.sln -c Release` (0 warnings, 0 errors, plus both `SlotForm=Parameters` and `SlotForm=Actions`), and `dotnet test v2\LogiCoolJ.sln -c Release` (44/44 pass).

## Findings

| # | Severity | File:line | What is wrong | How to see it | Suggested fix |
|---|----------|-----------|---------------|---------------|---------------|
| 1 | Medium | `v2/editor/keys-editor.html:371-469`, `v2/editor/keys-editor.html:1088-1094` | `validateLayout()` does not check for empty step values, empty/undefined/cyclic `${name}` path variables, unknown `key` modifiers, `wait > 600000`, or secrets after `${name}` path expansion, and `#btn-export-download` allows downloading even when validation fails; additionally `loadFromYaml()` and `exportToYaml()` drop button `icon` fields as well as top-level `profile_id` and `dials`. | Load `v2\keys.yaml` in `keys-editor.html`, add an empty step or `key: "Cmd+C"` or `open: "${missing}"`, and click `Save keys.yaml`: `validateLayout()` returns `[]` and writes a file that `KeysLoader.Load` rejects; or save unmodified `keys.yaml` and observe `icon:` and `dials:` removed. | Preserve `icon`, `profile_id`, and `dials` in `loadFromYaml()`/`exportToYaml()`; add path resolution, empty-step, modifier, `wait` upper-bound, and expanded-secret checks to `validateLayout()`; and block `#btn-export-download` on validation errors. |
| 2 | Medium | `v2/src/LogiCoolJ.Core/LayoutHost.cs:48-178` | `ReloadIfChanged()` calls `ReadShared(this.FilePath)` at line 164 and then calls `Reload()` at line 177, which reads the file a second time; `Reload()` does not update `_lastStamp` (causing the first `PollOnce()` tick after `Reload()` + `Start()` to trigger a redundant reload); and concurrent `Reload()` / `ApplyText()` calls are not serialized across parse + state swap, while `_lastStamp` and `_lastText` are read outside `_gate`. | Call `host.Reload()` followed by `host.Start()` and inspect `_lastStamp` (still `(0, DateTime.MinValue)`), or trace `ReloadIfChanged()` reading `this.FilePath` twice and `ApplyText()` parsing outside `_gate`. | Update `_lastStamp` inside `Reload()`, pass the text read in `ReloadIfChanged()` directly to `ApplyText()` instead of re-reading the file, guard `_lastStamp`/`_lastText` and serialize reload swaps so an older read cannot overwrite a newer layout, and dispose prior timers/watcher if `Start()` is called again. |
| 3 | Medium | `v2/src/LogiCoolJPlugin/Runtime/PluginStepSink.cs:65-81`, `v2/src/LogiCoolJ.Core/Steps.cs:74-79` | When a `.ps1` script path in an `open:` or `run:` step does not exist on disk, `StepPlanner.PlanLaunch` wraps it as `Target = "pwsh.exe"` with `-File "<script.ps1>"`. `PluginStepSink.Launch` starts `pwsh.exe` via `Process.Start` (`UseShellExecute = true`), which succeeds because `pwsh.exe` exists; `pwsh.exe` briefly flashes a window and exits on the missing file without throwing to `StepRunner`, so `Runner.Failed` never fires. | Plan and launch `- run: "C:\\nonexistent\\missing.ps1"`: unlike a missing `.exe` or `.bat`, `Process.Start` starts `pwsh.exe` and returns normally instead of raising an error through `StepRunner.Failed`. | Validate that a rooted target path (and any rooted `.ps1` path passed to `pwsh.exe -File`) exists before starting the process, throwing `FileNotFoundException` when missing so `StepRunner.Failed` logs and notifies consistently. |
| 4 | Low | `lib/ai-assist.js:113-128` | `AiAssist.parseButton()` validates step types and integer `wait`/`go_to_page` values, but does not check `color` hex syntax (`#RRGGBB`), empty string step values, or `key` modifier names at parse time (they are caught later at save time). | Call `AiAssist.parseButton('label: "X"\ncolor: "blue"\nsteps:\n  - key: "Cmd+C"', jsyaml)` — it returns without throwing until layout validation runs. | Keep as note; layout-level validation catches bad colors, empty step values, and bad modifiers before saving. |
| 5 | Low | `lp5-build/yaml2lp5.py:184-192` | `check_layout()` in `yaml2lp5.py` rejects `wait < 0` but does not enforce the `600000` ms upper bound enforced by `KeysLoader.MaxWaitMs` in `v2\src\LogiCoolJ.Core\KeysLoader.cs:261`. | Run `check_layout` on a layout with `wait: 900000`: `yaml2lp5.py` accepts it while `KeysLoader.Load` rejects it. | Keep as note; both tools accept all normal wait durations (`0..600000` ms). |

## Checked and Fine

- **Plugin vs. Logi Actions SDK (`PluginApi.dll` 6.1.4):**
  - `v2\src\LogiCoolJPlugin\package\metadata\LoupedeckPackage.yaml` declares `type: plugin4`, `name: LogiCoolJ`, `pluginFileName: LogiCoolJPlugin.dll`, `pluginFolderWin: bin`, `supportedDevices: [MxCreativeKeypad, LoupedeckCtFamily]`, `pluginCapabilities: [HasNoApplication]`, and `minimumLoupedeckVersion: 6.0`.
  - `LogiCoolJPlugin` sets `UsesApplicationApiOnly => true` and `HasNoApplication => true`.
  - Reflection against `PluginApi.dll` 6.1.4 confirmed that `SlotForm=Parameters` produces `SlotCommand.Name == "Loupedeck.LogiCoolJPlugin.SlotCommand"` and `new ActionString("LogiCoolJ", sc.Name, "k0").ToString() == "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k0"`, matching `yaml2lp5.py` (`slots.lp5` and `slots-10pages.lp5`).
  - Reflection against `PluginApi.dll` 6.1.4 with `SlotForm=Actions` confirmed that `KeyK0.Name == "Loupedeck.LogiCoolJPlugin.KeyK0"`, `SlotP01K0.Name == "Loupedeck.LogiCoolJPlugin.SlotP01K0"`, and `TestT0.Name == "Loupedeck.LogiCoolJPlugin.TestT0"`, producing `$LogiCoolJ___Loupedeck.LogiCoolJPlugin.KeyK0` etc., matching `yaml2lp5.py` (`slots-actions.lp5`).
  - Every `KeyCombo.VirtualKeyNames` member and alias in `v2\src\LogiCoolJ.Core\KeyCombo.cs` exists in `Loupedeck.VirtualKeyCode`, and all four modifiers (`Control`, `Shift`, `Alt`, `Windows`) exist in `Loupedeck.ModifierKey`.
- **File watching & error recovery (`LayoutHost.cs`, `SlotPresenter.cs`, `LogiCoolJPlugin.cs`):**
  - Invalid YAML or rule violations keep `LayoutHost.Current` on the last valid `KeysLayout`, expose `LayoutProblem` with its 1-based line number on the affected key (or key 0 for file-level errors / errors on another page), dismiss the key error on first press without executing steps, and send a Windows balloon notification via `pwsh.exe`/`powershell.exe` `-EncodedCommand` (UTF-16LE base64, single-quote escaped).
- **Secrets rule (HANDOUT rule 4):**
  - Identical 10-pattern `SecretPatterns` lists are enforced in `logi-cool-j-builder.html`, `lp5-build\yaml2lp5.py` (`--check` and build), `v2\src\LogiCoolJ.Core\PathVariables.cs` (`SecretPatterns.LooksSecret` on both raw and `${name}`-expanded values), and `v2\editor\keys-editor.html`. Secret values are never echoed in error messages.
  - AI assist in `lib\ai-assist.js` and `logi-cool-j-builder.html` is disabled by default (`enabled: false`), never stores or sends API keys, and only calls the user-configured endpoint when enabled.
- **`yaml2lp5.py` & `device-test.lp5`:**
  - `.lp5` archives contain `ApplicationIcon.png`, `ApplicationInfo.json`, `ProfileInfo.json`, `ActionImages/*.png` (for native mode), `metadata/AdvancedInfo.json`, `metadata/LoupedeckPackage.yaml`, and `metadata/ProfilePreview.json`.
  - `--slots` writes `slots.lp5`, `slots-actions.lp5`, and `slots-10pages.lp5` with `"LogiCoolJ"` in `additionalNativePluginNames` and `additionalPluginNames`.
  - `v2\device-test.yaml` and `v2\device-test.lp5` cover all 7 step types across keys 0..8 (`open`, `key` arrows/backtick/media, `text` typed and pasted, `wait`, `url`, `run` with `${logi_cool_j}`, and `go_to_page`).
- **HTML editors & offline operation:**
  - Both `logi-cool-j-builder.html` and `v2\editor\keys-editor.html` load `lib\js-yaml.min.js` and `lib\ai-assist.js` from relative local paths with zero external network requests (verified by Playwright request interception).
- **`v2\Install-Plugin.ps1` (inspected by reading only):**
  - Uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference = "Stop"`, checks `Test-Path` on `keys.yaml`, runs `dotnet tool restore`, builds `LogiCoolJPlugin.csproj` in `Release` with `-p:SlotForm=$SlotForm`, `-p:KeysPath=$KeysFull`, and `-p:LinkToPluginService=true`, packs and verifies `LogiCoolJ.lplug4` with `logiplugintool`, refreshes the three slot `.lp5` profiles in `lp5-build\` and `v2\`, and runs `logiplugintool -- install` with a `Start-Process` fallback on non-zero exit.

## Could Not Verify

- **Live MX Creative Console hardware & Logi Options+ behavior:** Per hard limits, `v2\Install-Plugin.ps1`, `-p:LinkToPluginService=true`, `logiplugintool install`, and Logi Options+ profile imports were not run. Whether Options+ binds parameterized `SlotCommand` actions from `slots.lp5` (or requires `slots-actions.lp5` / `slots-10pages.lp5`), whether `Native.SendString` vs clipboard paste works best in every target window, and whether unconfirmed native key names (`ArrowLeft/Right/Up/Down`, `Backquote`, `MediaPlayPause`, `VolumeMute`) fire in native `.lp5` macros without the plugin can only be confirmed on the physical keypad.

## Commands Run and Results

| Command | Result |
|---------|--------|
| `git log --oneline be180f8..HEAD` | 9 commits (`be180f8` baseline to `e02b563`) |
| `git diff --stat be180f8..HEAD` | 36 files changed |
| `python -m unittest discover -s tests` | **40/40 passed** (`0.904s`) |
| `node tests\ui-smoke.js` | **65/65 passed** (`50/50` in `ui-smoke.js` + `15/15` in `test-keys-editor.js`) |
| `dotnet build v2\LogiCoolJ.sln -c Release` | **Succeeded** (`0 Warning(s)`, `0 Error(s)`) |
| `dotnet build v2\src\LogiCoolJPlugin\LogiCoolJPlugin.csproj -c Release -p:SlotForm=Actions -p:LinkToPluginService=false` | **Succeeded** (`0 Warning(s)`, `0 Error(s)`) |
| `dotnet test v2\LogiCoolJ.sln -c Release` | **44/44 passed** (`317 ms`) |
