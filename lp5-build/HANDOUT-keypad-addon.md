# Handout: Keypad Layout add-on (for the local LLM)

## Goal
Add a **Keypad** panel to the existing HTML interface. The user picks a page and a button, fills in label + steps, and clicks **Build**. The add-on edits `keypad-layout.yaml` and runs the converter, which writes a ready-to-import `.lp5`. No other code is needed.

## Files (folder `lp5-build/`, keep them together)
| File | Role | Touch? |
|---|---|---|
| `keypad-layout.yaml` | The only thing the add-on edits (source of truth) | ✅ read/write |
| `yaml2lp5.py` | Converter. `python yaml2lp5.py [layout.yaml] [out.lp5] [template.lp5]` (all optional) | ❌ run only |
| `template.lp5` | Clean template profile used by the converter | ❌ never edit |
| `<profile_name>.lp5` | Output, import in Options+ | generated |

Requirements: Python 3 with `pyyaml` and `pillow` (`pip install pyyaml pillow`).

## YAML contract
```yaml
profile_name: "MX 10-Page Layout"   # also the output filename
profile_id: "mx-main"               # OPTIONAL. IDs are seeded from this, else from profile_name. Keep whichever you use constant
profile_page_count: 10
page_1:
  name: "Home"
  buttons:
    - id: 0                 # 0..8, 0 = top-left, row by row (3x3)
      label: "DeepSeek"     # also rendered as the icon text (≤12 chars looks best)
      icon: "deepseek"      # informational only; icons are auto-generated from label
      steps:                # run in order; [] = empty button
        - url: "https://chat.deepseek.com"
        - wait: 1500
```
Pages are `page_1`…`page_N`. Missing buttons = empty. Missing pages = empty page.

### Step types (exactly one key per step)
| Step | Example | Becomes |
|---|---|---|
| `key` | `key: "Ctrl+Shift+T"` | keystroke (mods: Ctrl, Shift, Alt, Win) |
| `text` | `text: "git status"` | types text (no Enter; add `key: Return`) |
| `wait` | `wait: 500` | pause in ms |
| `url` | `url: "https://…"` | opens in default browser |
| `open` | `open: "C:\\…\\app.exe"` or `"wt.exe -d \"C:\\x\""` | launches program (args allowed after .exe) |
| `open` | `open: "C:\\Templar Archives"` (folder/file) | opens with Windows default |
| `run` | `run: "C:\\…\\script.bat"` | runs script/file |
| `go_to_page` | `go_to_page: 1` | jumps to page (must be the button's only step) |

Key names: letters, digits, F1–F24, Return, Tab, Space, Escape, Backspace, Delete, Left/Right/Up/Down, `` ` ``, media names like `MediaPlayPause`, `VolumeMute`. Confirmed working: everything in the current layout.

## Rules for the add-on
1. **Edit YAML only.** Load → modify one button object → save. Preserve all other pages/buttons and comments where possible (use a round-trip YAML library such as `ruamel.yaml`, or rewrite the whole file from the in-memory model).
2. **Backslashes:** in double-quoted YAML, Windows paths need `\\`. Easiest: let the YAML library quote values for you.
3. **Validate before saving:** `id` 0–8, page 1–N, each step has exactly one known key, `go_to_page` alone, `wait` an integer.
4. **No secrets:** refuse API keys/passwords in `text` steps (the profile is a plain zip).
5. **Build:** run `python yaml2lp5.py` in the folder; show its stdout (it lists unverified key names). Non-zero exit = show the error, don't overwrite anything.
6. Keep `profile_id` (or, if it's blank, `profile_name`) constant between builds so the IDs stay the same. Only write `profile_id` when the user set one.
7. Quote every string value (PyYAML reads plain `yes`/`no`/`on`/`off` as booleans). With js-yaml: `dump(obj, { forceQuotes: true, lineWidth: -1 })`.
8. Modifiers must be Ctrl, Shift, Alt or Win. The converter silently drops anything else (e.g. `Cmd`), so reject it.

## Suggested UI
- 3×3 grid per page, page tabs 1–N, page name editable.
- Click a cell → side form: label, step list (add/remove/reorder; dropdown for step type + value field).
- Buttons: **Save YAML**, **Build .lp5**, **Copy/Move/Swap button**, **Clear button**.
- Show "Home on 8" as a reminder, not enforced.

## Import (manual, user does this)
Options+ → keypad profiles → Import → pick the built `.lp5`. Dials are set by hand in Options+ (not in YAML).
