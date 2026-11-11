"""Convert a keypad layout into a Loupedeck/Logi Options+ .lp5 profile.

Reads v1 keypad-layout.yaml and v2 keys.yaml (the same contract plus a paths: block of
${name} variables, an optional color per key and an optional text_mode).

Usage: python yaml2lp5.py [layout.yaml] [out.lp5] [template.lp5]
       python yaml2lp5.py --slots [layout.yaml] [out_dir] [template.lp5]
       python yaml2lp5.py --check [layout.yaml]
Defaults: keypad-layout.yaml, <profile_name>.lp5, template.lp5 (all next to this script).
--slots writes slots.lp5, slots-actions.lp5 and slots-10pages.lp5 for the v2 LogiCoolJ plugin.
IDs are derived from profile_id (or profile_name) + page/button, so rebuilding keeps the same IDs.
Options+ assigns its own IDs on import, so a rebuilt file imports as a new profile.

The template .lp5 supplies $type strings, plugin lists and app metadata.
Its old buttons, macros, profile actions and icons are dropped.
Requires: pyyaml, pillow.
"""
import base64, copy, io, json, os, re, sys, uuid, zipfile
from datetime import datetime, timezone

import yaml
from PIL import Image, ImageDraw, ImageFont

HKL = "67699721"  # 0x04090409, US keyboard layout (from the template)
DICT_T = ("System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],"
          "[System.String, System.Private.CoreLib]], System.Private.CoreLib")
T_MACRO = "Loupedeck.Service.ApplicationProfileMacroCommand, LoupedeckService"
T_EDCMD = "Loupedeck.Service.MacroActionEditorCommand, LoupedeckService"
T_PCMD = "Loupedeck.Service.ApplicationProfileCommand, LoupedeckService"
T_WS = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutWorkspace7, LoupedeckService"
T_PAGE = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutPage7, LoupedeckService"
T_CTRL = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutControl7, LoupedeckService"
PLUGIN_NAME = "LogiCoolJ"
PLUGIN_NS = "Loupedeck.LogiCoolJPlugin"
SLOT_MODES = ("slots", "slots-actions", "slots-10pages")

MODS = {"ctrl": ("ControlOrCommand", "Ctrl"), "control": ("ControlOrCommand", "Ctrl"),
        "shift": ("Shift", "Shift"), "alt": ("AltOrOption", "Alt"),
        "win": ("Windows", "Win"), "windows": ("Windows", "Win")}
MOD_ORDER = ["ControlOrCommand", "Shift", "AltOrOption", "Windows"]
# Names confirmed in the template: KeyX, Return, Tab, Space, Period.
# The rest are best guesses at Loupedeck's key names.
KEYS = {"enter": "Return", "return": "Return", "tab": "Tab", "space": "Space", ".": "Period",
        "esc": "Escape", "escape": "Escape", "left": "ArrowLeft", "right": "ArrowRight",
        "up": "ArrowUp", "down": "ArrowDown", "`": "Backquote", "backspace": "Backspace",
        "delete": "Delete"}

PAGE_COLORS = ["#2D3A4A", "#1F4E8C", "#3C3C8C", "#1E5E3A", "#7A4A12",
               "#6B1F4E", "#4A5A1E", "#2A5A6A", "#5A2A2A", "#444444"]

STEP_TYPES = ("key", "text", "wait", "url", "open", "run", "go_to_page")
# Same list as SECRET_PATTERNS in logi-cool-j-builder.html (HANDOUT rule 4). The .lp5 is a plain zip.
SECRET_PATTERNS = [re.compile(p) for p in (
    r"\bsk-[a-zA-Z0-9_-]{16,}\b", r"\bgsk_[a-zA-Z0-9]{20,}\b", r"\bgithub_pat_[a-zA-Z0-9_]{20,}\b",
    r"\bhf_[a-zA-Z0-9]{20,}\b", r"\bAKIA[0-9A-Z]{16}\b", r"\bghp_[a-zA-Z0-9]{20,}\b",
    r"\bAIza[0-9A-Za-z_-]{30,}\b", r"\bxox[baprs]-[0-9a-zA-Z-]{10,}\b",
    r"(?i)password\s*[:=]\s*\S+", r"(?i)api[_-]?key\s*[:=]\s*\S+")]

