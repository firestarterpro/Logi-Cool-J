"""keys.yaml (v2) tests: paths variables, color, text_mode, .ps1 handling and the migration.

Run from the project folder:  python -m unittest discover -s tests -v
"""
import contextlib, io, json, os, shutil, sys, tempfile, unittest, zipfile

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "lp5-build"))
sys.path.insert(0, os.path.join(ROOT, "v2"))
import yaml2lp5 as c  # noqa: E402
import migrate_keys as mig  # noqa: E402


def build_text(text):
    d = tempfile.mkdtemp(prefix="lcj-v2-")
    try:
        src, out = os.path.join(d, "k.yaml"), os.path.join(d, "o.lp5")
        with open(src, "w", encoding="utf-8") as f:
            f.write(text)
        with contextlib.redirect_stdout(io.StringIO()):
            c.main(src, out)
        with zipfile.ZipFile(out) as z:
            return json.loads(z.read("ProfileInfo.json").decode("utf-8-sig")), z.namelist()
    finally:
        shutil.rmtree(d, ignore_errors=True)


KEYS = '''profile_name: "T"
profile_page_count: 1
text_mode: "type"
paths:
  root: "C:\\\\Forge"
  tools: "${root}\\\\tools"
page_1:
  name: "P"
  buttons:
    - id: 0
      label: "Run"
      color: "#112233"
      steps:
        - run: "${tools}\\\\go.ps1"
    - id: 1
      label: "Paste"
      text_mode: "paste"
      steps:
        - text: "hello ${root}"
    - id: 2
      label: "Folder"
      steps:
        - open: "${tools}"
'''


class Paths(unittest.TestCase):
    def test_resolve_chain(self):
        r, errs = c.resolve_paths({"a": "C:\\A", "b": "${a}\\B", "c": "${b}\\C"})
        self.assertEqual(errs, [])
        self.assertEqual(r["c"], "C:\\A\\B\\C")

    def test_cycle_unknown_and_bad_name(self):
        _, errs = c.resolve_paths({"a": "${b}", "b": "${a}"})
        self.assertTrue(any("refers to itself" in e for e in errs), errs)
        _, errs = c.resolve_paths({"a": "${nope}\\x"})
        self.assertTrue(any("${nope}" in e for e in errs), errs)
        _, errs = c.resolve_paths({"bad name": "x"})
        self.assertTrue(any("not a valid name" in e for e in errs), errs)

    def test_step_using_unknown_variable(self):
        lay = yaml.safe_load(KEYS.replace("${tools}\\\\go.ps1", "${missing}\\\\go.ps1"))
        self.assertTrue(any("${missing} is not in paths" in e for e in c.check_layout(lay)))

    def test_secret_hidden_in_a_path_is_refused(self):
        lay = yaml.safe_load(KEYS.replace('"C:\\\\Forge"', '"sk-abcdefghijklmnopqrstuvwx"'))
        errs = c.check_layout(lay)
        self.assertTrue(any("paths: root looks like an API key" in e for e in errs), errs)
        self.assertFalse(any("abcdefghij" in e for e in errs))

    def test_bad_color_and_text_mode(self):
        lay = yaml.safe_load(KEYS.replace('"#112233"', '"blue"').replace('text_mode: "paste"', 'text_mode: "fast"'))
        errs = c.check_layout(lay)
        self.assertTrue(any("color must look like" in e for e in errs), errs)
        self.assertTrue(any("text_mode must be type or paste" in e for e in errs), errs)


class NativeBuildFromKeys(unittest.TestCase):
    def test_paths_color_paste_and_ps1(self):
        prof, names = build_text(KEYS)
        ctrls = prof["layout"]["layoutModes"][0]["workspaces"][0]["pressPages"][0]["controls"]
        macros = {m["name"]: m for m in prof["macroCommands"]}
        run = macros[ctrls[0]["pressAction"].rsplit("___", 1)[1]]
        self.assertEqual(run["actions"], ['$@Generic___@ExecuteApplication___pwsh.exe||-NoProfile -ExecutionPolicy Bypass -File "C:\\Forge\\tools\\go.ps1"||'])
        paste = macros[ctrls[1]["pressAction"].rsplit("___", 1)[1]]
        self.assertEqual(paste["actionEditorCommands"][0]["actionParameters"]["text"], "hello C:\\Forge")
        self.assertEqual(paste["actionEditorCommands"][0]["actionParameters"]["useClipboard"], "true")
        folder = prof["profileActions"][0]
        self.assertEqual(folder["actionParameters"]["parameters"]["filePath"], "C:\\Forge\\tools")
        self.assertEqual(len([n for n in names if n.startswith("ActionImages/")]), 3)

    def test_single_ps1_run_never_opens_notepad(self):
        prof, _ = build_text(KEYS.replace("    - id: 1", "    - id: 9").split("    - id: 9")[0])
        self.assertEqual(prof["profileActions"], [])  # no ShellExecute for .ps1


class Migration(unittest.TestCase):
    def setUp(self):
        with open(os.path.join(ROOT, "lp5-build", "keypad-layout.yaml"), encoding="utf-8") as f:
            self.v1 = yaml.safe_load(f)

    def test_real_layout_round_trips(self):
        v2 = yaml.safe_load(mig.migrate(self.v1))
        self.assertEqual(c.check_layout(v2), [])
        self.assertTrue(mig.same_steps(self.v1, v2))
        self.assertEqual(v2["paths"]["templar"], "C:\\Templar Archives")
        self.assertEqual(v2["text_mode"], "type")

    def test_moving_templar_is_one_edit(self):
        text = mig.migrate(self.v1).replace('templar: "C:\\\\Templar Archives"', 'templar: "C:\\\\Forge"')
        v2 = c.expanded_layout(yaml.safe_load(text))
        opens = [s["open"] for p in range(1, 11) for b in v2[f"page_{p}"]["buttons"] for s in b["steps"] if "open" in s]
        self.assertTrue(any(o == "C:\\Forge\\Templar Nexus\\dist\\TheGateway\\TheGateway.exe" for o in opens), opens)
        self.assertFalse(any("Templar Archives" in o for o in opens))

    def test_substitute_whole_path_pieces_only(self):
        r, _ = c.resolve_paths({"t": "C:\\T"})
        used = set()
        self.assertEqual(mig.substitute('C:\\T\\x "C:\\T" C:\\Tx', r, used), '${t}\\x "${t}" C:\\Tx')
        self.assertEqual(used, {"t"})

    def test_checked_in_keys_yaml_is_valid(self):
        with open(os.path.join(ROOT, "v2", "keys.yaml"), encoding="utf-8") as f:
            v2 = yaml.safe_load(f)
        self.assertEqual(c.check_layout(v2), [])
        self.assertEqual(v2["profile_page_count"], 10)


if __name__ == "__main__":
    unittest.main()
