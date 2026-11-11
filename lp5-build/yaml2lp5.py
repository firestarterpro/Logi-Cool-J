"""Convert keypad-layout.yaml into a Loupedeck/Logi Options+ .lp5 profile.

Usage: python yaml2lp5.py [keypad-layout.yaml] [out.lp5] [template.lp5]
Defaults: keypad-layout.yaml, <profile_name>.lp5, template.lp5 (all next to this script).
IDs are derived from profile_name + page/button, so rebuilding keeps the same IDs.

The template .lp5 supplies $type strings, plugin lists and app metadata.
Its old buttons, macros, profile actions and icons are dropped.
Requires: pyyaml, pillow.
"""
import base64, io, json, os, re, sys, uuid, zipfile
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


def check_layout(lay):
    """Every problem that would make a wrong or unsafe .lp5, as readable lines (empty = OK).
    Mirrors the builder's checks so a hand-edited YAML gets the same rules."""
    errs = []
    if not isinstance(lay, dict):
        return ["The file is not a YAML mapping."]
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
                if any(rx.search(str(val)) for rx in SECRET_PATTERNS):
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


def is_uri(t):
    return re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]+:(?!\\)", t) is not None


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


def main(yaml_path=None, out_path=None, template=None):
    here = os.path.dirname(os.path.abspath(__file__))
    j = lambda f: os.path.join(here, f)
    yaml_path = yaml_path or j("keypad-layout.yaml")
    template = template or j("template.lp5")
    seen_unknown.clear()
    with zipfile.ZipFile(template) as z:
        tpl = {n: z.read(n) for n in z.namelist()}
    prof = json.loads(tpl["ProfileInfo.json"].decode("utf-8-sig"))
    appinfo = json.loads(tpl["ApplicationInfo.json"].decode("utf-8-sig"))
    with open(yaml_path, encoding="utf-8") as f:
        lay = yaml.safe_load(f)
    problems = check_layout(lay)
    if problems:
        raise SystemExit("Not built; fix these first:\n  " + "\n  ".join(problems))
    name = str(lay.get("profile_name") or "Keypad Layout")
    _seed[0] = str(lay.get("profile_id") or name)
    out_path = out_path or j(re.sub(r'[<>:"/\\|?*]', "_", name) + ".lp5")

    n_pages = int(lay.get("profile_page_count", 10))
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
            action = None
            if len(steps) == 1 and "go_to_page" in steps[0]:
                action = goto(steps[0]["go_to_page"])
            elif len(steps) == 1 and ("run" in steps[0] or ("open" in steps[0]
                                       and not split_exe(steps[0]["open"])[0].lower().endswith(".exe")
                                       and not is_uri(steps[0]["open"]))):
                target = steps[0].get("run") or steps[0].get("open")
                target = target.replace("%USERPROFILE%", os.environ.get("USERPROFILE", "%USERPROFILE%"))
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
                                    "actionParameters": {"$type": DICT_T, "text": str(s["text"]), "useClipboard": "false"}})
                    elif "wait" in s:
                        acts.append(f"$@Generic___@Sleep___{int(s['wait'])}")
                    elif "url" in s:
                        acts.append(f"$@Generic___@OpenUrl___{s['url']}")
                    elif "open" in s or "run" in s:
                        t = (s.get("open") or s.get("run")).replace("%USERPROFILE%", os.environ.get("USERPROFILE", "%USERPROFILE%"))
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
            if action and "ChangeTouchPage" not in action:
                images[f"ActionImages/{action}.png"] = render_icon(label, PAGE_COLORS[pi % 10])
            controls.append({"$type": T_CTRL, "controlId": cid, "pressAction": action, "rotateAction": None})
            if pi == 0:
                preview.append({"controlId": cid, "actionName": action, "displayName": label, "description": None,
                                "image": base64.b64encode(render_icon(label, PAGE_COLORS[0], 116)).decode()})
        pages.append({"$type": T_PAGE, "name": page_ids[pi], "displayName": str(src.get("name") or f"Page {pi + 1}"),
                      "description": None, "controls": controls})

    mode = prof["layout"]["layoutModes"][0]
    mode["workspaces"] = [{"$type": T_WS, "name": ws_id, "displayName": "Workspace 1", "description": None,
                           "pressPages": pages, "rotatePages": []}]
    mode["homeWorkspaceName"] = ws_id
    prof["layout"]["layoutModes"] = [mode]

    pid = gid("profile")
    prof.update({"name": pid, "displayName": name, "description": "Built from keypad-layout.yaml",
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
        o.writestr("metadata/AdvancedInfo.json", tpl["metadata/AdvancedInfo.json"])
        o.writestr("metadata/LoupedeckPackage.yaml", pkg.encode("utf-8"))
        o.writestr("metadata/ProfilePreview.json", dump({"buttonPages": preview, "encoderPages": []}))

    print(f"Wrote {out_path}: {n_pages} pages, {len(macros)} macros, "
          f"{len(pactions)} file/script actions, {len(images)} icons")
    if seen_unknown:
        print("Unverified key names (test these):", ", ".join(sorted(seen_unknown)))


if __name__ == "__main__":
    main(*sys.argv[1:4])
