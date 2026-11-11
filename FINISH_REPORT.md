# logi-cool-j: finish report (partial, stopped on budget)

Written 2026-11-11 by Claude Code (Opus 5.5), working from `CLAUDE_CODE_FINISH.md`.
**Status: Track A done. Track B: B1 done; B2 only scaffolded (resume there); B3, B4, B5 and the Finish section not started.**
Stopped under operating rule 9 (budget). The next session resumes at **B2** below.

## Commits (git log, no remote)
| Commit | Unit |
|---|---|
| `be180f8` | baseline v1 |
| `9ad16d9` | Make the v1 test and converter run on Windows |
| `4e9a8b3` | A1: refresh the 10-page layout |
| `86e41ef` | A2: builder works offline; finish the AI assist |
| `e164cc3` | A3: converter checks the layout first; tests for every step type |
| `7c6f805` | B1: keys.yaml with paths variables, and the migration |
| (last) | B2 scaffold only: local LogiPluginTool manifest, `v2\Directory.Build.props`, Core `.csproj` |

## Changes per unit
- **Windows fixes (before A1).** The converter asked Pillow for `DejaVuSans-Bold.ttf`, which Windows lacks, so it could not build any `.lp5` on this PC. It now falls back to Segoe UI Bold / Arial Bold / Pillow's font. `tests\ui-smoke.js` defaults to `python` on Windows (`python3` is a Store stub), pins Playwright 1.64.1 (`tests\package.json`; matches the browsers LLM Explorer already cached), exits 1 on any FAIL.
- **A1 layout** (`lp5-build\keypad-layout.yaml`; v1 kept as `keypad-layout.v1-backup.yaml`). Pages: 1 AI Scripts, 2 Home, 3 DeepSeek, 4 Cursor, 5 Terminal, 6 Research, 7 Capture, 8 Files, 9 Windows, 10 Index. Every page has Home on 8.
  - AI Scripts: Open DL, Open Clip, Copy Result, Recycle Run run `AiScript.ps1` directly through `pwsh.exe -WindowStyle Hidden` (no PowerToys row needed); Run Script = Ctrl+Alt+Shift+R (Cursor task); Ask Cursor = Ctrl+L; To Cursor opens Cursor.exe ("open script first"); Index; Home.
  - Home: The Gateway (`open` on `...\Templar Nexus\dist\TheGateway\TheGateway.exe`), Claude (Store app via `explorer.exe shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude`), AI Control Center (`app\dist\AI Control Center.exe`), Ref Drafts (same command as its desktop shortcut: electron.exe + app folder), DeepSeek, CursorCline, PlayPause, AI Scripts, Index.
  - Folded page: Media (fewest working keys; `spotify:` has no handler on this PC). Play/Pause → Home 6, Mute → Index 7; Prev, Next, Spotify dropped.
  - Also moved/dropped: old Home's Perplexity (still Research 0), Browser, Terminal (wt) and Files (Win+E typing) dropped; Capture/Windows jumps moved to Index; Index's Options+ and Sync Console moved to Files 7 and 2; Index's placeholder "Shortcuts" dropped. Stale `logi scripts` key → Logi.Cool.J folder; stale AIControl folder key removed (the app key is on Home). Header comment fixed.
  - v1 had Files and Media unreachable from any page; every page is reachable now.
- **A2 builder.** js-yaml loads from `lib\js-yaml.min.js` (no CDN). New shared `lib\ai-assist.js`: Build/Copy prompt → paste answer → **Apply to button** (undoable; refuses secrets and unknown steps). Optional endpoint, **off by default**, takes Ollama (`/api/generate`, `/api/chat`, bare `:11434`) or any OpenAI-compatible `/chat/completions` URL (e.g. `http://localhost:8000/v3/chat/completions`); failures leave the prompt ready to copy. No API keys stored.
- **A3 converter + tests.** `yaml2lp5.py` checks first and builds nothing on: unknown/double steps, go_to_page out of range or not alone, non-integer wait, unknown modifier (was silently dropped), secrets (never echoed). `tests\test_yaml2lp5.py` covers every step type. Built `lp5-build\MX 10-Page Layout.lp5` (10 pages, 63 macros, 8 file actions, 71 icons; git-ignored).
- **B1.** `v2\keys.yaml` = v1 contract + `paths:` (`${name}`; moving C:\Templar Archives is one edit to `paths.templar`) + optional `color` per key + `text_mode: type|paste` (top level or per key; the clipboard option the brief asks for). `v2\migrate_keys.py` writes it from `keypad-layout.yaml` and refuses to write unless it expands back to identical steps. The converter reads keys.yaml too (paths, color, paste → `useClipboard`, `--check`), and always runs `.ps1` through pwsh.

## Verified (ran) vs device-only
Ran and passing at the last commit: `python -m unittest discover -s tests` (37 tests), `node tests\ui-smoke.js` (50/50), converter on the real layout, `migrate_keys.py`, `yaml2lp5.py --check` on both layouts, every open/run path checked on disk.
Device-only (not testable here): whether Options+ accepts the imported `.lp5`; pwsh-with-arguments keys; arrow, backtick and media key names; Claude/Ref Drafts launch commands; endpoint CORS with a real local server.

