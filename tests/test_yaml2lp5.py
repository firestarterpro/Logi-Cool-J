"""Converter tests: every step type, the pre-build check, IDs and the .lp5 layout.

Run from the project folder:  python -m unittest discover -s tests -v
Needs pyyaml and pillow (same as the converter).
"""
import contextlib, io, json, os, re, shutil, sys, tempfile, unittest, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "lp5-build"))
import yaml2lp5 as c  # noqa: E402

HKL = c.HKL


def build(layout_yaml):
    """Writes layout_yaml to a temp folder, converts it, returns (ProfileInfo dict, zip names, stdout-free)."""
    d = tempfile.mkdtemp(prefix="lcj-test-")
    try:
        src = os.path.join(d, "layout.yaml")
        out = os.path.join(d, "out.lp5")
        with open(src, "w", encoding="utf-8") as f:
            f.write(layout_yaml)
        with contextlib.redirect_stdout(io.StringIO()):
            c.main(src, out)
        with zipfile.ZipFile(out) as z:
            prof = json.loads(z.read("ProfileInfo.json").decode("utf-8-sig"))
            names = z.namelist()
            preview = json.loads(z.read("metadata/ProfilePreview.json").decode("utf-8-sig"))
            pkg = z.read("metadata/LoupedeckPackage.yaml").decode("utf-8")
        return prof, names, preview, pkg
    finally:
        shutil.rmtree(d, ignore_errors=True)


def one_button(steps_yaml, pages=2, label="K"):
    return (f'profile_name: "T"\nprofile_page_count: {pages}\n'
            f'page_1:\n  name: "P1"\n  buttons:\n    - id: 0\n      label: "{label}"\n      steps:\n{steps_yaml}')


def macro_of(prof, cid=0, page=0):
    ws = prof["layout"]["layoutModes"][0]["workspaces"][0]
    action = ws["pressPages"][page]["controls"][cid]["pressAction"]
    mid = action.rsplit("___", 1)[1]
    return action, next(m for m in prof["macroCommands"] if m["name"] == mid)


class KeyStep(unittest.TestCase):
    def test_letters_and_modifier_order(self):
        self.assertEqual(c.key_string("Shift+Ctrl+t"), f"ControlOrCommand+Shift+KeyT___{HKL}___Shift+Ctrl+T___")
        self.assertEqual(c.key_string("Win+Alt+X"), f"AltOrOption+Windows+KeyX___{HKL}___Win+Alt+X___")

    def test_digits_function_keys_named_keys(self):
        self.assertTrue(c.key_string("Ctrl+1").startswith("ControlOrCommand+Digit1___"))
        self.assertTrue(c.key_string("f12").startswith("F12___"))
        self.assertEqual(c.key_string("Enter"), f"Return___{HKL}___Return___")
        self.assertTrue(c.key_string("Ctrl+.").startswith("ControlOrCommand+Period___"))

    def test_unconfirmed_names_are_reported(self):
        c.seen_unknown.clear()
        self.assertTrue(c.key_string("Win+Left").startswith("Windows+ArrowLeft___"))
        self.assertTrue(c.key_string("Ctrl+`").startswith("ControlOrCommand+Backquote___"))
        self.assertTrue(c.key_string("MediaPlayPause").startswith("MediaPlayPause___"))
        self.assertEqual(c.seen_unknown, {"Left", "`", "MediaPlayPause"})

    def test_key_step_in_profile(self):
        prof, *_ = build(one_button('        - key: "Ctrl+Shift+T"\n'))
        action, m = macro_of(prof)
        self.assertTrue(action.startswith("$@Generic___@Macro___"))
        ed = m["actionEditorCommands"][0]
        self.assertEqual(ed["templateName"], "$@Generic___@KeyboardKey")
        self.assertEqual(ed["actionParameters"]["keyboardKey"], f"ControlOrCommand+Shift+KeyT___{HKL}___Ctrl+Shift+T___")
        self.assertEqual(m["actions"], [ed["name"]])
        self.assertTrue(m["showAsSingleAction"])


class TextStep(unittest.TestCase):
    def test_text_is_typed_not_pasted_and_stays_a_string(self):
        prof, *_ = build(one_button('        - text: "yes"\n        - key: "Return"\n'))
        _, m = macro_of(prof)
        ed = m["actionEditorCommands"][0]
        self.assertEqual(ed["templateName"], "$@Generic___@SendText")
        self.assertEqual(ed["actionParameters"], {"$type": c.DICT_T, "text": "yes", "useClipboard": "false"})
        self.assertEqual(len(m["actions"]), 2)
        self.assertFalse(m["showAsSingleAction"])


