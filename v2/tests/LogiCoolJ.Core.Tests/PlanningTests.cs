using LogiCoolJ.Core;
using Xunit;

namespace LogiCoolJ.Core.Tests;

public class PlanningTests
{
    private static IReadOnlyList<PlannedStep> Plan(string steps, string extra = "")
    {
        var r = KeysLoader.Load(Repo.OnePage.Replace("label: \"K\"", "label: \"K\"" + extra).Replace("STEPS", steps));
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        return StepPlanner.Plan(r.Layout!, r.Layout!.GetButton(1, 0)!, s => s.Replace("%USERPROFILE%", @"C:\Users\me"));
    }

    [Fact]
    public void KeyTextWaitUrlAndPage()
    {
        var p = Plan("        - key: \"ctrl+shift+t\"\n        - text: \"hi ${root}\"\n        - wait: 250\n        - url: \"https://x.test\"");
        var key = Assert.IsType<KeyStep>(p[0]);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift, key.Combo.Modifiers);
        Assert.Equal("KeyT", key.Combo.VirtualKey);
        Assert.Equal(new TextStep(@"hi C:\Forge", false), p[1]);
        Assert.Equal(new WaitStep(250), p[2]);
        Assert.Equal(new LaunchStep(LaunchKind.Uri, "https://x.test", "", ""), p[3]);
        Assert.Equal(new GoToPageStep(2), Plan("        - go_to_page: 2").Single());
    }

    [Fact]
    public void PasteModeFromTheKeyOrTheFile()
    {
        Assert.Equal(new TextStep("x", true), Plan("        - text: \"x\"", "\n      text_mode: \"paste\"").Single());
        var r = KeysLoader.Load("text_mode: \"paste\"\n" + Repo.OnePage.Replace("STEPS", "        - text: \"x\""));
        Assert.Equal(new TextStep("x", true), StepPlanner.Plan(r.Layout!, r.Layout!.GetButton(1, 0)!).Single());
    }

    [Fact]
    public void ProgramsWithArgumentsAndPaths()
    {
        Assert.Equal(new LaunchStep(LaunchKind.Program, @"C:\Forge\tools\a.exe", "--x \"y z\"", @"C:\Forge\tools"),
            Plan("        - open: \"${tools}\\\\a.exe --x \\\"y z\\\"\"").Single());
        Assert.Equal(new LaunchStep(LaunchKind.Program, "wt.exe", "-d \"C:\\Forge\"", ""),
            Plan("        - open: \"wt.exe -d \\\"${root}\\\"\"").Single());
        Assert.Equal(new LaunchStep(LaunchKind.Program, @"C:\Program Files\A\a.exe", "--go", @"C:\Program Files\A"),
            StepPlanner.PlanLaunch("\"C:\\Program Files\\A\\a.exe\" --go"));
        Assert.Equal(new LaunchStep(LaunchKind.Program, @"C:\T A\x\electron.exe", "\"C:\\T A\\app\"", @"C:\T A\x"),
            StepPlanner.PlanLaunch("C:\\T A\\x\\electron.exe \"C:\\T A\\app\""));
    }

    [Fact]
    public void FoldersFilesAndUris()
    {
        Assert.Equal(new LaunchStep(LaunchKind.Shell, @"C:\Users\me\Downloads", "", ""), Plan("        - open: \"%USERPROFILE%\\\\Downloads\"").Single());
        Assert.Equal(new LaunchStep(LaunchKind.Shell, @"C:\Forge\go.bat", "", ""), Plan("        - run: \"${root}\\\\go.bat\"").Single());
        Assert.Equal(LaunchKind.Uri, StepPlanner.PlanLaunch("ms-settings:display").Kind);
        Assert.Equal(LaunchKind.Shell, StepPlanner.PlanLaunch(@"C:\Templar Archives").Kind);
    }

    [Fact]
    public void Ps1AlwaysRunsThroughPwsh()
    {
        var step = Assert.IsType<LaunchStep>(Plan("        - run: \"${tools}\\\\go.ps1\"").Single());
        Assert.Equal(LaunchKind.Program, step.Kind);
        Assert.Equal("pwsh.exe", step.Target);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\Forge\\tools\\go.ps1\"", step.Arguments);
        Assert.Throws<FileNotFoundException>(() => step.EnsureTargetExists());
    }

    [Fact]
    public void RealLayoutPlansEveryKey()
    {
        var l = KeysLoader.Load(File.ReadAllText(Path.Combine(Repo.V2, "keys.yaml"))).Layout!;
        var count = 0;
        for (var p = 1; p <= l.PageCount; p++)
        {
            foreach (var b in l.GetPage(p)!.Buttons.Where(b => b != null && !b.IsEmpty))
            {
                Assert.NotEmpty(StepPlanner.Plan(l, b!));
                count++;
            }
        }

        Assert.Equal(90, count);
        var openDl = Assert.IsType<LaunchStep>(StepPlanner.Plan(l, l.GetButton(1, 0)!).Single());
        Assert.Equal("pwsh.exe", openDl.Target);
        Assert.Contains(@"C:\Templar Archives\stacks-attacks\tools\ai-scripts\AiScript.ps1", openDl.Arguments);
    }
}