## Decisions
1. "Open script first" read as a note on Run Script; added a To Cursor key so Run lands in Cursor.
2. AI Scripts keys call `AiScript.ps1` through pwsh rather than PowerToys shortcuts (no setup dependency; brief says take the exact commands). Fallback in the YAML comment: switch to Ctrl+Alt+Shift+D/V/C/X.
3. `keypad-layout.yaml` is git-ignored as personal; force-added so the safety net covers it.
4. AI endpoint: local servers only, no API key field.
5. Converter refuses bad layouts instead of passing them through (same rules as the builder; secret list shared and tested).
6. **Plugin targets .NET 8 against `PluginApi.dll` 6.1.4** from the official `LogiPluginTool` 6.1.4.22672 package (local tool in `v2\.config\dotnet-tools.json`). Reason: LogiPluginTool 6.4.2 and the `LogiPluginApi` NuGet are .NET 10 only, the installed service's PluginApi 6.4.2 is net10, and only the .NET 8 SDK is installed. The installed service already runs net8 plugins (ClaudeDesktop, LogiSmartActions, WindowsPowerToys).

## Known issues
- `C:\Templar Archives\stacks-attacks\tools\ai-scripts\AiScript.ps1` is on GitHub main (`adc26cf`) but **the local clone has not pulled it**: run `stacks-attacks-pull.bat` before testing the AI Scripts keys.
- The ai-scripts README's PowerToys rows use `C:\Program Files\PowerShell\7\pwsh.exe`, which does not exist here (pwsh is the Store build, `pwsh.exe` on PATH). Not edited (read-only folder).
- **Options+ assigns its own IDs on import** (the stored "MX 10-Page Layout" from 2026-11-07 has different profile, workspace and macro IDs than the converter makes), so a re-import adds a second profile rather than replacing it. README's "stable IDs" claim needs correcting.
- The Gateway exe was briefly missing during this session (The Gateway session republishing); the key keeps the exact path as instructed. It existed at the last path check.
- Ref Drafts key uses the dev electron.exe; switch to its portable build's exe once that session ships one.

## Rollback
`git log` → `git checkout be180f8 -- <file>` for any file, or `git reset --hard be180f8` for everything (asks nothing; owner's call). v1 layout is also in `lp5-build\keypad-layout.v1-backup.yaml`. In Options+, keep the old "MX 10-Page Layout" profile until the new import works (rename it, e.g. "MX v1").

## Resume here: B2 onward (research already done)
SDK facts checked 2026-11-11 (docs `llms-full.txt`, reflection on PluginApi 6.1.4, installed service 6.4.2.3414):
- Do **not** keep the generated csproj's PostBuild: it writes `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\<Name>.link` and sends `loupedeck:plugin/<Name>/reload` (= installing). Gate both behind a property only `Install-Plugin.ps1` sets. Reference PluginApi with `Private=false`; `Directory.Build.props` already defines `$(PluginApiDir)` (run `dotnet tool restore` in `v2` first).
- API: `PluginDynamicCommand` + `AddParameter(name, displayName, group)`; `RunCommand`, `GetCommandDisplayName`, `GetCommandImage` (`BitmapBuilder(size)`, `Clear(BitmapColor)`, `DrawText(text)`); `ActionImageChanged(param)`; keys via `Plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode, ModifierKey)` (parse enum names at runtime); typing `Plugin.NativeMethods.SendString`; clipboard `ClipboardSetText/GetText`; status `OnPluginStatusChanged(PluginStatus.Warning, msg)`; settings `Try/SetPluginSetting`. Digits are `Key0..Key9`, backtick `Oem3`, `MediaPrevTrack`.
- No API to switch device pages → design: **virtual paging** (9 "follow the page" slots k0–k8; plugin keeps the current page) as primary; fixed slots p01k0–p10k8 on 10 device pages with native `ChangeTouchPage` jumps as the fallback; t0–t8 for the device-test page. Build switch `-p:SlotForm=Actions` for one class per slot.
- Profile action string (from `ActionString.ToString`): `$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k0`. Plugin name `LogiCoolJ`, class `LogiCoolJPlugin`, namespace `Loupedeck.LogiCoolJPlugin`. `supportedDevices`: installed plugins use `MxCreativeKeypad` / `LogitechCreativeFamily`; verify with `logiplugintool verify`.
- Windows notification: reuse AiScript's NotifyIcon balloon via hidden pwsh.

## Owner test checklist (Track A now; Track B checks come with B2–B5)
1. Run `stacks-attacks-pull.bat` so `tools\ai-scripts\AiScript.ps1` exists.
2. In Options+, rename the current "MX 10-Page Layout" to "MX v1" (rollback).
3. Import `lp5-build\MX 10-Page Layout.lp5`. Does page 1 show AI Scripts? (No → keep MX v1 and report.)
4. Download any small `.ps1`, press **Open DL**: does it open in Cursor? (No → change the key to Ctrl+Alt+Shift+D after the PowerToys setup in the ai-scripts README, with `pwsh.exe` as the program.)
5. With the script in front in Cursor, press **Run Script**, then **Copy Result** and paste somewhere; then **Recycle Run**.
6. Press **Open Clip** with code on the clipboard, then **Ask Cursor**.
7. Home page: press Gateway, Claude, AI Control, Ref Drafts. Note any that do not open.
8. Windows page arrows, Cursor page Terminal (backtick), Home PlayPause, Index Mute: note which do nothing (unconfirmed key names).
