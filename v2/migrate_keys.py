"""Migrate a v1 keypad-layout.yaml into a v2 keys.yaml.

Usage: python migrate_keys.py [keypad-layout.yaml] [keys.yaml]
Defaults: ..\\lp5-build\\keypad-layout.yaml -> keys.yaml next to this script.

What it does:
  - keeps every page, key, label, icon and step exactly (same contract as v1);
  - adds a paths: block and replaces known folders in open/run/text steps with ${name},
    so moving C:\\Templar Archives (for example to C:\\Forge) is one edit to paths.templar;
  - adds text_mode: "type" (keys may say text_mode: "paste") and leaves color out
    (the plugin and converter use the page colour unless a key sets color: "#RRGGBB").
It refuses to write when the result does not expand back to the original steps.
Comments in the v1 file are not carried over.
"""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "lp5-build"))
import yaml  # noqa: E402
import yaml2lp5 as conv  # noqa: E402

# name -> value. Values may use ${other}; order matters only for the written file.
DEFAULT_PATHS = [
    ("templar", r"C:\Templar Archives"),
    ("ai_scripts", r"${templar}\stacks-attacks\tools\ai-scripts"),
    ("gateway", r"${templar}\Templar Nexus\dist\TheGateway\TheGateway.exe"),
    ("ai_control_center", r"${templar}\ai-control-center\app\dist\AI Control Center.exe"),
    ("ref_drafts", r"${templar}\Basketball Bro\Ref Text Confirm\pc-only-ref-drafts"),
    ("beer_league", r"${templar}\Basketball Bro\beer-league"),
    ("html_render", r"${templar}\My Apps\html-render"),
    ("logi_cool_j", r"${templar}\My Apps\Logi.Cool.J"),
    ("cursor", r"C:\Users\kcorn\AppData\Local\Programs\Cursor\Cursor.exe"),
    ("llm_explorer", r"C:\llm-explorer"),
    ("documents", r"C:\Users\kcorn\OneDrive\Documents"),
    ("options_plus", r"C:\Program Files\LogiOptionsPlus\logioptionsplus.exe"),
]
ALWAYS_KEEP = {"templar"}

HEADER = """# ============================================================================
# keys.yaml: logi-cool-j 2 layout (10 pages x 9 keys). The plugin reloads this
# file a moment after you save it; a bad file keeps the last good layout and
# shows the line number on a key and in a Windows notification.
# Contract: lp5-build\\HANDOUT-keypad-addon.md, plus
#   paths:      ${name} variables for text/url/open/run steps
#   color:      optional per key, "#RRGGBB"
#   text_mode:  "type" (default) or "paste" (clipboard), top level or per key
# Editor: v2\\editor\\keys-editor.html (saving there rewrites this file and drops comments).
# Made by migrate_keys.py from lp5-build\\keypad-layout.yaml.
# ============================================================================
"""


def q(v):
    """Double-quoted YAML scalar (JSON escapes are valid YAML)."""
    return json.dumps(v, ensure_ascii=False)


def substitute(value, resolved, used):
    """Replace the longest known folder values with ${name}, case-insensitively."""
    for name, full in sorted(resolved.items(), key=lambda kv: -len(kv[1])):
        low, lowfull, out, i = value.lower(), full.lower(), [], 0
        while True:
            j = low.find(lowfull, i)
            if j < 0:
                break
            end = j + len(full)
            # Only whole path pieces: the match must end the value or be followed by \ " or space.
            if end < len(value) and value[end] not in '\\" ':
                out.append(value[i:end]); i = end
                continue
            out.append(value[i:j] + "${" + name + "}"); i = end
            used.add(name)
        value = "".join(out) + value[i:]
        low = value.lower()
    return value


def needed(names):
    """The used names plus every name they refer to, in DEFAULT_PATHS order."""
    raw = dict(DEFAULT_PATHS)
    todo, keep = list(names), set()
    while todo:
        n = todo.pop()
        if n in keep:
            continue
        keep.add(n)
        todo += [m.group(1) for m in conv.PATH_VAR.finditer(raw[n])]
    return [(k, v) for k, v in DEFAULT_PATHS if k in keep]


