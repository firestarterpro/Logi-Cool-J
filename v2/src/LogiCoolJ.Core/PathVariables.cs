using System.Text.RegularExpressions;

namespace LogiCoolJ.Core;

/// <summary>The paths: block of keys.yaml: ${name} variables that may refer to each other.</summary>
public static class PathVariables
{
    public static readonly Regex Reference = new(@"\$\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex ValidName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Resolves every variable. Problems name the variable; nothing is echoed beyond names.</summary>
    public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> raw, IReadOnlyDictionary<string, int>? lines, List<LayoutProblem> problems)
    {
        var done = new Dictionary<string, string>(StringComparer.Ordinal);
        var bad = new HashSet<string>(StringComparer.Ordinal);
        int LineOf(string n) => lines != null && lines.TryGetValue(n, out var l) ? l : 0;

        foreach (var name in raw.Keys)
        {
            if (!ValidName.IsMatch(name))
            {
                problems.Add(new LayoutProblem(LineOf(name), null, null, $"paths: '{name}' is not a valid name (letters, digits, _)."));
                bad.Add(name);
            }
        }

        string Solve(string name, List<string> stack)
        {
            if (done.TryGetValue(name, out var v))
            {
                return v;
            }

            if (stack.Contains(name))
            {
                throw new InvalidOperationException($"paths: {string.Join(" -> ", stack.Append(name))} refers to itself.");
            }

            if (!raw.TryGetValue(name, out var value) || bad.Contains(name))
            {
                throw new KeyNotFoundException(name);
            }

            stack.Add(name);
            var result = Reference.Replace(value, m => Solve(m.Groups[1].Value, stack));
            stack.RemoveAt(stack.Count - 1);
            done[name] = result;
            return result;
        }

        foreach (var name in raw.Keys.Where(n => !bad.Contains(n)))
        {
            try
            {
                Solve(name, new List<string>());
            }
            catch (InvalidOperationException e)
            {
                if (!problems.Any(p => p.Message == e.Message))
                {
                    problems.Add(new LayoutProblem(LineOf(name), null, null, e.Message));
                }
            }
            catch (KeyNotFoundException e)
            {
                problems.Add(new LayoutProblem(LineOf(name), null, null, $"paths: {name} uses ${{{e.Message}}}, which is not defined."));
            }
        }

        return done;
    }

    /// <summary>Names used as ${name} in a value.</summary>
    public static IEnumerable<string> References(string value) =>
        Reference.Matches(value).Select(m => m.Groups[1].Value);

    /// <summary>Replaces ${name} with resolved values; unknown names stay as written.</summary>
    public static string Expand(string value, IReadOnlyDictionary<string, string> resolved) =>
        Reference.Replace(value, m => resolved.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}

/// <summary>Values that look like credentials. Same list as yaml2lp5.py and the builder (HANDOUT rule 4).</summary>
public static class SecretPatterns
{
    private static readonly Regex[] Patterns =
    {
        new(@"\bsk-[a-zA-Z0-9_-]{16,}\b"),
        new(@"\bgsk_[a-zA-Z0-9]{20,}\b"),
        new(@"\bgithub_pat_[a-zA-Z0-9_]{20,}\b"),
        new(@"\bhf_[a-zA-Z0-9]{20,}\b"),
        new(@"\bAKIA[0-9A-Z]{16}\b"),
        new(@"\bghp_[a-zA-Z0-9]{20,}\b"),
        new(@"\bAIza[0-9A-Za-z_-]{30,}\b"),
        new(@"\bxox[baprs]-[0-9a-zA-Z-]{10,}\b"),
        new(@"password\s*[:=]\s*\S+", RegexOptions.IgnoreCase),
        new(@"api[_-]?key\s*[:=]\s*\S+", RegexOptions.IgnoreCase),
    };

    public static bool LooksSecret(string? value) => value != null && Patterns.Any(p => p.IsMatch(value));
}