class WaitAndUrlSteps(unittest.TestCase):
    def test_wait_becomes_sleep(self):
        prof, *_ = build(one_button('        - url: "https://example.com"\n        - wait: 1500\n'))
        _, m = macro_of(prof)
        self.assertEqual(m["actions"], ["$@Generic___@OpenUrl___https://example.com", "$@Generic___@Sleep___1500"])
        self.assertEqual(m["actionEditorCommands"], [])


class OpenAndRunSteps(unittest.TestCase):
    def test_exe_with_arguments(self):
        self.assertEqual(c.exec_app('wt.exe -d "C:\\x"'), '$@Generic___@ExecuteApplication___wt.exe||-d "C:\\x"||')
        self.assertEqual(c.exec_app('"C:\\Program Files\\A\\a.exe" --go'),
                         '$@Generic___@ExecuteApplication___C:\\Program Files\\A\\a.exe||--go||C:\\Program Files\\A')

    def test_exe_path_with_spaces_unquoted(self):
        path, args = c.split_exe('C:\\Templar Archives\\x\\electron.exe "C:\\Templar Archives\\app"')
        self.assertEqual(path, "C:\\Templar Archives\\x\\electron.exe")
        self.assertEqual(args, '"C:\\Templar Archives\\app"')

    def test_single_exe_open_is_a_macro(self):
        prof, *_ = build(one_button('        - open: "pwsh.exe -NoProfile -File \\"C:\\\\a b\\\\s.ps1\\" -Action Copy"\n'))
        _, m = macro_of(prof)
        self.assertEqual(m["actions"], ['$@Generic___@ExecuteApplication___pwsh.exe||-NoProfile -File "C:\\a b\\s.ps1" -Action Copy||'])

    def test_single_folder_open_and_run_are_shell_execute(self):
        y = ('profile_name: "T"\nprofile_page_count: 1\npage_1:\n  name: "P"\n  buttons:\n'
             '    - id: 0\n      label: "F"\n      steps:\n        - open: "%USERPROFILE%\\\\Downloads"\n'
             '    - id: 1\n      label: "R"\n      steps:\n        - run: "C:\\\\x\\\\go.bat"\n')
        prof, *_ = build(y)
        acts = prof["profileActions"]
        self.assertEqual(len(acts), 2)
        self.assertTrue(all(a["templateActionName"] == "$@Generic___@ShellExecute" for a in acts))
        self.assertEqual(acts[0]["actionParameters"]["parameters"]["filePath"],
                         os.path.join(os.environ.get("USERPROFILE", "%USERPROFILE%"), "Downloads"))
        self.assertEqual(acts[1]["actionParameters"]["parameters"]["filePath"], "C:\\x\\go.bat")
        ctrls = prof["layout"]["layoutModes"][0]["workspaces"][0]["pressPages"][0]["controls"]
        self.assertEqual([x["pressAction"] for x in ctrls[:2]], [a["name"] for a in acts])

    def test_uri_open_becomes_open_url(self):
        prof, *_ = build(one_button('        - open: "ms-settings:display"\n        - wait: 10\n'))
        _, m = macro_of(prof)
        self.assertEqual(m["actions"][0], "$@Generic___@OpenUrl___ms-settings:display")

    def test_run_inside_a_macro(self):
        prof, *_ = build(one_button('        - run: "C:\\\\x\\\\go.bat"\n        - wait: 10\n'))
        _, m = macro_of(prof)
        self.assertEqual(m["actions"][0], "$@Generic___@ExecuteApplication___C:\\x\\go.bat||||")


class GoToPageStep(unittest.TestCase):
    def test_go_to_page_points_at_that_page(self):
        prof, *_ = build(one_button('        - go_to_page: 2\n'))
        ws = prof["layout"]["layoutModes"][0]["workspaces"][0]
        action = ws["pressPages"][0]["controls"][0]["pressAction"]
        self.assertEqual(action, f"$@Generic___@ChangeTouchPage___System|{ws['name']}|{ws['pressPages'][1]['name']}")
        self.assertEqual(prof["macroCommands"], [])


