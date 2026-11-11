namespace Loupedeck.LogiCoolJPlugin
{
    using System;

    using LogiCoolJ.Core;

#if !SLOT_ACTIONS
    // Default form (SlotForm=Parameters): one action, one parameter per slot.
    // In a profile: $LogiCoolJ___Loupedeck.LogiCoolJPlugin.SlotCommand___k0 (k0..k8, p01k0..p10k8, t0..t8).
    public class SlotCommand : PluginDynamicCommand
    {
        public SlotCommand()
            : base()
        {
            foreach (var slot in SlotId.All())
            {
                this.AddParameter(slot.Name, SlotNames.DisplayName(slot), SlotNames.Group(slot));
            }
        }

        private LogiCoolJPlugin Owner => this.Plugin as LogiCoolJPlugin;

        protected override Boolean OnLoad()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw += this.RedrawAll;
            }

            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw -= this.RedrawAll;
            }

            return true;
        }

        private void RedrawAll()
        {
            foreach (var slot in SlotId.All())
            {
                this.ActionImageChanged(slot.Name);
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            if (SlotId.TryParse(actionParameter, out var slot))
            {
                this.Owner?.RunSlot(slot);
            }
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            this.Owner != null && SlotId.TryParse(actionParameter, out var slot) ? SlotRenderer.DisplayName(this.Owner.View(slot).Label) : null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (this.Owner == null || !SlotId.TryParse(actionParameter, out var slot))
            {
                return null;
            }

            var view = this.Owner.View(slot);
            return SlotRenderer.Render(view.Label, view.Color, imageSize);
        }
    }
#else
    // SlotForm=Actions: one action class per slot (see SlotActions.g.cs). Same behaviour.
    public abstract class SlotActionBase : PluginDynamicCommand
    {
        private readonly SlotId _slot;

        protected SlotActionBase(SlotKind kind, Int32 page, Int32 key)
            : base(displayName: SlotNames.DisplayName(new SlotId(kind, page, key)), description: "Logi Cool J slot", groupName: SlotNames.Group(new SlotId(kind, page, key)))
        {
            this._slot = new SlotId(kind, page, key);
        }

        private LogiCoolJPlugin Owner => this.Plugin as LogiCoolJPlugin;

        protected override Boolean OnLoad()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw += this.Changed;
            }

            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw -= this.Changed;
            }

            return true;
        }

        private void Changed() => this.ActionImageChanged();

        protected override void RunCommand(String actionParameter) => this.Owner?.RunSlot(this._slot);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            this.Owner == null ? null : SlotRenderer.DisplayName(this.Owner.View(this._slot).Label);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (this.Owner == null)
            {
                return null;
            }

            var view = this.Owner.View(this._slot);
            return SlotRenderer.Render(view.Label, view.Color, imageSize);
        }
    }
#endif

    // Shows whether keys.yaml loaded and which page is up. Press: read keys.yaml again now.
    public class StatusCommand : PluginDynamicCommand
    {
        public StatusCommand()
            : base(displayName: "Logi Cool J status", description: "Shows whether keys.yaml loaded; press to read it again", groupName: "Pages and status")
        {
        }

        private LogiCoolJPlugin Owner => this.Plugin as LogiCoolJPlugin;

        protected override Boolean OnLoad()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw += this.Changed;
            }

            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this.Owner != null)
            {
                this.Owner.Redraw -= this.Changed;
            }

            return true;
        }

        private void Changed() => this.ActionImageChanged();

        protected override void RunCommand(String actionParameter) => this.Owner?.ReloadNow();

        private (String Text, String Color) Status()
        {
            var keys = this.Owner?.Keys;
            if (keys == null)
            {
                return ("keys.yaml\nnot found", Palette.ErrorColor);
            }

            var page = this.Owner.Presenter.CurrentPage;
            var name = keys.Current?.GetPage(page)?.Name ?? "";
            if (keys.Error != null)
            {
                return ($"keys.yaml\n{(keys.Error.Line > 0 ? $"line {keys.Error.Line}" : "error")}\nP{page}", Palette.ErrorColor);
            }

            return ($"OK\nP{page} {name}", Palette.ForPage(page));
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => SlotRenderer.DisplayName(this.Status().Text);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var (text, color) = this.Status();
            return SlotRenderer.Render(text, color, imageSize);
        }
    }

    // Page jumps for any button (for example the keypad's own page buttons, if Options+ lets you assign them).
    public class PageCommand : PluginDynamicCommand
    {
        public PageCommand()
            : base()
        {
            this.AddParameter("next", "Next page", "Pages and status");
            this.AddParameter("prev", "Previous page", "Pages and status");
            for (var p = 1; p <= KeysLayout.MaxPages; p++)
            {
                this.AddParameter($"page{p}", $"Go to page {p}", "Pages and status");
            }
        }

        private LogiCoolJPlugin Owner => this.Plugin as LogiCoolJPlugin;

        protected override void RunCommand(String actionParameter)
        {
            var owner = this.Owner;
            if (owner?.Presenter == null)
            {
                return;
            }

            var count = owner.Keys?.Current?.PageCount ?? KeysLayout.MaxPages;
            var current = owner.Presenter.CurrentPage;
            if (actionParameter == "next")
            {
                owner.SetPage(current >= count ? 1 : current + 1);
            }
            else if (actionParameter == "prev")
            {
                owner.SetPage(current <= 1 ? count : current - 1);
            }
            else if (actionParameter != null && actionParameter.StartsWith("page") && Int32.TryParse(actionParameter.Substring(4), out var p))
            {
                owner.SetPage(p);
            }
        }
    }
}
