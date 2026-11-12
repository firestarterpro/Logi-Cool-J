using System.Text;
using LogiCoolJ.Core;
using Xunit;

namespace LogiCoolJ.Core.Tests;

public sealed class HostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lcj-host-").FullName;

    public void Dispose() => Directory.Delete(this._dir, true);

    private static string Good(string label = "One") => Repo.OnePage.Replace("label: \"K\"", $"label: \"{label}\"").Replace("STEPS", "        - key: \"A\"");

    [Fact]
    public void BadTextKeepsTheLastGoodLayout()
    {
        using var host = new LayoutHost(Path.Combine(this._dir, "keys.yaml"));
        var events = new List<LayoutChangedEventArgs>();
        host.Changed += (_, e) => events.Add(e);

        Assert.True(host.ApplyText(Good("One")).IsValid);
        var first = host.Current;
        var bad = host.ApplyText(Good("Two").Replace("key: \"A\"", "launch: \"A\""));
        Assert.False(bad.IsValid);
        Assert.Same(first, host.Current);
        Assert.Equal("One", host.Current!.GetButton(1, 0)!.Label);
        Assert.Equal(12, host.Error!.Line);
        Assert.True(events[1].ErrorIsNew);
        Assert.False(events[1].LayoutSwapped);

        host.ApplyText(Good("Two").Replace("key: \"A\"", "launch: \"A\""));
        Assert.False(events[2].ErrorIsNew); // same error again: no second notification

        Assert.True(host.ApplyText(Good("Three")).IsValid);
        Assert.Null(host.Error);
        Assert.Equal("Three", host.Current!.GetButton(1, 0)!.Label);
    }

    [Fact]
    public void MissingFileIsAnErrorWithoutALayout()
    {
        using var host = new LayoutHost(Path.Combine(this._dir, "nope.yaml"));
        host.Reload();
        Assert.Null(host.Current);
        Assert.Contains("not found", host.Error!.Message);
    }

    [Fact]
    public async Task SavingTheFileSwapsTheLayoutWithinSeconds()
    {
        var path = Path.Combine(this._dir, "keys.yaml");
        File.WriteAllText(path, Good("Before"), new UTF8Encoding(false));
        using var host = new LayoutHost(path, debounceMs: 80, pollMs: 120);
        host.Reload();
        var spuriousCount = 0;
        var swapped = new TaskCompletionSource();
        host.Changed += (_, e) =>
        {
            if (host.Current?.GetButton(1, 0)?.Label == "Before") spuriousCount++;
            if (e.LayoutSwapped && host.Current?.GetButton(1, 0)?.Label == "After") swapped.TrySetResult();
        };
        host.Start();

        await Task.Delay(260); // past pollMs + debounceMs: Reload() seeded _lastStamp, so no spurious reload
        Assert.Equal(0, spuriousCount);
        File.WriteAllText(path, Good("After"), new UTF8Encoding(false));
        var done = await Task.WhenAny(swapped.Task, Task.Delay(5000));
        Assert.Same(swapped.Task, done);
    }
}

public class PresenterTests
{
    private static (SlotPresenter P, LayoutHost H) Make(string text)
    {
        var host = new LayoutHost(@"C:\nowhere\keys.yaml");
        host.ApplyText(text);
        return (new SlotPresenter(_ => host), host);
    }

    private const string TwoPages = """
        profile_page_count: 2
        page_1:
          name: "One"
          buttons:
            - id: 0
              label: "Hello"
              steps:
                - text: "hi"
            - id: 1
              label: "Blue"
              color: "#0000FF"
              steps:
                - key: "A"
        page_2:
          name: "Two"
          buttons:
            - id: 3
              label: "Back"
              steps:
                - go_to_page: 1
        """;

