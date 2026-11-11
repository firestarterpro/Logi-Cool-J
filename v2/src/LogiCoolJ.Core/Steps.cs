using System.Globalization;
using System.Text.RegularExpressions;

namespace LogiCoolJ.Core;

public abstract record PlannedStep;

public sealed record KeyStep(KeyCombo Combo) : PlannedStep;

/// <summary>Typed with SendString, or pasted through the clipboard when Paste is true.</summary>
public sealed record TextStep(string Text, bool Paste) : PlannedStep;

public sealed record WaitStep(int Milliseconds) : PlannedStep;

public enum LaunchKind
{
    /// <summary>A URL or other URI (https:, ms-settings:, spotify:): opened by Windows.</summary>
    Uri,

    /// <summary>A program with arguments (an .exe, or pwsh for a .ps1).</summary>
    Program,

    /// <summary>A folder, document or script opened by Windows (ShellExecute).</summary>
    Shell,
}

public sealed record LaunchStep(LaunchKind Kind, string Target, string Arguments, string WorkingDirectory) : PlannedStep;

public sealed record GoToPageStep(int Page) : PlannedStep;

/// <summary>Turns a checked button into concrete steps: paths expanded, programs split from arguments.</summary>
public static class StepPlanner
{
    private static readonly Regex ExeWithArgs = new(@"^(""?)(.+?\.exe)\1(?:\s+(.*))?$", RegexOptions.IgnoreCase);
    private static readonly Regex UriScheme = new(@"^[a-zA-Z][a-zA-Z0-9+.-]+:(?!\\)");

