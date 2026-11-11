using LogiCoolJ.Core;
using Xunit;

namespace LogiCoolJ.Core.Tests;

internal static class Repo
{
    public static string V2
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "keys.yaml")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("v2 folder not found");
        }
    }

    /// <summary>A one-page layout; replace STEPS with the step lines of button 0 (8 spaces indent).</summary>
    public const string OnePage = """
        profile_name: "T"
        profile_page_count: 2
        paths:
          root: "C:\\Forge"
          tools: "${root}\\tools"
        page_1:
          name: "P1"
          buttons:
            - id: 0
              label: "K"
              steps:
        STEPS
        """;

    public static LoadResult LoadSteps(string stepLines) =>
        KeysLoader.Load(OnePage.Replace("STEPS", stepLines));
}

public class LoaderTests
{
    [Fact]
    public void RealKeysYamlLoadsWithoutErrors()
    {
        var r = KeysLoader.Load(File.ReadAllText(Path.Combine(Repo.V2, "keys.yaml")));
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        var l = r.Layout!;
        Assert.Equal(10, l.PageCount);
        Assert.Equal("AI Scripts", l.GetPage(1)!.Name);
        Assert.Equal("Home", l.GetPage(2)!.Name);
        Assert.Equal("Gateway", l.GetButton(2, 0)!.Label);
        Assert.Equal(@"C:\Templar Archives\Templar Nexus\dist\TheGateway\TheGateway.exe", l.ResolvedPaths["gateway"]);
        Assert.Equal("go_to_page", l.GetButton(1, 8)!.Steps.Single().Kind);
    }

    [Fact]
    public void EveryStepKindLoads()
    {
        var r = Repo.LoadSteps("""
                    - key: "Ctrl+Shift+T"
                    - text: "git status"
                    - wait: 500
                    - url: "https://example.com"
                    - open: "${tools}\\a.exe --x"
                    - run: "${root}\\go.bat"
            """);
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        Assert.Equal(new[] { "key", "text", "wait", "url", "open", "run" }, r.Layout!.GetButton(1, 0)!.Steps.Select(s => s.Kind));
    }

    [Theory]
    [InlineData("        - launch: \"calc\"", "unknown step 'launch'")]
    [InlineData("        - key: \"A\"\n          text: \"b\"", "exactly one of")]
    [InlineData("        - wait: \"soon\"", "wait must be a whole number")]
    [InlineData("        - wait: -5", "wait must be 0..")]
    [InlineData("        - go_to_page: 3", "out of range 1..2")]
    [InlineData("        - go_to_page: 1\n        - wait: 5", "only step")]
    [InlineData("        - key: \"Cmd+C\"", "unknown modifier 'Cmd'")]
    [InlineData("        - key: \"Ctrl+Banana\"", "unknown key name 'Banana'")]
    [InlineData("        - open: \"${nope}\\\\x.exe\"", "${nope} is not in paths")]
    [InlineData("        - text: \"\"", "value is empty")]
    public void BadStepsAreErrorsWithTheirLine(string steps, string expected)
    {
        var r = Repo.LoadSteps(steps);
        Assert.False(r.IsValid);
        var e = r.FirstError!;
        Assert.Contains(expected, e.Message);
        Assert.Equal(12, e.Line); // first step line in Repo.OnePage
        Assert.Equal(1, e.Page);
        Assert.Equal(0, e.Key);
    }

    [Fact]
    public void SecretsAreRefusedAndNeverEchoed()
    {
        var secret = "gsk_" + "A1b2C3d4E5f6G7h8I9j0K1l2M3n4";
        var r = Repo.LoadSteps($"        - text: \"{secret}\"");
        Assert.Contains("API key or password", r.FirstError!.Message);
        Assert.DoesNotContain(r.Problems, p => p.Message.Contains(secret));
        Assert.False(Repo.LoadSteps("        - text: \"password: hunter2\"").IsValid);
    }

    [Fact]
    public void SecretHiddenBehindAPathVariableIsRefused()
    {
        var r = KeysLoader.Load(Repo.OnePage.Replace("\"C:\\\\Forge\"", "\"sk-abcdefghijklmnopqrstuvwx\"").Replace("STEPS", "        - text: \"${root}\""));
        Assert.Contains(r.Problems, p => p.Message.Contains("paths: root looks like an API key"));
    }

    [Fact]
    public void YamlSyntaxErrorReportsALine()
    {
        var r = KeysLoader.Load("profile_name: \"T\"\npage_1:\n  name: \"P\n  buttons: [\n");
        Assert.Null(r.Layout);
        Assert.StartsWith("YAML", r.FirstError!.Message);
        Assert.True(r.FirstError.Line >= 2, r.FirstError.ToString());
    }

    [Fact]
    public void DuplicateKeysAreAProblemNotACrash()
    {
        var r = KeysLoader.Load("profile_name: \"A\"\nprofile_name: \"B\"\n");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void PathCyclesAndBadNamesAreErrors()
    {
        var r = KeysLoader.Load("paths:\n  a: \"${b}\"\n  b: \"${a}\"\n  \"bad name\": \"x\"\n");
        Assert.Contains(r.Problems, p => p.Message.Contains("refers to itself"));
        Assert.Contains(r.Problems, p => p.Message.Contains("not a valid name"));
    }

    [Fact]
    public void ColorTextModePageCountAndDuplicateIds()
    {
        var text = """
            profile_page_count: 11
            text_mode: "fast"
            page_1:
              buttons:
                - id: 0
                  color: "blue"
                  text_mode: "slow"
                  steps: []
                - id: 0
                  steps: []
            """;
        var msgs = KeysLoader.Load(text).Problems.Select(p => p.Message).ToList();
        Assert.Contains(msgs, m => m.Contains("profile_page_count must be 1..10"));
        Assert.Contains(msgs, m => m == "text_mode must be type or paste.");
        Assert.Contains(msgs, m => m.Contains("color must look like"));
        Assert.Contains(msgs, m => m.Contains("button 0: text_mode must be"));
        Assert.Contains(msgs, m => m.Contains("id used twice"));
    }

    [Fact]
    public void LongLabelIsOnlyAWarning()
    {
        var r = KeysLoader.Load(Repo.OnePage.Replace("label: \"K\"", "label: \"A very long label\"").Replace("STEPS", "        - key: \"A\""));
        Assert.True(r.IsValid);
        Assert.Contains(r.Problems, p => p.Severity == Severity.Warning && p.Message.Contains("over 12"));
    }

    [Fact]
    public void YesStaysAString()
    {
        var r = Repo.LoadSteps("        - text: yes");
        Assert.Equal("yes", r.Layout!.GetButton(1, 0)!.Steps[0].Value);
    }
}
