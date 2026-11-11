namespace Loupedeck.LogiCoolJPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;

    using LogiCoolJ.Core;

    // Logi Cool J: keypad slots whose label, colour and steps come from keys.yaml.
    // The file is watched; a valid save swaps the layout within about a second, a bad save keeps the
    // last good layout and shows the error (with its line) on a key, in the plugin status and in a
    // Windows notification.
    public class LogiCoolJPlugin : Plugin
    {
        private const String PageSetting = "CurrentPage";
        private String _lastNotified;

        public LogiCoolJPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
        }

        // Universal plugin: no application has to be in front, and no shortcuts are tied to one app.
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        // NativeMethods is protected on Plugin; the step sink reaches it through here.
        internal INativeMethods Native => this.NativeMethods;

        internal LayoutHost Keys { get; private set; }

        internal LayoutHost DeviceTest { get; private set; }

        internal SlotPresenter Presenter { get; private set; }

        internal StepRunner Runner { get; private set; }

        // Slot, status and page actions subscribe and redraw their images.
        internal event Action Redraw;

        public override void Load()
        {
            this.Presenter = new SlotPresenter(kind => kind == SlotKind.Test ? this.DeviceTest : this.Keys);
            if (this.TryGetPluginSetting(PageSetting, out var saved) && Int32.TryParse(saved, out var page))
            {
                this.Presenter.CurrentPage = page;
            }

            this.Runner = new StepRunner(new PluginStepSink(this));
            this.Runner.Failed += message =>
            {
                PluginLog.Warning(message);
                this.Notify("Logi Cool J: a key failed", message);
            };

            var pluginDir = Path.GetDirectoryName(this.AssemblyFilePath) ?? AppContext.BaseDirectory;
            var keysPath = KeysLocator.Find(pluginDir);
            if (keysPath == null)
            {
                this.OnPluginStatusChanged(Loupedeck.PluginStatus.Error, "keys.yaml not found. Run v2\\Install-Plugin.ps1 again, or set LOGICOOLJ_KEYS.");
                PluginLog.Error($"keys.yaml not found from {pluginDir}");
                return;
            }

            PluginLog.Info($"Watching {keysPath}");
            this.Keys = new LayoutHost(keysPath);
            this.Keys.Changed += this.OnKeysChanged;
            this.DeviceTest = new LayoutHost(Path.Combine(Path.GetDirectoryName(keysPath) ?? "", "device-test.yaml"));
            this.DeviceTest.Changed += (_, _) => this.Redraw?.Invoke();
            this.Keys.Reload();
            this.DeviceTest.Reload();
            this.Keys.Start();
            this.DeviceTest.Start();
        }

        public override void Unload()
        {
            this.Keys?.Dispose();
            this.DeviceTest?.Dispose();
        }

        internal SlotView View(SlotId slot) =>
            this.Presenter?.View(slot) ?? new SlotView("loading…", Palette.EmptyColor, false, true, 1, null);

        internal void RunSlot(SlotId slot)
        {
            var steps = this.Presenter?.Press(slot);
            if (steps == null)
            {
                this.Redraw?.Invoke(); // an error was dismissed, or the key is empty
                return;
            }

            var page = slot.PageFor(this.Presenter.CurrentPage);
            this.Runner.Enqueue(steps, $"page {page} key {slot.Key + 1}");
        }

        internal void SetPage(Int32 page)
        {
            var max = this.Keys?.Current?.PageCount ?? KeysLayout.MaxPages;
            this.Presenter.CurrentPage = Math.Clamp(page, 1, max);
            this.SetPluginSetting(PageSetting, this.Presenter.CurrentPage.ToString(), false);
            this.Redraw?.Invoke();
        }

        internal void ReloadNow()
        {
            this.Keys?.Reload();
            this.DeviceTest?.Reload();
        }

        private void OnKeysChanged(Object sender, LayoutChangedEventArgs e)
        {
            if (e.Error == null)
            {
                this._lastNotified = null;
                this.OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, null);
            }
            else
            {
                var text = $"{Path.GetFileName(this.Keys.FilePath)} {e.Error}";
                var hasLayout = this.Keys.Current != null;
                this.OnPluginStatusChanged(hasLayout ? Loupedeck.PluginStatus.Warning : Loupedeck.PluginStatus.Error,
                    hasLayout ? text + " (keeping the last good layout)" : text);
                if (e.ErrorIsNew && this._lastNotified != text)
                {
                    this._lastNotified = text;
                    this.Notify(hasLayout ? "keys.yaml not loaded: kept the last good layout" : "keys.yaml not loaded", text);
                }
            }

            if (e.LayoutSwapped && this.Keys.Current != null && this.Presenter.CurrentPage > this.Keys.Current.PageCount)
            {
                this.Presenter.CurrentPage = this.Keys.Current.PageCount;
            }

            this.Redraw?.Invoke();
        }

        // Windows balloon notification through a hidden PowerShell, like AiScript.ps1 does.
        private void Notify(String title, String message)
        {
            foreach (var shell in new[] { "pwsh.exe", "powershell.exe" })
            {
                try
                {
                    var psi = new ProcessStartInfo(shell, NotificationScript.Arguments(title, message))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    using (Process.Start(psi))
                    {
                    }

                    return;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Notification through {shell} failed");
                }
            }
        }
    }
}