def migrate(v1):
    problems = conv.check_layout(v1)
    if problems:
        raise SystemExit("The v1 layout has problems; fix them first:\n  " + "\n  ".join(problems))
    resolved, _ = conv.resolve_paths(dict(DEFAULT_PATHS))
    used = set(ALWAYS_KEEP)
    n_pages = int(v1.get("profile_page_count", 10))
    lines = [HEADER]
    lines.append(f"profile_name: {q(str(v1.get('profile_name') or 'Keypad Layout'))}")
    if v1.get("profile_id"):
        lines.append(f"profile_id: {q(str(v1['profile_id']))}")
    lines.append(f"profile_page_count: {n_pages}")
    lines.append('text_mode: "type"')
    body = []
    for pi in range(1, n_pages + 1):
        page = v1.get(f"page_{pi}") or {"name": f"Page {pi}", "buttons": []}
        body.append("")
        body.append(f"page_{pi}:")
        body.append(f"  name: {q(str(page.get('name') or f'Page {pi}'))}")
        body.append("  buttons:")
        for b in sorted(page.get("buttons") or [], key=lambda b: int(b["id"])):
            body.append(f"    - id: {int(b['id'])}")
            body.append(f"      label: {q(str(b.get('label') or ''))}")
            if b.get("icon"):
                body.append(f"      icon: {q(str(b['icon']))}")
            for opt in ("color", "text_mode"):
                if b.get(opt):
                    body.append(f"      {opt}: {q(str(b[opt]))}")
            steps = b.get("steps") or []
            if not steps:
                body.append("      steps: []")
                continue
            body.append("      steps:")
            for s in steps:
                (kind, val), = s.items()
                if kind in ("wait", "go_to_page"):
                    body.append(f"        - {kind}: {int(val)}")
                else:
                    v = str(val)
                    if kind in conv.EXPANDED_STEPS:
                        v = substitute(v, resolved, used)
                    body.append(f"        - {kind}: {q(v)}")
    lines.append("")
    lines.append("# Folders and programs used below. Change a value here and every key follows.")
    lines.append("paths:")
    for k, v in needed(used):
        lines.append(f"  {k}: {q(v)}")
    for extra in ("dials",):
        if extra in v1:
            body.append("")
            body.append("# Informational only (dials are set by hand in Options+).")
            body.append(yaml.safe_dump({extra: v1[extra]}, allow_unicode=True, default_flow_style=False).rstrip())
    return "\n".join(lines + body) + "\n"


def same_steps(a, b):
    """True when two layouts have the same pages, labels and steps (paths expanded)."""
    ea, eb = conv.expanded_layout(a), conv.expanded_layout(b)
    n = int(ea.get("profile_page_count", 10))
    if n != int(eb.get("profile_page_count", 10)):
        return False
    for pi in range(1, n + 1):
        pa, pb = ea.get(f"page_{pi}") or {}, eb.get(f"page_{pi}") or {}
        if (pa.get("name") or "") != (pb.get("name") or ""):
            return False
        ka = {int(x["id"]): (str(x.get("label") or ""), x.get("steps") or []) for x in pa.get("buttons") or []}
        kb = {int(x["id"]): (str(x.get("label") or ""), x.get("steps") or []) for x in pb.get("buttons") or []}
        if ka != kb:
            return False
    return True


def main(src=None, dst=None):
    src = src or os.path.join(HERE, "..", "lp5-build", "keypad-layout.yaml")
    dst = dst or os.path.join(HERE, "keys.yaml")
    with open(src, encoding="utf-8") as f:
        v1 = yaml.safe_load(f)
    text = migrate(v1)
    v2 = yaml.safe_load(text)
    problems = conv.check_layout(v2)
    if problems or not same_steps(v1, v2):
        raise SystemExit("Migration check failed; nothing written.\n  " + "\n  ".join(problems or ["steps differ after expanding paths"]))
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    used = len(v2.get("paths") or {})
    print(f"Wrote {dst}: {v2['profile_page_count']} pages, {used} path variables.")


if __name__ == "__main__":
    main(*sys.argv[1:3])
