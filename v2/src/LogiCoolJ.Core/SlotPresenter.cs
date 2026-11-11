using System.Text.RegularExpressions;

namespace LogiCoolJ.Core;

public sealed record SlotView(string Label, string Color, bool IsError, bool IsEmpty, int Page, KeysButton? Button);

/// <summary>
/// Decides what each slot shows and does: the key from the last good layout, or the current file
/// error ("keys.yaml / line N / reason") on the key the error is about (key 0 when it is about no key).
/// Pressing a key that shows an error dismisses the error; it does not run anything.
/// </summary>
public sealed class SlotPresenter
{
    private static readonly Regex Where = new(@"^Page \d+ button \d+( step \d+)?( \([a-z_]+\))?: ", RegexOptions.Compiled);
    private readonly Func<SlotKind, LayoutHost?> _hostFor;
    private readonly Dictionary<LayoutHost, string> _dismissed = new();
    private int _currentPage = 1;

    public SlotPresenter(Func<SlotKind, LayoutHost?> hostFor) => this._hostFor = hostFor;

    public int CurrentPage
    {
        get => this._currentPage;
        set => this._currentPage = Math.Clamp(value, 1, KeysLayout.MaxPages);
    }

    public SlotView View(SlotId slot)
    {
        var host = this._hostFor(slot.Kind);
        var page = slot.PageFor(this.CurrentPage);
        var fileName = host == null ? "keys.yaml" : Path.GetFileName(host.FilePath);
        if (host == null)
        {
            return new SlotView(slot.Kind == SlotKind.Test ? "no test file" : "keys.yaml\nnot found", Palette.EmptyColor, false, true, page, null);
        }

        var error = host.Error;
        if (error != null && !this.IsDismissed(host, error) && ShowsErrorOn(error, slot, page))
        {
            return new SlotView($"{fileName}\n{(error.Line > 0 ? $"line {error.Line}" : "error")}\n{Short(error.Message)}", Palette.ErrorColor, true, false, page, null);
        }

        var layout = host.Current;
        if (layout == null)
        {
            return new SlotView(error == null ? "loading…" : "", Palette.EmptyColor, false, true, page, null);
        }

        if (page > layout.PageCount)
        {
            return new SlotView("", Palette.EmptyColor, false, true, page, null);
        }

        var button = layout.GetButton(page, slot.Key);
        if (button == null || (button.IsEmpty && button.Label.Length == 0))
        {
            return new SlotView("", Palette.EmptyColor, false, true, page, button);
        }

        var color = button.Color ?? Palette.ForPage(slot.Kind == SlotKind.Test ? 10 : page);
        return new SlotView(button.Label, color, false, button.IsEmpty, page, button);
    }

    /// <summary>Handles a press. Returns the steps to run, or null (error dismissed, or nothing to run).</summary>
    public IReadOnlyList<PlannedStep>? Press(SlotId slot, Func<string, string>? expandEnvironment = null)
    {
        var view = this.View(slot);
        var host = this._hostFor(slot.Kind);
        if (view.IsError && host?.Error != null)
        {
            lock (this._dismissed)
            {
                this._dismissed[host] = host.Error.ToString();
            }

            return null;
        }

        var layout = host?.Current;
        if (layout == null || view.Button == null || view.Button.IsEmpty)
        {
            return null;
        }

        return StepPlanner.Plan(layout, view.Button, expandEnvironment);
    }

    private bool IsDismissed(LayoutHost host, LayoutProblem error)
    {
        lock (this._dismissed)
        {
            return this._dismissed.TryGetValue(host, out var d) && d == error.ToString();
        }
    }

    private static bool ShowsErrorOn(LayoutProblem error, SlotId slot, int page)
    {
        if (error.Page is int ep && error.Key is int ek)
        {
            // On its own key; follow-page slots also show it on key 0 while another page is up.
            return (ep == page && ek == slot.Key) || (slot.Kind == SlotKind.FollowPage && ep != page && slot.Key == 0);
        }

        return slot.Key == 0;
    }

    private static string Short(string message)
    {
        var m = Where.Replace(message, "");
        return m.Length > 40 ? m[..40] + "…" : m;
    }
}