    [Fact]
    public void ShowsLabelsAndColours()
    {
        var (p, _) = Make(TwoPages);
        Assert.Equal(new SlotView("Hello", Palette.ForPage(1), false, false, 1, p.View(new SlotId(SlotKind.FollowPage, 0, 0)).Button), p.View(new SlotId(SlotKind.FollowPage, 0, 0)));
        Assert.Equal("#0000FF", p.View(new SlotId(SlotKind.FollowPage, 0, 1)).Color);
        Assert.True(p.View(new SlotId(SlotKind.FollowPage, 0, 5)).IsEmpty);
        p.CurrentPage = 2;
        Assert.Equal("Back", p.View(new SlotId(SlotKind.FollowPage, 0, 3)).Label);
        Assert.Equal("Hello", p.View(new SlotId(SlotKind.Fixed, 1, 0)).Label); // fixed slots ignore the current page
        Assert.True(p.View(new SlotId(SlotKind.Fixed, 7, 0)).IsEmpty);       // beyond profile_page_count
    }

    [Fact]
    public void PressPlansTheSteps()
    {
        var (p, _) = Make(TwoPages);
        Assert.Equal(new TextStep("hi", false), p.Press(new SlotId(SlotKind.FollowPage, 0, 0))!.Single());
        Assert.Null(p.Press(new SlotId(SlotKind.FollowPage, 0, 8)));
    }

    [Fact]
    public void ErrorShowsOnItsKeyAndAPressDismissesIt()
    {
        var (p, h) = Make(TwoPages);
        h.ApplyText(TwoPages.Replace("- key: \"A\"", "- key: \"Cmd+A\""));
        var slot = new SlotId(SlotKind.FollowPage, 0, 1);
        var v = p.View(slot);
        Assert.True(v.IsError);
        Assert.Equal(Palette.ErrorColor, v.Color);
        Assert.StartsWith("keys.yaml\nline 13\nunknown modifier", v.Label);
        Assert.False(p.View(new SlotId(SlotKind.FollowPage, 0, 0)).IsError); // the other keys keep working
        Assert.Equal("Hello", p.View(new SlotId(SlotKind.FollowPage, 0, 0)).Label);

        Assert.Null(p.Press(slot)); // dismiss, do not run
        Assert.False(p.View(slot).IsError);
        Assert.Equal("Blue", p.View(slot).Label); // last good layout
    }

    [Fact]
    public void ErrorOnAnotherPageShowsOnKeyOneOfTheCurrentPage()
    {
        var (p, h) = Make(TwoPages);
        h.ApplyText(TwoPages.Replace("go_to_page: 1", "go_to_page: 9"));
        Assert.True(p.View(new SlotId(SlotKind.FollowPage, 0, 0)).IsError);
        Assert.True(p.View(new SlotId(SlotKind.Fixed, 2, 3)).IsError);
        Assert.False(p.View(new SlotId(SlotKind.Fixed, 1, 0)).IsError);
    }

    [Fact]
    public void FileErrorWithoutAKeyShowsOnKeyOne()
    {
        var (p, h) = Make(TwoPages);
        h.ApplyText("page_1: [unclosed");
        Assert.True(p.View(new SlotId(SlotKind.FollowPage, 0, 0)).IsError);
        Assert.False(p.View(new SlotId(SlotKind.FollowPage, 0, 1)).IsError);
    }
}

public class NamesAndHelpersTests
{
    [Fact]
    public void SlotNamesRoundTrip()
    {
        var all = SlotId.All().ToList();
        Assert.Equal(108, all.Count);
        Assert.Equal(all.Count, all.Select(s => s.Name).Distinct().Count());
        foreach (var s in all)
        {
            Assert.True(SlotId.TryParse(s.Name, out var back));
            Assert.Equal(s, back);
        }

        Assert.Equal("p03k7", new SlotId(SlotKind.Fixed, 3, 7).Name);
        Assert.False(SlotId.TryParse("p11k0", out _));
        Assert.False(SlotId.TryParse("k9", out _));
        Assert.False(SlotId.TryParse(null, out _));
    }

