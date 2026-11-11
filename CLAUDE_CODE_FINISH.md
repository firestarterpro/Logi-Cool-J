# logi-cool-j: finish brief for Claude Code

Written 2026-11-11 by Claude (chat) from the files in this folder and the v2 spec agreed on 2026-11-07. Run it start to finish in one or more sessions. The owner tests on the console afterward.

## Where things stand
- Folder: `C:\Templar Archives\My Apps\Logi.Cool.J`. No git repo yet.
- v1 works as a pipeline: `logi-cool-j-builder.html` (Keypad Layout Studio) writes `lp5-build\keypad-layout.yaml`, `lp5-build\yaml2lp5.py` turns it into a `.lp5`, and the owner imports that in Logi Options+. Read `README.md` and `lp5-build\HANDOUT-keypad-addon.md` (the YAML contract) first.
- A 10-page, 9-key layout exists. Whether it was ever imported on the MX Creative Console is not confirmed.
- You cannot see or press the console and you cannot click in Options+. So build both tracks below to the point where the owner's first test is one import and a few key presses, and where a failed check already has its fallback built.

## Operating rules
1. Work alone. Never ask the owner a question and never wait for approval. At a choice, take the option that best fits the existing docs and code, log it under Decisions in the report, and keep going.
2. Safety net first: `git init`, commit the current state as "baseline v1". Commit after every unit. No remote, no push.
3. Stay inside this folder. Other paths named here are read-only. Do not install anything into Options+ or the Logi Plugin Service, and do not import profiles: the owner does that.
4. Never leave it broken. Every commit passes the tests that exist at that point. If a unit will not come together, revert it, log it, move on.
5. Say in the report what you actually ran versus only wrote, and which behaviors can only be confirmed on the device.
6. No secrets in any layout: the checker refuses API keys and passwords (HANDOUT rule 4).
7. Allowed new dependencies: the Logi Actions SDK and its `logiplugintool`, a YAML library for .NET, xUnit, and the existing Python and Playwright needs. Check Logitech's current SDK documentation online before writing plugin code; do not rely on memory for its API.
8. Owner conventions: PowerShell 7 (`pwsh`); scripts do the job when run (no dry-run or `$Apply` switch); doc commands on one line; `.ps1` files always run through `pwsh` and never open in Notepad. Read `C:\Templar Archives\stacks-attacks\INSTRUCTIVE_STACK.md` if it exists and follow it where it does not conflict with rule 1.
9. If budget runs low: finish the current unit, commit, write the report, stop. A later session resumes at the first unfinished unit in `FINISH_REPORT.md`.

## Track A: make today's profile right (works without the plugin)
**A1 Layout refresh.** Copy `keypad-layout.yaml` to `keypad-layout.v1-backup.yaml`, then:
- Fix the stale header comment (it still mentions `build-json.ps1`) and the stale keys `logi scripts` and AIControl.
- Check that every `open` and `run` path exists on this PC; fix or flag each miss.
- Make page 1 the AI Scripts page: Run script, Open script first, Copy results, Recycle last run, plus Open download, Open clipboard and Ask Cursor. Take the exact commands from `C:\Templar Archives\stacks-attacks\tools\ai-scripts\` (`AiScript.ps1` and the Cursor task files).
- Keep 10 pages of 9 keys. To make room, fold the page with the fewest still-useful keys into Home and Index and list what moved.
- Add keys for The Gateway (`open` on `C:\Templar Archives\Templar Nexus\dist\TheGateway\TheGateway.exe` directly, because Options+ will not launch it from a saved shortcut), AI Control Center, Ref Drafts and Claude.

**A2 Builder fixes.** Load js-yaml from the local copy so the page works offline. Finish the half-built AI assist: endpoint is a setting that accepts Ollama or an OpenAI-compatible URL (the owner's local server is `http://localhost:8000/v3/chat/completions`), off by default, with copy-prompt / paste-answer as the path that always works.

**A3 Build and test.** Run `yaml2lp5.py`, run `tests\ui-smoke.js`, add converter tests for every step type. Leave the finished `.lp5` in `lp5-build\`.

## Track B: logi-cool-j 2 (keys that update live from a file)
Build in a new `v2\` subfolder. Goal: change any key by editing one file or clicking it in an editor; the console shows the change within seconds, with no Options+ window and no re-import.

**B1 `keys.yaml`.** Same contract as today plus a `paths:` block of variables (so the planned move to `C:\Forge` is one edit) and an optional `color` per key. Write a migration from `keypad-layout.yaml`.

**B2 Plugin (C# .NET 8, Logi Actions SDK).**
- Slot actions for 10 pages of 9 keys. Each slot reads its label, icon and steps from `keys.yaml`. Build whichever form the SDK supports best (one action with page/key parameters, or 90 actions) and keep the other reachable by a build switch, because only the device can settle it.
- Watch the file. Validate, then swap in the new layout only if valid. A bad file keeps the last good layout and shows the error with its line number on a key and in a Windows notification.
- Step runner: `key`, `text` (typed, with clipboard paste as an option), `wait`, `url`, `open` (program with arguments, folder or file), `run`, `go_to_page`.
- Unit tests for the loader, checker and step planning.

**B3 Slots profile and install script.** Extend the converter to generate `slots.lp5`, which assigns every slot. Write `Install-Plugin.ps1` (one run installs the plugin). Do not run it.

**B4 Editor.** One HTML page: 3x3 grid with page tabs; click a key for a single panel; quick picks (App, Website, Shortcut recorded by pressing keys, Text, Script, Folder, Project from `paths`); an AI box using the same assist as A2; saves straight to `keys.yaml` (the browser asks once per session). Playwright test for it.

**B5 Device test page.** One page with a key per step type, including the unconfirmed arrow, backtick and media keys.

Out of scope: the dialpad, direct USB/HID control, other Logitech devices, automatic re-import.

## Finish
- Have a fresh subagent review the full diff, especially the plugin; fix what it finds.
- Update `README.md` for both tracks.
- Write `FINISH_REPORT.md`: changes per unit, verified versus device-only, decisions, known issues, how to roll back, and an owner test checklist of at most 15 steps. Start with Track A (import the `.lp5`, press the AI Scripts keys), then Track B as yes/no checks, each naming the fallback already built: (1) does the imported `slots.lp5` assign the plugin slots, (2) do labels change live when `keys.yaml` is saved, (3) do slots survive switching apps, (4) does typed text work in the browser and in Cursor, (5) can a key switch pages.