public class RunnerTests
{
    private sealed class FakeSink : IStepSink
    {
        public List<string> Log { get; } = new();
        public bool FailOnLaunch { get; init; }

        public void SendKey(KeyCombo combo) => this.Log.Add("key " + combo.ToKeyString());
        public void TypeText(string text) => this.Log.Add("type " + text);
        public void PasteText(string text) => this.Log.Add("paste " + text);
        public void Launch(LaunchStep step)
        {
            if (this.FailOnLaunch) throw new InvalidOperationException("not found");
            this.Log.Add("launch " + step.Target);
        }

        public void GoToPage(int page) => this.Log.Add("page " + page);
    }

    [Fact]
    public async Task RunsInOrderAndWaits()
    {
        var sink = new FakeSink();
        var waits = new List<int>();
        var runner = new StepRunner(sink, (ms, _) => { waits.Add(ms); return Task.CompletedTask; }) { GapMs = 7 };
        var steps = new PlannedStep[]
        {
            new LaunchStep(LaunchKind.Program, "a.exe", "", ""), new WaitStep(300),
            new KeyStep(KeyCombo.TryParse("Ctrl+L", out _)!), new TextStep("x", false), new TextStep("y", true), new GoToPageStep(3),
        };
        await runner.RunAsync(steps, "t");
        Assert.Equal(new[] { "launch a.exe", "key Ctrl+L", "type x", "paste y", "page 3" }, sink.Log);
        Assert.Equal(new[] { 300, 7, 7, 7 }, waits);
    }

    [Fact]
    public async Task AFailureStopsThatMacroAndIsReported()
    {
        var sink = new FakeSink { FailOnLaunch = true };
        var runner = new StepRunner(sink, (_, _) => Task.CompletedTask);
        string? failed = null;
        runner.Failed += m => failed = m;
        await runner.RunAsync(new PlannedStep[] { new LaunchStep(LaunchKind.Program, "a.exe", "", ""), new GoToPageStep(2) }, "page 1 key 1");
        Assert.Empty(sink.Log);
        Assert.Equal("page 1 key 1, step 1: not found", failed);
    }

    [Fact]
    public async Task MacrosQueueOneAfterAnother()
    {
        var sink = new FakeSink();
        var gate = new TaskCompletionSource();
        var runner = new StepRunner(sink, async (ms, _) => { if (ms == 999) await gate.Task; });
        var first = runner.Enqueue(new PlannedStep[] { new WaitStep(999), new GoToPageStep(1) }, "a");
        var second = runner.Enqueue(new PlannedStep[] { new GoToPageStep(2) }, "b");
        await Task.Delay(50);
        Assert.Empty(sink.Log);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "page 1", "page 2" }, sink.Log);
    }
}