seen_unknown = set()

PATH_VAR = re.compile(r"\$\{([^}]*)\}")
VAR_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
COLOR = re.compile(r"^#[0-9A-Fa-f]{6}$")
TEXT_MODES = ("type", "paste")
EXPANDED_STEPS = ("text", "url", "open", "run")


def resolve_paths(paths):
    """keys.yaml paths: block -> ({name: fully expanded value}, [problems]). Values may use ${other}."""
    if paths is None:
        return {}, []
    if not isinstance(paths, dict):
        return {}, ["paths must be a mapping of name: value."]
    errs, raw = [], {}
    for k, v in paths.items():
        if not isinstance(k, str) or not VAR_NAME.match(k):
            errs.append(f"paths: '{k}' is not a valid name (letters, digits, _).")
        elif not isinstance(v, str) or not v.strip():
            errs.append(f"paths: {k} needs a text value.")
        else:
            raw[k] = v
    done = {}

    def solve(name, stack):
        if name in done:
            return done[name]
        if name in stack:
            raise ValueError(f"paths: {' -> '.join(stack + [name])} refers to itself.")
        if name not in raw:
            raise KeyError(name)
        out = PATH_VAR.sub(lambda m: solve(m.group(1), stack + [name]), raw[name])
        done[name] = out
        return out

    for k in raw:
        try:
            solve(k, [])
        except ValueError as e:
            errs.append(str(e))
        except KeyError as e:
            errs.append(f"paths: {k} uses ${{{e.args[0]}}}, which is not defined.")
    return done, sorted(set(errs), key=errs.index)


def expand_value(value, resolved):
    return PATH_VAR.sub(lambda m: resolved[m.group(1)], value)


def expanded_layout(lay):
    """Copy of a checked layout with every ${name} in text/url/open/run steps replaced."""
    resolved, _ = resolve_paths(lay.get("paths"))
    out = copy.deepcopy(lay)
    out.pop("paths", None)
    for key, page in out.items():
        if not (key.startswith("page_") and isinstance(page, dict)):
            continue
        for b in page.get("buttons") or []:
            for s in b.get("steps") or []:
                (kind, val), = s.items()
                if kind in EXPANDED_STEPS and isinstance(val, str):
                    s[kind] = expand_value(val, resolved)
    return out