class Check(unittest.TestCase):
    def assertProblem(self, steps_yaml, pattern, pages=2):
        import yaml
        probs = c.check_layout(yaml.safe_load(one_button(steps_yaml, pages)))
        self.assertTrue(any(re.search(pattern, p) for p in probs), probs)
        return probs

    def test_valid_layout_has_no_problems(self):
        import yaml
        with open(os.path.join(ROOT, "lp5-build", "keypad-layout.yaml"), encoding="utf-8") as f:
            self.assertEqual(c.check_layout(yaml.safe_load(f)), [])
        with open(os.path.join(ROOT, "lp5-build", "example-layout.yaml"), encoding="utf-8") as f:
            self.assertEqual(c.check_layout(yaml.safe_load(f)), [])

    def test_unknown_step(self):
        self.assertProblem('        - launch: "calc"\n', r"unknown step 'launch'")

    def test_two_keys_in_one_step(self):
        self.assertProblem('        - key: "A"\n          text: "b"\n', r"exactly one")

    def test_go_to_page_alone_and_in_range(self):
        self.assertProblem('        - go_to_page: 3\n', r"out of range 1\.\.2")
        self.assertProblem('        - go_to_page: 1\n        - wait: 5\n', r"only step")

    def test_wait_must_be_whole(self):
        self.assertProblem('        - wait: "soon"\n', r"whole number")

    def test_unknown_modifier(self):
        self.assertProblem('        - key: "Cmd+C"\n', r"unknown modifier 'Cmd'")

    def test_secret_refused_and_not_echoed(self):
        secret = "gsk_" + "A1b2C3d4E5f6G7h8I9j0K1l2M3n4"
        probs = self.assertProblem(f'        - text: "{secret}"\n', r"API key or password")
        self.assertFalse(any(secret in p for p in probs))
        self.assertProblem('        - text: "password: hunter2"\n', r"API key or password")

    def test_main_refuses_and_writes_nothing(self):
        d = tempfile.mkdtemp(prefix="lcj-test-")
        try:
            src, out = os.path.join(d, "l.yaml"), os.path.join(d, "o.lp5")
            with open(src, "w", encoding="utf-8") as f:
                f.write(one_button('        - go_to_page: 9\n'))
            with self.assertRaises(SystemExit) as cm:
                c.main(src, out)
            self.assertIn("Not built", str(cm.exception))
            self.assertFalse(os.path.exists(out))
        finally:
            shutil.rmtree(d, ignore_errors=True)

    def test_builder_and_converter_share_the_secret_list(self):
        with open(os.path.join(ROOT, "logi-cool-j-builder.html"), encoding="utf-8") as f:
            html = f.read()
        block = re.search(r"const SECRET_PATTERNS = \[(.*?)\];", html, re.S).group(1)
        js = [m.group(1) + ("(?i)" if m.group(2) else "") for m in re.finditer(r"/(.+?)/(i?),?\s*$", block, re.M)]
        py = [p.pattern.replace("(?i)", "") + ("(?i)" if p.flags & re.I else "") for p in c.SECRET_PATTERNS]
        self.assertEqual(sorted(js), sorted(py))


class Package(unittest.TestCase):
    def test_ids_are_stable_and_seeded_by_profile_id(self):
        y = one_button('        - key: "A"\n')
        a, *_ = build(y)
        b, *_ = build(y)
        self.assertEqual(a["name"], b["name"])
        self.assertEqual(a["macroCommands"][0]["name"], b["macroCommands"][0]["name"])
        seeded, *_ = build(y.replace('profile_name: "T"\n', 'profile_name: "T"\nprofile_id: "other"\n'))
        self.assertNotEqual(a["name"], seeded["name"])

    def test_zip_layout_icons_and_preview(self):
        y = one_button('        - key: "A"\n', label="Clip→txt") + \
            '    - id: 8\n      label: "Home"\n      steps:\n        - go_to_page: 1\n'
        prof, names, preview, pkg = build(y)
        for n in ("ApplicationIcon.png", "ApplicationInfo.json", "ProfileInfo.json", "metadata/AdvancedInfo.json",
                  "metadata/LoupedeckPackage.yaml", "metadata/ProfilePreview.json"):
            self.assertIn(n, names)
        icons = [n for n in names if n.startswith("ActionImages/")]
        self.assertEqual(len(icons), 1)  # page jumps get no icon
        self.assertEqual(len(preview["buttonPages"]), 9)
        self.assertIn(f"name: {prof['name']}", pkg)
        self.assertIn("displayName: T", pkg)
        ws = prof["layout"]["layoutModes"][0]["workspaces"][0]
        self.assertEqual(len(ws["pressPages"]), 2)
        self.assertEqual([x["pressAction"] for x in ws["pressPages"][1]["controls"]], [None] * 9)

    def test_icon_renders_without_dejavu(self):
        png = c.render_icon("Run Script", "#1F4E8C")
        self.assertTrue(png.startswith(b"\x89PNG"))

    def test_real_layout_builds(self):
        with open(os.path.join(ROOT, "lp5-build", "keypad-layout.yaml"), encoding="utf-8") as f:
            prof, names, *_ = build(f.read())
        ws = prof["layout"]["layoutModes"][0]["workspaces"][0]
        self.assertEqual([p["displayName"] for p in ws["pressPages"]][:2], ["AI Scripts", "Home"])
        self.assertEqual(len(ws["pressPages"]), 10)


if __name__ == "__main__":
    unittest.main()
