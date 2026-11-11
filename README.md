# Logi Cool J

Lay out a Logitech MX Creative Console keypad (Logi Options+, Loupedeck engine) in a browser, then build a ready-to-import `.lp5` profile.

```
logi-cool-j-builder.html  →  keypad-layout.yaml  →  python yaml2lp5.py  →  <profile_name>.lp5  →  Options+ import
```

## Files

| Path | What it is |
|---|---|
| `logi-cool-j-builder.html` | Single-file builder. Open it in Edge or Chrome. Pages, 3×3 buttons, steps, Copy/Move/Swap/Clear/Undo, library, profiles, prompts for an LLM. |
| `lp5-build/yaml2lp5.py` | Converter: YAML → `.lp5`. Needs Python 3 with `pip install pyyaml pillow`. |
| `lp5-build/template.lp5` | Empty template profile that the converter builds on. Don't edit it. |
| `lp5-build/example-layout.yaml` | Small two-page example. |
| `lp5-build/HANDOUT-keypad-addon.md` | YAML contract and rules (for people and LLMs). |
| `tests/ui-smoke.js` | Playwright smoke test: drives the builder, exports YAML, runs the converter. |

## Use it

1. Open `logi-cool-j-builder.html`. Use **Import** with an existing `keypad-layout.yaml` or start fresh.
2. Edit pages and buttons in **Keypad Layout Studio**.
3. Click **⚙ Build .lp5**. Edge and Chrome ask once per session where to save. Pick `lp5-build\keypad-layout.yaml` and later saves overwrite it. Other browsers download to Downloads.
4. Run:
   ```
   cd lp5-build
   python yaml2lp5.py                      # reads keypad-layout.yaml next to the script
   python yaml2lp5.py "%USERPROFILE%\Downloads\keypad-layout.yaml"   # or point at the download
   ```
5. Import `<profile_name>.lp5` in Options+.

**Stable IDs:** the converter derives every ID from `profile_id`, or from `profile_name` when `profile_id` is blank. Keep that value the same and each rebuild targets the same profile. Change it and Options+ sees a new profile.

The page stores data in the browser's localStorage under `mx-console-v3`. Use **⬇ JSON** for backups. The page makes no network calls except the explicit **Ask Local Ollama** button and loading js-yaml from jsDelivr.

## Test

```
cd tests
npm i playwright   # if not installed
node ui-smoke.js   # PYTHON=python to override the interpreter
```

## Known gaps

- Key names for arrows, `` ` ``, and media keys (`MediaPlayPause`, `VolumeMute`, …) are guesses. Test them on the device.
- Dials are set by hand in Options+.
- Options+ re-import behavior (does it replace a profile with the same ID?) hasn't been confirmed yet.