def check_layout(lay):
    """Every problem that would make a wrong or unsafe .lp5, as readable lines (empty = OK).
    Mirrors the builder's checks so a hand-edited YAML gets the same rules."""
    errs = []
    if not isinstance(lay, dict):
        return ["The file is not a YAML mapping."]
    resolved, path_errs = resolve_paths(lay.get("paths"))
    errs += path_errs
    for k, v in resolved.items():
        if any(rx.search(v) for rx in SECRET_PATTERNS):
            errs.append(f"paths: {k} looks like an API key or password; refused.")
    if lay.get("text_mode") is not None and lay.get("text_mode") not in TEXT_MODES:
        errs.append("text_mode must be type or paste.")
    try:
        n_pages = int(lay.get("profile_page_count", 10))
    except (TypeError, ValueError):
        return ["profile_page_count must be a whole number."]
    if not 1 <= n_pages <= 99:
        errs.append(f"profile_page_count {n_pages} is out of range 1..99.")
    for pi in range(1, n_pages + 1):
        src = lay.get(f"page_{pi}")
        if src is None:
            continue
        if not isinstance(src, dict):
            errs.append(f"page_{pi} must be a mapping with name and buttons.")
            continue
        seen_ids = set()
        for b in src.get("buttons") or []:
            where = f"Page {pi}"
            try:
                bid = int(b.get("id"))
            except (TypeError, ValueError, AttributeError):
                errs.append(f"{where}: a button has no valid id (0..8).")
                continue
            where = f"Page {pi} button {bid}"
            if not 0 <= bid <= 8:
                errs.append(f"{where}: id must be 0..8.")
            if bid in seen_ids:
                errs.append(f"{where}: id used twice on this page.")
            seen_ids.add(bid)
            if b.get("color") is not None and not COLOR.match(str(b.get("color"))):
                errs.append(f"{where}: color must look like #1F4E8C.")
            if b.get("text_mode") is not None and b.get("text_mode") not in TEXT_MODES:
                errs.append(f"{where}: text_mode must be type or paste.")
            steps = b.get("steps") or []
            if not isinstance(steps, list):
                errs.append(f"{where}: steps must be a list.")
                continue
            for si, s in enumerate(steps, 1):
                if not isinstance(s, dict) or len(s) != 1:
                    errs.append(f"{where} step {si}: needs exactly one of {', '.join(STEP_TYPES)}.")
                    continue
                (kind, val), = s.items()
                if kind not in STEP_TYPES:
                    errs.append(f"{where} step {si}: unknown step '{kind}'.")
                    continue
                if val is None or str(val).strip() == "":
                    errs.append(f"{where} step {si} ({kind}): value is empty.")
                    continue
                if kind in ("wait", "go_to_page"):
                    try:
                        num = int(str(val))
                    except ValueError:
                        errs.append(f"{where} step {si}: {kind} must be a whole number.")
                        continue
                    if kind == "wait" and num < 0:
                        errs.append(f"{where} step {si}: wait must be 0 or more.")
                    if kind == "go_to_page":
                        if not 1 <= num <= n_pages:
                            errs.append(f"{where}: go_to_page {num} is out of range 1..{n_pages}.")
                        if len(steps) > 1:
                            errs.append(f"{where}: go_to_page must be the button's only step.")
                if kind == "key":
                    combo = str(val)
                    parts = ["+"] if combo == "+" else [p.strip() for p in combo.split("+") if p.strip()]
                    bad = [p for p in parts[:-1] if p.lower() not in MODS]
                    if bad:
                        errs.append(f"{where} step {si}: unknown modifier '{'+'.join(bad)}' (use Ctrl, Shift, Alt, Win).")
                if kind in EXPANDED_STEPS:
                    for m in PATH_VAR.finditer(str(val)):
                        if m.group(1) not in resolved and not any(f"paths: {m.group(1)} " in e for e in path_errs):
                            errs.append(f"{where} step {si}: ${{{m.group(1)}}} is not in paths.")
                text = str(val)
                if kind in EXPANDED_STEPS and resolved:
                    text = PATH_VAR.sub(lambda m: resolved.get(m.group(1), m.group(0)), text)
                if any(rx.search(text) for rx in SECRET_PATTERNS):
                    # Never echo the value.
                    errs.append(f"{where} step {si}: looks like an API key or password; refused (the .lp5 is a plain zip).")
    return errs


_NS = uuid.NAMESPACE_URL
_seed = ["keypad"]


def gid(*parts):
    """Stable ID from profile name + parts; random if no parts are given."""
    if not parts:
        return uuid.uuid4().hex.upper()
    return uuid.uuid5(_NS, "/".join([_seed[0], *map(str, parts)])).hex.upper()


