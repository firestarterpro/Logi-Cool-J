# Logi Cool J project rules

Shared stack: follow [firestarterpro/stacks-attacks `AGENTS.md`](https://github.com/firestarterpro/stacks-attacks/blob/main/AGENTS.md) (local clone `C:\Templar Archives\Cybernetics Core\stacks-attacks\AGENTS.md`). The project registry is STACKS.md → Projects. This file adds only what is specific to Logi Cool J.
Status: `FINISH_REPORT.md`

## What this repo is

Tools for the Logitech MX Creative Console keypad: a browser builder that writes `.lp5` profiles (Track A) and a .NET 8 Logi Actions SDK plugin driven by `v2/keys.yaml` (Track B). Local clone: `C:\Templar Archives\Forge\Logi.Cool.J`.

## Working here

- This repo is **public**. Keep personal data, private paths beyond the project folders, and secrets out of it.
- `v2/keys.yaml` and `lp5-build/keypad-layout.yaml` hold launch paths for other projects. When a project in STACKS.md moves or is renamed, update those paths here too.
- `.lp5` files are build output (git-ignored). Rebuild them; never commit them.
- What only the device can settle is in `FINISH_REPORT.md` with an owner checklist. Do not mark those items done without the owner's device check.
- Save the reasoning behind decisions as a dated handoff in `docs/handoffs/` (create it on first use); cross-project decisions go in stacks-attacks `handoffs/`.
- If this project moves, is renamed or changes its status file, update its row in stacks-attacks STACKS.md → Projects and run `python tools\project-sync-check.py` there.