    [Fact]
    public void ActionsFormHasOneClassPerSlot()
    {
        var file = File.ReadAllText(Path.Combine(Repo.V2, "src", "LogiCoolJPlugin", "Actions", "SlotActions.g.cs"));
        string ClassName(SlotId s) => s.Kind switch
        {
            SlotKind.FollowPage => $"KeyK{s.Key}",
            SlotKind.Fixed => $"SlotP{s.Page:00}K{s.Key}",
            _ => $"TestT{s.Key}",
        };
        foreach (var s in SlotId.All())
        {
            Assert.Contains($"class {ClassName(s)} : SlotActionBase {{ public {ClassName(s)}() : base(SlotKind.{s.Kind}, {s.Page}, {s.Key})", file);
        }
    }

    [Fact]
    public void KeyNamesExistInTheRealLoupedeckEnums()
    {
        var real = Enum.GetNames(typeof(Loupedeck.VirtualKeyCode)).ToHashSet();
        Assert.All(KeyCombo.VirtualKeyNames, n => Assert.Contains(n, real));
        foreach (var k in new[] { "Left", "`", "MediaPlayPause", "MediaPreviousTrack", "VolumeMute", "Return", "Backspace", "F12", "1", "z", ".", "PgDn" })
        {
            Assert.Contains(KeyCombo.KeyName(k)!, real);
        }

        var mods = Enum.GetNames(typeof(Loupedeck.ModifierKey));
        Assert.Contains("Control", mods);
        Assert.Contains("Shift", mods);
        Assert.Contains("Alt", mods);
        Assert.Contains("Windows", mods);
    }

    [Fact]
    public void KeyComboParsing()
    {
        Assert.Equal("Oem3", KeyCombo.TryParse("Ctrl+`", out _)!.VirtualKey);
        Assert.Equal("Key1", KeyCombo.TryParse("ctrl+1", out _)!.VirtualKey);
        Assert.Equal("Ctrl+Alt+Shift+R", KeyCombo.TryParse("Ctrl+Alt+Shift+R", out _)!.ToKeyString().Replace("Ctrl+Shift+Alt", "Ctrl+Alt+Shift"));
        Assert.Equal(KeyModifiers.Win, KeyCombo.TryParse("Win+Left", out _)!.Modifiers);
        Assert.Null(KeyCombo.TryParse("", out var e1));
        Assert.Contains("empty", e1);
        Assert.Null(KeyCombo.TryParse("Hyper+A", out var e2));
        Assert.Contains("unknown modifier 'Hyper'", e2);
    }

    [Fact]
    public void LocatorPrefersEnvThenPointerThenParents()
    {
        var root = Directory.CreateTempSubdirectory("lcj-loc-").FullName;
        try
        {
            var plugin = Directory.CreateDirectory(Path.Combine(root, "bin", "Release", "bin")).FullName;
            File.WriteAllText(Path.Combine(root, "keys.yaml"), "");
            Assert.Equal(Path.Combine(root, "keys.yaml"), KeysLocator.Find(plugin, _ => null));
            File.WriteAllText(Path.Combine(plugin, "keys-path.txt"), "\"D:\\x\\keys.yaml\"\n");
            Assert.Equal(@"D:\x\keys.yaml", KeysLocator.Find(plugin, _ => null));
            Assert.Equal(@"E:\k.yaml", KeysLocator.Find(plugin, n => n == KeysLocator.EnvironmentVariable ? @"E:\k.yaml" : null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NotificationScriptQuotesSafely()
    {
        var s = NotificationScript.Build("It's bad", "line 3: don't\nmore");
        Assert.Contains("'It''s bad'", s);
        Assert.Contains("'line 3: don''t more'", s);
        var args = NotificationScript.Arguments("t", "m");
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
        Assert.Equal(NotificationScript.Build("t", "m"), decoded);
    }

    [Fact]
    public void PaletteMatchesTheConverter()
    {
        var py = File.ReadAllText(Path.Combine(Repo.V2, "..", "lp5-build", "yaml2lp5.py"));
        foreach (var c in Palette.PageColors)
        {
            Assert.Contains($"\"{c}\"", py);
        }

        Assert.True(Palette.TryParse("#1F4E8C", out var rgb));
        Assert.Equal((0x1F, 0x4E, 0x8C), (rgb.R, rgb.G, rgb.B));
        Assert.False(Palette.TryParse("blue", out _));
    }
}
