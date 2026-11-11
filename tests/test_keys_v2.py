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


class SlotProfilesAndInstaller(unittest.TestCase):
    def test_slot_action_strings_match_sdk_action_string(self):
        self.assertEqual(c.slot_action("k0"), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k0")
        self.assertEqual(c.slot_action("k8"), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k8")
        self.assertEqual(c.slot_action("p01k0"), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___p01k0")
        self.assertEqual(c.slot_action("p10k8"), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___p10k8")
        self.assertEqual(c.slot_action("t0"), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___t0")
        self.assertEqual(c.slot_action("k0", actions_form=True), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.KeyK0")
        self.assertEqual(c.slot_action("p01k0", actions_form=True), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotP01K0")
        self.assertEqual(c.slot_action("p10k8", actions_form=True), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotP10K8")
        self.assertEqual(c.slot_action("t8", actions_form=True), "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.TestT8")
        with self.assertRaises(ValueError):
            c.slot_action("k9")

    def test_build_slots_writes_all_three_profiles_from_keys_yaml(self):
        d = tempfile.mkdtemp(prefix="lcj-slots-")
        try:
            keys_path = os.path.join(ROOT, "v2", "keys.yaml")
            with contextlib.redirect_stdout(io.StringIO()):
                paths = c.build_slots(keys_path, d)
            self.assertEqual([os.path.basename(p) for p in paths], ["slots.lp5", "slots-actions.lp5", "slots-10pages.lp5"])

            def read_lp5(path):
                with zipfile.ZipFile(path) as z:
                    prof = json.loads(z.read("ProfileInfo.json").decode("utf-8-sig"))
                    adv = json.loads(z.read("metadata/AdvancedInfo.json").decode("utf-8-sig"))
                    prev = json.loads(z.read("metadata/ProfilePreview.json").decode("utf-8-sig"))
                    return prof, adv, prev, z.namelist()

            prof_s, adv_s, prev_s, names_s = read_lp5(paths[0])
            ws_s = prof_s["layout"]["layoutModes"][0]["workspaces"][0]
            self.assertEqual(len(ws_s["pressPages"]), 1)
            self.assertEqual(
                [x["pressAction"] for x in ws_s["pressPages"][0]["controls"]],
                [f"$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k{k}" for k in range(9)],
            )
            self.assertEqual(prof_s["macroCommands"], [])
            self.assertEqual(prof_s["profileActions"], [])
            self.assertEqual([n for n in names_s if n.startswith("ActionImages/")], [])
            self.assertEqual(len(prev_s["buttonPages"]), 9)
            self.assertIn("LogiCoolJ", prof_s["additionalNativePluginNames"])
            self.assertIn("LogiCoolJ", adv_s["additionalPluginNames"])

            prof_a, adv_a, _, names_a = read_lp5(paths[1])
            ws_a = prof_a["layout"]["layoutModes"][0]["workspaces"][0]
            self.assertEqual(len(ws_a["pressPages"]), 1)
            self.assertEqual(
                [x["pressAction"] for x in ws_a["pressPages"][0]["controls"]],
                [f"$LogiCoolJ___Loupedeck.LogiCoolJPlugin.KeyK{k}" for k in range(9)],
            )
            self.assertEqual([n for n in names_a if n.startswith("ActionImages/")], [])
            self.assertIn("LogiCoolJ", adv_a["additionalPluginNames"])

            prof_10, adv_10, _, names_10 = read_lp5(paths[2])
            ws_10 = prof_10["layout"]["layoutModes"][0]["workspaces"][0]
            self.assertEqual(len(ws_10["pressPages"]), 10)
            p1_ctrls = [x["pressAction"] for x in ws_10["pressPages"][0]["controls"]]
            self.assertEqual(
                p1_ctrls[:7],
                [f"$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___p01k{k}" for k in range(7)],
            )
            # Page 1 keys 7 (Index -> page 10) and 8 (Home -> page 2) are native ChangeTouchPage jumps.
            self.assertEqual(p1_ctrls[7], f"$@Generic___@ChangeTouchPage___System|{ws_10['name']}|{ws_10['pressPages'][9]['name']}")
            self.assertEqual(p1_ctrls[8], f"$@Generic___@ChangeTouchPage___System|{ws_10['name']}|{ws_10['pressPages'][1]['name']}")
            # Page 2 key 7 (AI Scripts -> page 1) and key 8 (Index -> page 10) are ChangeTouchPage; key 0 (Gateway) is p02k0.
            p2_ctrls = [x["pressAction"] for x in ws_10["pressPages"][1]["controls"]]
            self.assertEqual(p2_ctrls[0], "$LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___p02k0")
            self.assertEqual(p2_ctrls[7], f"$@Generic___@ChangeTouchPage___System|{ws_10['name']}|{ws_10['pressPages'][0]['name']}")
            self.assertEqual(p2_ctrls[8], f"$@Generic___@ChangeTouchPage___System|{ws_10['name']}|{ws_10['pressPages'][9]['name']}")
            self.assertEqual(prof_10["macroCommands"], [])
            self.assertEqual(prof_10["profileActions"], [])
            self.assertEqual([n for n in names_10 if n.startswith("ActionImages/")], [])
            self.assertIn("LogiCoolJ", adv_10["additionalPluginNames"])

            # Distinct profile IDs and displayNames across the three slot profiles
            self.assertEqual(len({prof_s["name"], prof_a["name"], prof_10["name"]}), 3)
            self.assertEqual(len({prof_s["displayName"], prof_a["displayName"], prof_10["displayName"]}), 3)
        finally:
            shutil.rmtree(d, ignore_errors=True)

    def test_install_plugin_script_exists_and_has_expected_steps(self):
        ps1 = os.path.join(ROOT, "v2", "Install-Plugin.ps1")
        self.assertTrue(os.path.exists(ps1))
        with open(ps1, encoding="utf-8") as f:
            text = f.read()
        for step in (
            "dotnet tool restore",
            "-p:LinkToPluginService=true",
            "logiplugintool -- pack",
            "logiplugintool -- verify",
            "logiplugintool -- install",
            "Start-Process -FilePath $PackagePath",
        ):
            self.assertIn(step, text)


if __name__ == "__main__":
    unittest.main()
