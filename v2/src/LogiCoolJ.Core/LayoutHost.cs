using System.Text;

namespace LogiCoolJ.Core;

public sealed class LayoutChangedEventArgs : EventArgs
{
    public bool LayoutSwapped { get; init; }
    public LayoutProblem? Error { get; init; }
    public bool ErrorIsNew { get; init; }
}

/// <summary>
/// Owns one keys file. Watches it, and on every change parses and checks the new text:
/// a valid file replaces the layout; a bad file keeps the last good layout and reports its first error.
/// </summary>
public sealed class LayoutHost : IDisposable
{
    private readonly object _gate = new();
    private readonly object _reloadGate = new();
    private readonly int _debounceMs;
    private readonly int _pollMs;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _poll;
    private string? _lastText;
    private (long Length, DateTime Written) _lastStamp;
    private bool _disposed;

    public LayoutHost(string path, int debounceMs = 400, int pollMs = 2000)
    {
        this.FilePath = Path.GetFullPath(path);
        this._debounceMs = debounceMs;
        this._pollMs = pollMs;
    }

    public string FilePath { get; }

    /// <summary>The last layout that passed every check (null until the first good read).</summary>
    public KeysLayout? Current { get; private set; }

    /// <summary>Problems of the most recent read, errors and warnings.</summary>
    public IReadOnlyList<LayoutProblem> Problems { get; private set; } = Array.Empty<LayoutProblem>();

    public LayoutProblem? Error => this.Problems.FirstOrDefault(p => p.IsError);

    public event EventHandler<LayoutChangedEventArgs>? Changed;

    /// <summary>Parses text as the new file content. Swaps the layout only if it is valid.</summary>
    public LoadResult ApplyText(string text)
    {
        LoadResult result;
        LayoutChangedEventArgs args;
        lock (this._reloadGate)
        {
            (result, args) = this.ApplyTextCore(text);
        }

        this.Changed?.Invoke(this, args);
        return result;
    }

    private (LoadResult Result, LayoutChangedEventArgs Args) ApplyTextCore(string text)
    {
        var result = KeysLoader.Load(text);
        lock (this._gate)
        {
            var oldError = this.Error;
            this._lastText = text;
            this.Problems = result.Problems;
            var swapped = false;
            if (result.IsValid)
            {
                this.Current = result.Layout;
                swapped = true;
            }

            var error = this.Error;
            var args = new LayoutChangedEventArgs
            {
                LayoutSwapped = swapped,
                Error = error,
                ErrorIsNew = error != null && (oldError == null || oldError.ToString() != error.ToString()),
            };
            return (result, args);
        }
    }

    /// <summary>Reads the file (retrying while an editor still holds it) and applies it.</summary>
    public LoadResult Reload() => this.ReloadCore(onlyIfChanged: false);

