namespace Loupedeck.LogiCoolJPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;

    using LogiCoolJ.Core;

    // Carries out planned steps with the Logi SDK (keys, text, clipboard) and Windows (programs, files, URLs).
    internal sealed class PluginStepSink : IStepSink
    {
        private readonly LogiCoolJPlugin _plugin;

        public PluginStepSink(LogiCoolJPlugin plugin) => this._plugin = plugin;

        public void SendKey(KeyCombo combo)
        {
            // Names are parsed at runtime so they bind to the PluginApi the service actually loaded.
            var vk = (VirtualKeyCode)Enum.Parse(typeof(VirtualKeyCode), combo.VirtualKey);
            var mods = ToModifierKey(combo.Modifiers);
            var app = this._plugin.ClientApplication;
            if (app != null)
            {
                app.SendKeyboardShortcut(vk, mods);
            }
            else
            {
                this._plugin.Native.SendKeyboardShortcut(vk, mods);
            }
        }

        public void TypeText(String text) => this._plugin.Native.SendString(text);

        public void PasteText(String text)
        {
            var native = this._plugin.Native;
            String previous = null;
            try
            {
                previous = native.ClipboardGetText();
            }
            catch (Exception ex)
            {
                PluginLog.Verbose(ex, "Could not read the clipboard before pasting");
            }

            native.ClipboardSetText(text);
            Thread.Sleep(60);
            this.SendKey(new KeyCombo(KeyModifiers.Ctrl, "KeyV", "V"));
            Thread.Sleep(350);
            if (previous != null)
            {
                try
                {
                    native.ClipboardSetText(previous);
                }
                catch (Exception ex)
                {
                    PluginLog.Verbose(ex, "Could not restore the clipboard");
                }
            }
        }

        public void Launch(LaunchStep step)
        {
            var psi = new ProcessStartInfo
            {
                FileName = step.Target,
                Arguments = step.Arguments ?? "",
                UseShellExecute = true, // programs on PATH or App Paths, folders, documents and URIs
            };
            if (!String.IsNullOrEmpty(step.WorkingDirectory) && Directory.Exists(step.WorkingDirectory))
            {
                psi.WorkingDirectory = step.WorkingDirectory;
            }

            using (Process.Start(psi))
            {
            }
        }

        public void GoToPage(Int32 page) => this._plugin.SetPage(page);

        internal static ModifierKey ToModifierKey(KeyModifiers m)
        {
            UInt64 bits = 0;
            void Add(KeyModifiers flag, String name)
            {
                if (m.HasFlag(flag))
                {
                    bits |= Convert.ToUInt64(Enum.Parse(typeof(ModifierKey), name));
                }
            }

            Add(KeyModifiers.Ctrl, "Control");
            Add(KeyModifiers.Shift, "Shift");
            Add(KeyModifiers.Alt, "Alt");
            Add(KeyModifiers.Win, "Windows");
            return (ModifierKey)Enum.ToObject(typeof(ModifierKey), bits);
        }
    }

    internal static class SlotRenderer
    {
        public static BitmapImage Render(String text, String color, PluginImageSize size)
        {
            if (!Palette.TryParse(color, out var rgb))
            {
                Palette.TryParse(Palette.EmptyColor, out rgb);
            }

            using (var builder = new BitmapBuilder(size))
            {
                builder.Clear(new BitmapColor(rgb.R, rgb.G, rgb.B));
                if (!String.IsNullOrEmpty(text))
                {
                    // No font size: the service picks the best size per device.
                    builder.DrawText(text.Replace("\n", Environment.NewLine));
                }

                return builder.ToImage();
            }
        }

        public static String DisplayName(String text) => String.IsNullOrEmpty(text) ? " " : text.Replace("\n", Environment.NewLine);
    }

    internal static class SlotNames
    {
        public static String DisplayName(SlotId slot) => slot.Kind switch
        {
            SlotKind.FollowPage => $"Key {slot.Key + 1} (follows page)",
            SlotKind.Fixed => $"Page {slot.Page} key {slot.Key + 1}",
            _ => $"Test key {slot.Key + 1}",
        };

        public static String Group(SlotId slot) => slot.Kind switch
        {
            SlotKind.FollowPage => "Keys",
            SlotKind.Fixed => $"Fixed slots###Page {slot.Page}",
            _ => "Device test",
        };
    }
}