def key_string(combo):
    parts = [p.strip() for p in combo.split("+") if p.strip()] if combo != "+" else ["+"]
    mods, disp_mods, key = [], [], None
    for p in parts:
        m = MODS.get(p.lower())
        if m and p is not parts[-1]:
            mods.append(m[0]); disp_mods.append(m[1])
        else:
            key = p
    lk = key.lower()
    if len(key) == 1 and key.isalpha():
        name, disp = "Key" + key.upper(), key.upper()
    elif len(key) == 1 and key.isdigit():
        name, disp = "Digit" + key, key
    elif re.fullmatch(r"[Ff]\d{1,2}", key):
        name, disp = key.upper(), key.upper()
    elif lk in KEYS:
        name, disp = KEYS[lk], key
    else:
        name, disp = key, key  # media keys etc. passed through as written
        seen_unknown.add(key)
    if name in ("ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Backquote"):
        seen_unknown.add(key)
    mods.sort(key=MOD_ORDER.index)
    names = "+".join(mods + [name])
    display = "+".join(disp_mods + [disp if name not in ("Return", "Tab", "Space") else name])
    return f"{names}___{HKL}___{display}___"


def split_exe(target):
    """'wt.exe -d "C:\\x"' -> ('wt.exe', '-d "C:\\x"')"""
    m = re.match(r'^("?)(.+?\.exe)\1(?:\s+(.*))?$', target, re.I)
    return (m.group(2), m.group(3) or "") if m else (target, "")


def exec_app(target):
    path, args = split_exe(target)
    workdir = path.rsplit("\\", 1)[0] if "\\" in path and path.lower().endswith(".exe") else ""
    return f"$@Generic___@ExecuteApplication___{path}||{args}||{workdir}"


def is_ps1(target):
    return target.strip().strip('"').lower().endswith(".ps1")


def ps1_command(target):
    """A .ps1 always runs through pwsh (Windows would open it in Notepad)."""
    return f'pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "{target.strip().strip(chr(34))}"'


def is_uri(t):
    return re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]+:(?!\\)", t) is not None


def slot_action(slot_name, actions_form=False):
    """Profile action string matching Loupedeck.ActionString(pluginName, actionName, parameter).ToString()."""
    m = re.fullmatch(r"k([0-8])|p(\d\d)k([0-8])|t([0-8])", slot_name)
    if not m:
        raise ValueError(f"Invalid slot name: {slot_name}")
    if not actions_form:
        return f"${PLUGIN_NAME}___{PLUGIN_NS}.SlotCommand___{slot_name}"
    if m.group(1) is not None:
        cls = f"KeyK{m.group(1)}"
    elif m.group(2) is not None:
        cls = f"SlotP{int(m.group(2)):02d}K{m.group(3)}"
    else:
        cls = f"TestT{m.group(4)}"
    return f"${PLUGIN_NAME}___{PLUGIN_NS}.{cls}"


def _infer_mode(out_path, mode):
    if mode:
        return mode
    if out_path:
        base = os.path.basename(out_path).lower()
        if base == "slots.lp5":
            return "slots"
        if base == "slots-actions.lp5":
            return "slots-actions"
        if base == "slots-10pages.lp5":
            return "slots-10pages"
    return "native"


# DejaVu ships with most Linux installs; Windows has Segoe UI and Arial instead.
FONT_CANDIDATES = ("DejaVuSans-Bold.ttf", "segoeuib.ttf", "arialbd.ttf", "seguisb.ttf")
_font_cache = {}


def load_font(size):
    if size not in _font_cache:
        for name in FONT_CANDIDATES:
            try:
                _font_cache[size] = ImageFont.truetype(name, size)
                break
            except OSError:
                continue
        else:
            _font_cache[size] = ImageFont.load_default(size)
    return _font_cache[size]


def render_icon(label, color, size=192):
    img = Image.new("RGBA", (size, size), color)
    d = ImageDraw.Draw(img)
    text = label.strip() or " "
    for fs in range(44, 14, -2):
        font = load_font(fs)
        words, lines, cur = text.split(), [], ""
        for w in words:
            t = (cur + " " + w).strip()
            if d.textlength(t, font=font) <= size - 20 or not cur:
                cur = t
            else:
                lines.append(cur); cur = w
        lines.append(cur)
        if all(d.textlength(l, font=font) <= size - 16 for l in lines) and len(lines) * fs * 1.2 <= size - 16:
            break
    total = len(lines) * fs * 1.2
    y = (size - total) / 2
    for l in lines:
        d.text(((size - d.textlength(l, font=font)) / 2, y), l, font=font, fill="white")
        y += fs * 1.2
    buf = io.BytesIO(); img.save(buf, "PNG")
    return buf.getvalue()