    private LoadResult ReloadCore(bool onlyIfChanged)
    {
        LayoutChangedEventArgs? notifyArgs = null;
        LoadResult? result = null;
        lock (this._reloadGate)
        {
            try
            {
                var stamp = this.CurrentStamp();
                if (onlyIfChanged && stamp.Length >= 0)
                {
                    var text = ReadShared(this.FilePath);
                    lock (this._gate)
                    {
                        this._lastStamp = stamp;
                        if (text == this._lastText)
                        {
                            return new LoadResult { Layout = this.Current, Problems = this.Problems };
                        }
                    }

                    (result, notifyArgs) = this.ApplyTextCore(text);
                }
                else
                {
                    var text = ReadShared(this.FilePath);
                    lock (this._gate)
                    {
                        this._lastStamp = this.CurrentStamp();
                    }

                    (result, notifyArgs) = this.ApplyTextCore(text);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var missing = e is FileNotFoundException or DirectoryNotFoundException;
                if (onlyIfChanged && !missing)
                {
                    this.Kick(); // still being written: try again shortly
                    return new LoadResult { Layout = this.Current, Problems = this.Problems };
                }

                var problem = new LayoutProblem(0, null, null, missing ? $"{Path.GetFileName(this.FilePath)} not found at {Path.GetDirectoryName(this.FilePath)}" : $"cannot read {Path.GetFileName(this.FilePath)}: {e.Message}");
                lock (this._gate)
                {
                    var isNew = this.Error?.ToString() != problem.ToString();
                    this.Problems = new[] { problem };
                    this._lastText = null;
                    this._lastStamp = (-1L, DateTime.MinValue);
                    notifyArgs = new LayoutChangedEventArgs { LayoutSwapped = false, Error = problem, ErrorIsNew = isNew };
                    result = new LoadResult { Layout = null, Problems = this.Problems };
                }
            }
        }

        if (notifyArgs != null)
        {
            this.Changed?.Invoke(this, notifyArgs);
        }

        return result!;
    }

    /// <summary>Starts watching: a FileSystemWatcher plus a slow poll in case an event is missed.</summary>
    public void Start()
    {
        this._watcher?.Dispose();
        this._debounce?.Dispose();
        this._poll?.Dispose();

        var dir = Path.GetDirectoryName(this.FilePath)!;
        this._debounce = new Timer(_ => this.ReloadIfChanged(), null, Timeout.Infinite, Timeout.Infinite);
        if (Directory.Exists(dir))
        {
            this._watcher = new FileSystemWatcher(dir, Path.GetFileName(this.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler kick = (_, _) => this.Kick();
            this._watcher.Changed += kick;
            this._watcher.Created += kick;
            this._watcher.Deleted += kick;
            this._watcher.Renamed += (_, _) => this.Kick();
            this._watcher.EnableRaisingEvents = true;
        }

        this._poll = new Timer(_ => this.PollOnce(), null, this._pollMs, this._pollMs);
    }

    private void Kick()
    {
        if (!this._disposed)
        {
            this._debounce?.Change(this._debounceMs, Timeout.Infinite);
        }
    }

    private void PollOnce()
    {
        try
        {
            var stamp = this.CurrentStamp();
            (long Length, DateTime Written) last;
            lock (this._gate)
            {
                last = this._lastStamp;
            }

            if (stamp != last)
            {
                this.Kick();
            }
        }
        catch (IOException)
        {
        }
    }

    private void ReloadIfChanged()
    {
        if (this._disposed)
        {
            return;
        }

        this.ReloadCore(onlyIfChanged: true);
    }

    private (long Length, DateTime Written) CurrentStamp()
    {
        var fi = new FileInfo(this.FilePath);
        return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1L, DateTime.MinValue);
    }

    private static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(80);
            }
        }
    }

    public void Dispose()
    {
        this._disposed = true;
        this._watcher?.Dispose();
        this._debounce?.Dispose();
        this._poll?.Dispose();
    }
}

/// <summary>Finds keys.yaml for the plugin: LOGICOOLJ_KEYS, then keys-path.txt beside the plugin, then parent folders.</summary>
public static class KeysLocator
{
    public const string EnvironmentVariable = "LOGICOOLJ_KEYS";
    public const string PointerFile = "keys-path.txt";

    public static string? Find(string pluginDirectory, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var env = getEnv(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim().Trim('"');
        }

        var pointer = Path.Combine(pluginDirectory, PointerFile);
        if (File.Exists(pointer))
        {
            var line = File.ReadLines(pointer).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(line))
            {
                return line;
            }
        }

        var dir = new DirectoryInfo(pluginDirectory);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "keys.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>PowerShell for a Windows balloon notification (the same NotifyIcon approach AiScript.ps1 uses).</summary>
public static class NotificationScript
{
    public static string Build(string title, string message)
    {
        static string Q(string s, int max) => (s.Length > max ? s[..max] + "…" : s).Replace("'", "''").Replace("\r", " ").Replace("\n", " ");
        return "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; " +
               "$n = New-Object System.Windows.Forms.NotifyIcon; $n.Icon = [System.Drawing.SystemIcons]::Warning; $n.Visible = $true; " +
               $"$n.ShowBalloonTip(8000, '{Q(title, 60)}', '{Q(message, 240)}', 'Warning'); Start-Sleep -Seconds 9; $n.Dispose()";
    }

    /// <summary>Arguments for pwsh/powershell -EncodedCommand (no quoting problems).</summary>
    public static string Arguments(string title, string message) =>
        "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Build(title, message)));
}