    public static IReadOnlyList<PlannedStep> Plan(KeysLayout layout, KeysButton button, Func<string, string>? expandEnvironment = null)
    {
        expandEnvironment ??= Environment.ExpandEnvironmentVariables;
        var paste = (button.TextMode ?? layout.TextMode) == "paste";
        var steps = new List<PlannedStep>();
        foreach (var s in button.Steps)
        {
            var value = s.Kind is "text" or "url" or "open" or "run" ? PathVariables.Expand(s.Value, layout.ResolvedPaths) : s.Value;
            switch (s.Kind)
            {
                case "key":
                    steps.Add(new KeyStep(KeyCombo.TryParse(value, out _) ?? throw new InvalidOperationException($"line {s.Line}: bad key")));
                    break;
                case "text":
                    steps.Add(new TextStep(value, paste));
                    break;
                case "wait":
                    steps.Add(new WaitStep(int.Parse(value, CultureInfo.InvariantCulture)));
                    break;
                case "url":
                    steps.Add(new LaunchStep(LaunchKind.Uri, value.Trim(), "", ""));
                    break;
                case "open":
                case "run":
                    steps.Add(PlanLaunch(expandEnvironment(value.Trim())));
                    break;
                case "go_to_page":
                    steps.Add(new GoToPageStep(int.Parse(value, CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return steps;
    }

    /// <summary>open/run target -> how to start it. A .ps1 always runs through pwsh (Windows would open Notepad).</summary>
    public static LaunchStep PlanLaunch(string target)
    {
        var bare = target.Trim().Trim('"');
        if (bare.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchStep(LaunchKind.Program, "pwsh.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{bare}\"", DirectoryOf(bare));
        }

        var m = ExeWithArgs.Match(target);
        if (m.Success)
        {
            var exe = m.Groups[2].Value;
            return new LaunchStep(LaunchKind.Program, exe, m.Groups[3].Value, exe.Contains('\\') ? DirectoryOf(exe) : "");
        }

        if (UriScheme.IsMatch(target))
        {
            return new LaunchStep(LaunchKind.Uri, target, "", "");
        }

        return new LaunchStep(LaunchKind.Shell, bare, "", "");
    }

    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('\\');
        return i > 0 ? path[..i] : "";
    }
}

/// <summary>What the plugin does for each kind of step. The plugin implements it with the Logi SDK.</summary>
public interface IStepSink
{
    void SendKey(KeyCombo combo);

    void TypeText(string text);

    void PasteText(string text);

    void Launch(LaunchStep step);

    void GoToPage(int page);
}

/// <summary>Runs planned steps in order, one key press at a time, never two macros at once.</summary>
public sealed class StepRunner
{
    private readonly IStepSink _sink;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Gap after each key or text step so the target window keeps up.</summary>
    public int GapMs { get; init; } = 40;

    public event Action<string>? Failed;

    public StepRunner(IStepSink sink, Func<int, CancellationToken, Task>? delay = null)
    {
        this._sink = sink;
        this._delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    /// <summary>Queues a macro behind any that is still running. The task completes when it has run.</summary>
    public Task Enqueue(IReadOnlyList<PlannedStep> steps, string what, CancellationToken ct = default)
    {
        lock (this._gate)
        {
            this._tail = this._tail.ContinueWith(_ => this.RunAsync(steps, what, ct), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return this._tail;
        }
    }

    public async Task RunAsync(IReadOnlyList<PlannedStep> steps, string what, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var step in steps)
        {
            n++;
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                switch (step)
                {
                    case KeyStep k:
                        this._sink.SendKey(k.Combo);
                        await this._delay(this.GapMs, ct).ConfigureAwait(false);
                        break;
                    case TextStep t:
                        if (t.Paste) this._sink.PasteText(t.Text); else this._sink.TypeText(t.Text);
                        await this._delay(this.GapMs, ct).ConfigureAwait(false);
                        break;
                    case WaitStep w:
                        await this._delay(w.Milliseconds, ct).ConfigureAwait(false);
                        break;
                    case LaunchStep l:
                        this._sink.Launch(l);
                        break;
                    case GoToPageStep g:
                        this._sink.GoToPage(g.Page);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Stop this macro; the next key press still works.
                this.Failed?.Invoke($"{what}, step {n}: {e.Message}");
                return;
            }
        }
    }
}

public enum SlotKind
{
    /// <summary>k0..k8: shows key K of the plugin's current page (virtual paging).</summary>
    FollowPage,

    /// <summary>p01k0..p10k8: always page P, key K (for a profile with 10 device pages).</summary>
    Fixed,

    /// <summary>t0..t8: key K of device-test.yaml.</summary>
    Test,
}

/// <summary>Names of the plugin's slots. These strings are the action parameters in profiles.</summary>
public readonly record struct SlotId(SlotKind Kind, int Page, int Key)
{
    private static readonly Regex Pattern = new(@"^(?:k([0-8])|p(\d\d)k([0-8])|t([0-8]))$");

    public string Name => this.Kind switch
    {
        SlotKind.FollowPage => $"k{this.Key}",
        SlotKind.Fixed => $"p{this.Page:00}k{this.Key}",
        _ => $"t{this.Key}",
    };

    public static bool TryParse(string? s, out SlotId id)
    {
        id = default;
        var m = Pattern.Match(s ?? "");
        if (!m.Success)
        {
            return false;
        }

        if (m.Groups[1].Success) id = new SlotId(SlotKind.FollowPage, 0, m.Groups[1].Value[0] - '0');
        else if (m.Groups[4].Success) id = new SlotId(SlotKind.Test, 1, m.Groups[4].Value[0] - '0');
        else
        {
            var page = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (page < 1 || page > KeysLayout.MaxPages) return false;
            id = new SlotId(SlotKind.Fixed, page, m.Groups[3].Value[0] - '0');
        }

        return true;
    }

    /// <summary>Every slot: 9 follow-page, 90 fixed, 9 test.</summary>
    public static IEnumerable<SlotId> All()
    {
        for (var k = 0; k < 9; k++) yield return new SlotId(SlotKind.FollowPage, 0, k);
        for (var p = 1; p <= KeysLayout.MaxPages; p++)
            for (var k = 0; k < 9; k++) yield return new SlotId(SlotKind.Fixed, p, k);
        for (var k = 0; k < 9; k++) yield return new SlotId(SlotKind.Test, 1, k);
    }

    /// <summary>The page this slot shows, given the plugin's current page.</summary>
    public int PageFor(int currentPage) => this.Kind == SlotKind.FollowPage ? currentPage : this.Page;
}