def main(yaml_path=None, out_path=None, template=None, mode=None):
    here = os.path.dirname(os.path.abspath(__file__))
    j = lambda f: os.path.join(here, f)
    mode = _infer_mode(out_path, mode)
    if mode not in ("native", *SLOT_MODES):
        raise ValueError(f"Unknown mode: {mode}")
    v2_keys = os.path.join(os.path.dirname(here), "v2", "keys.yaml")
    default_yaml = v2_keys if (mode in SLOT_MODES and os.path.exists(v2_keys)) else j("keypad-layout.yaml")
    yaml_path = yaml_path or default_yaml
    template = template or j("template.lp5")
    seen_unknown.clear()
    with zipfile.ZipFile(template) as z:
        tpl = {n: z.read(n) for n in z.namelist()}
    prof = json.loads(tpl["ProfileInfo.json"].decode("utf-8-sig"))
    appinfo = json.loads(tpl["ApplicationInfo.json"].decode("utf-8-sig"))
    advinfo = json.loads(tpl["metadata/AdvancedInfo.json"].decode("utf-8-sig"))
    with open(yaml_path, encoding="utf-8") as f:
        lay = yaml.safe_load(f)
    problems = check_layout(lay)
    if problems:
        raise SystemExit("Not built; fix these first:\n  " + "\n  ".join(problems))
    lay = expanded_layout(lay)
    base_name = str(lay.get("profile_name") or "Keypad Layout")
    base_seed = str(lay.get("profile_id") or base_name)
    _seed[0] = base_seed if mode == "native" else f"{base_seed}:{mode}"
    if mode == "native":
        name = base_name
        default_file = re.sub(r'[<>:"/\\|?*]', "_", name) + ".lp5"
    else:
        suffix = {"slots": "Slots", "slots-actions": "Slots - Actions", "slots-10pages": "Slots - 10 Pages"}[mode]
        name = f"{base_name} ({suffix})"
        default_file = f"{mode}.lp5"
    out_path = out_path or j(default_file)

    n_pages = 1 if mode in ("slots", "slots-actions") else int(lay.get("profile_page_count", 10))
    ws_id = gid("ws")
    page_ids = [gid("page", i) for i in range(n_pages)]
    goto = lambda n: f"$@Generic___@ChangeTouchPage___System|{ws_id}|{page_ids[int(n) - 1]}"

    macros, pactions, images, pages, preview = [], [], {}, [], []
    for pi in range(n_pages):
        src = lay.get(f"page_{pi + 1}") or {"name": f"Page {pi + 1}", "buttons": []}
        by_id = {int(b["id"]): b for b in src.get("buttons") or []}
        controls = []
        for cid in range(9):
            b = by_id.get(cid) or {}
            steps = b.get("steps") or []
            label = str(b.get("label") or "")
            color = str(b.get("color") or PAGE_COLORS[pi % 10])
            paste = (b.get("text_mode") or lay.get("text_mode") or "type") == "paste"
            action = None
            target1 = steps[0].get("run") or steps[0].get("open") if len(steps) == 1 else None
            target1 = None if target1 is None else str(target1)
            if mode in ("slots", "slots-actions"):
                action = slot_action(f"k{cid}", actions_form=(mode == "slots-actions"))
            elif mode == "slots-10pages":
                if len(steps) == 1 and "go_to_page" in steps[0]:
                    action = goto(steps[0]["go_to_page"])
                else:
                    action = slot_action(f"p{pi + 1:02d}k{cid}", actions_form=False)
            elif len(steps) == 1 and "go_to_page" in steps[0]:
                action = goto(steps[0]["go_to_page"])
            elif target1 is not None and not is_ps1(target1) and (
                    "run" in steps[0] or (not split_exe(target1)[0].lower().endswith(".exe") and not is_uri(target1))):
                target = target1.replace("%USERPROFILE%", os.environ.get("USERPROFILE", "%USERPROFILE%"))
                action = f"$@Generic___@ProfileAction___{gid(pi, cid, 'file')}"
                pactions.append({
                    "$type": T_PCMD, "isCommand": True, "name": action,
                    "templateActionName": "$@Generic___@ShellExecute",
                    "actionParameters": {"$type": "Loupedeck.ActionEditorActionParameters, PluginApi",
                                         "parameters": {"$type": "Loupedeck.StringDictionaryNoCase, PluginApi",
                                                        "filePath": target}, "count": 1},
                    "displayName": label, "description": "Opens a specified file", "groupName": "",
                    "superGroupName": "@desktop", "isProfileAction": True, "isMultiState": False,
                    "isResetCommand": False, "adjustmentName": None, "states": None})
            elif steps:
                mid = gid(pi, cid, "macro")
                eds, acts = [], []
                for si, s in enumerate(steps):
                    if "key" in s:
                        n = gid(pi, cid, si); acts.append(n)
                        eds.append({"$type": T_EDCMD, "name": n, "templateName": "$@Generic___@KeyboardKey",
                                    "actionParameters": {"$type": DICT_T, "keyboardKey": key_string(str(s["key"]))}})
                    elif "text" in s:
                        n = gid(pi, cid, si); acts.append(n)
                        eds.append({"$type": T_EDCMD, "name": n, "templateName": "$@Generic___@SendText",
                                    "actionParameters": {"$type": DICT_T, "text": str(s["text"]),
                                                         "useClipboard": "true" if paste else "false"}})
                    elif "wait" in s:
                        acts.append(f"$@Generic___@Sleep___{int(s['wait'])}")
                    elif "url" in s:
                        acts.append(f"$@Generic___@OpenUrl___{s['url']}")
                    elif "open" in s or "run" in s:
                        t = (s.get("open") or s.get("run")).replace("%USERPROFILE%", os.environ.get("USERPROFILE", "%USERPROFILE%"))
                        if is_ps1(t):
                            t = ps1_command(t)
                        acts.append(f"$@Generic___@OpenUrl___{t}" if is_uri(t) else exec_app(t))
                    elif "go_to_page" in s:
                        acts.append(goto(s["go_to_page"]))
                    else:
                        raise ValueError(f"Unknown step on page {pi + 1} button {cid}: {s}")
                macros.append({"$type": T_MACRO, "isCommand": True, "name": mid, "displayName": label,
                               "description": "", "groupName": "", "superGroupName": "@macro",
                               "supportedOs": "All", "supportedModes": ["system"],
                               "showAsSingleAction": len(acts) == 1, "actionEditorCommands": eds,
                               "isMultiState": False, "actions": acts})
                action = f"$@Generic___@Macro___{mid}"
            if mode == "native" and action and "ChangeTouchPage" not in action:
                images[f"ActionImages/{action}.png"] = render_icon(label, color)
            controls.append({"$type": T_CTRL, "controlId": cid, "pressAction": action, "rotateAction": None})
            if pi == 0:
                preview.append({"controlId": cid, "actionName": action, "displayName": label, "description": None,
                                "image": base64.b64encode(render_icon(label, PAGE_COLORS[0], 116)).decode()})
        pages.append({"$type": T_PAGE, "name": page_ids[pi], "displayName": str(src.get("name") or f"Page {pi + 1}"),
                      "description": None, "controls": controls})

    mode_obj = prof["layout"]["layoutModes"][0]
    mode_obj["workspaces"] = [{"$type": T_WS, "name": ws_id, "displayName": "Workspace 1", "description": None,
                               "pressPages": pages, "rotatePages": []}]
    mode_obj["homeWorkspaceName"] = ws_id
    prof["layout"]["layoutModes"] = [mode_obj]

    if mode in SLOT_MODES:
        native_plugins = list(prof.get("additionalNativePluginNames") or [])
        if PLUGIN_NAME not in native_plugins:
            native_plugins.append(PLUGIN_NAME)
        prof["additionalNativePluginNames"] = native_plugins
        adv_plugins = list(advinfo.get("additionalPluginNames") or [])
        if PLUGIN_NAME not in adv_plugins:
            adv_plugins.append(PLUGIN_NAME)
        advinfo["additionalPluginNames"] = adv_plugins

    pid = gid("profile")
    desc = "Built from keypad-layout.yaml" if mode == "native" else f"Logi Cool J {mode} profile from {os.path.basename(yaml_path)}"
    prof.update({"name": pid, "displayName": name, "description": desc,
                 "lastModifiedTimeUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z"),
                 "actionImages90": None, "actionImages60": None, "wheelImages": None, "actionColors": None,
                 "macroCommands": macros, "macroAdjustments": [], "profileCommands": [],
                 "profileAdjustments": [], "profileActions": pactions, "packageName": gid("package")})
    appinfo["defaultProfileName"] = pid

    dump = lambda o: json.dumps(o, indent=4, ensure_ascii=False).replace("\n", "\r\n").encode("utf-8")
    pkg = tpl["metadata/LoupedeckPackage.yaml"].decode("utf-8-sig")
    pkg = re.sub(r"(?m)^name: .*$", f"name: {pid}", pkg)
    pkg = re.sub(r"(?m)^displayName: .*$", f"displayName: {name}", pkg)

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as o:
        o.writestr("ApplicationIcon.png", tpl["ApplicationIcon.png"])
        o.writestr("ApplicationInfo.json", dump(appinfo))
        o.writestr("ProfileInfo.json", dump(prof))
        for path, data in images.items():
            o.writestr(path, data)
        o.writestr("metadata/AdvancedInfo.json", tpl["metadata/AdvancedInfo.json"] if mode == "native" else dump(advinfo))
        o.writestr("metadata/LoupedeckPackage.yaml", pkg.encode("utf-8"))
        o.writestr("metadata/ProfilePreview.json", dump({"buttonPages": preview, "encoderPages": []}))

    print(f"Wrote {out_path}: {n_pages} pages, {len(macros)} macros, "
          f"{len(pactions)} file/script actions, {len(images)} icons")
    if seen_unknown:
        print("Unverified key names (test these):", ", ".join(sorted(seen_unknown)))
    return out_path


