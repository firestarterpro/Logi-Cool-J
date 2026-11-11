namespace LogiCoolJ.Core;

public enum Severity { Error, Warning }

/// <summary>One problem in keys.yaml. Line is 1-based (0 = not tied to a line). Page is 1-based, Key 0..8.</summary>
public sealed record LayoutProblem(int Line, int? Page, int? Key, string Message, Severity Severity = Severity.Error)
{
    public bool IsError => this.Severity == Severity.Error;

    public override string ToString() => this.Line > 0 ? $"line {this.Line}: {this.Message}" : this.Message;
}

public sealed class KeysStep
{
    public string Kind { get; init; } = "";
    public string Value { get; init; } = "";
    public int Line { get; init; }
}

public sealed class KeysButton
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string? TextMode { get; init; }
    public List<KeysStep> Steps { get; } = new();
    public int Line { get; init; }

    public bool IsEmpty => this.Steps.Count == 0;
}

public sealed class KeysPage
{
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public KeysButton?[] Buttons { get; } = new KeysButton?[9];
    public int Line { get; init; }
}

/// <summary>A loaded keys.yaml (v2 layout). Pages are 1-based through <see cref="GetPage"/>.</summary>
public sealed class KeysLayout
{
    public const int MaxPages = 10;

    public string ProfileName { get; init; } = "Keypad Layout";
    public string? ProfileId { get; init; }
    public int PageCount { get; init; } = 10;
    public string TextMode { get; init; } = "type";
    public IReadOnlyDictionary<string, string> Paths { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ResolvedPaths { get; set; } = new Dictionary<string, string>();
    public KeysPage[] Pages { get; init; } = Array.Empty<KeysPage>();

    public KeysPage? GetPage(int page) => page >= 1 && page <= this.Pages.Length ? this.Pages[page - 1] : null;

    public KeysButton? GetButton(int page, int key) =>
        key is >= 0 and <= 8 ? this.GetPage(page)?.Buttons[key] : null;
}

/// <summary>Page colours shared with lp5-build\yaml2lp5.py and the builder.</summary>
public static class Palette
{
    public static readonly string[] PageColors =
    {
        "#2D3A4A", "#1F4E8C", "#3C3C8C", "#1E5E3A", "#7A4A12",
        "#6B1F4E", "#4A5A1E", "#2A5A6A", "#5A2A2A", "#444444",
    };

    public const string ErrorColor = "#B3261E";
    public const string EmptyColor = "#1A1A1A";

    public static string ForPage(int page) => PageColors[((page - 1) % PageColors.Length + PageColors.Length) % PageColors.Length];

    public static bool TryParse(string? hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return false;
        }

        try
        {
            rgb = (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