def build_slots(yaml_path=None, out_dir=None, template=None):
    """Build slots.lp5, slots-actions.lp5 and slots-10pages.lp5 in out_dir."""
    here = os.path.dirname(os.path.abspath(__file__))
    v2_dir = os.path.join(os.path.dirname(here), "v2")
    v2_keys = os.path.join(v2_dir, "keys.yaml")
    yaml_path = yaml_path or (v2_keys if os.path.exists(v2_keys) else os.path.join(here, "keypad-layout.yaml"))
    out_dir = out_dir or here
    os.makedirs(out_dir, exist_ok=True)
    return tuple(
        main(yaml_path, os.path.join(out_dir, f"{m}.lp5"), template, mode=m)
        for m in SLOT_MODES
    )


def check_only(yaml_path=None):
    here = os.path.dirname(os.path.abspath(__file__))
    yaml_path = yaml_path or os.path.join(here, "keypad-layout.yaml")
    with open(yaml_path, encoding="utf-8") as f:
        problems = check_layout(yaml.safe_load(f))
    if problems:
        raise SystemExit("Problems in " + yaml_path + ":\n  " + "\n  ".join(problems))
    print(f"{yaml_path}: OK")


if __name__ == "__main__":
    args = sys.argv[1:]
    if args[:1] == ["--check"]:
        check_only(*args[1:2])
    elif args[:1] == ["--slots"]:
        build_slots(*args[1:4])
    else:
        main(*args[:3])
