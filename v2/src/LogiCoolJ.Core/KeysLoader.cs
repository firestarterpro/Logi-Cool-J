using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LogiCoolJ.Core;

public sealed class LoadResult
{
    public KeysLayout? Layout { get; init; }
    public IReadOnlyList<LayoutProblem> Problems { get; init; } = Array.Empty<LayoutProblem>();

    /// <summary>True when there is a layout and no error (warnings are fine).</summary>
    public bool IsValid => this.Layout != null && !this.Problems.Any(p => p.IsError);

    public LayoutProblem? FirstError => this.Problems.FirstOrDefault(p => p.IsError);
}

/// <summary>
/// Reads keys.yaml into a <see cref="KeysLayout"/> and checks it against the same rules as
/// lp5-build\yaml2lp5.py (check_layout), with a line number on every problem.
/// The plugin only swaps in a layout when <see cref="LoadResult.IsValid"/> is true.
/// </summary>
public static class KeysLoader
{
    public static readonly string[] StepKinds = { "key", "text", "wait", "url", "open", "run", "go_to_page" };
    private static readonly string[] ExpandedKinds = { "text", "url", "open", "run" };
    public const int MaxWaitMs = 600_000;

    public static LoadResult Load(string text)
    {
        var problems = new List<LayoutProblem>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text ?? ""));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode m)
            {
                return Fail(1, "The file is not a YAML mapping (expected profile_name:, page_1: …).");
            }

            root = m;
        }
        catch (YamlException e)
        {
            var reason = (e.InnerException?.Message ?? e.Message).Split('\n')[0];
            return Fail((int)e.Start.Line, "YAML syntax: " + Trim(reason, 90));
        }
        catch (ArgumentException e)
        {
            // e.g. the same key twice in one mapping
            return Fail(0, "YAML: " + Trim(e.Message.Split('\n')[0], 90));
        }

        string? Scalar(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s ? s.Value : null;
        YamlNode? Node(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var n) ? n : null;

        // Top level.
        var pageCount = 10;
        var countNode = Node(root, "profile_page_count");
        if (countNode != null)
        {
            if (countNode is not YamlScalarNode cs || !int.TryParse(cs.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pageCount))
            {
                problems.Add(new LayoutProblem(Line(countNode), null, null, "profile_page_count must be a whole number."));
                pageCount = 10;
            }
            else if (pageCount < 1 || pageCount > KeysLayout.MaxPages)
            {
                problems.Add(new LayoutProblem(Line(countNode), null, null, $"profile_page_count must be 1..{KeysLayout.MaxPages} (the plugin has slots for {KeysLayout.MaxPages} pages)."));
                pageCount = Math.Clamp(pageCount, 1, KeysLayout.MaxPages);
            }
        }

        var textMode = Scalar(root, "text_mode") ?? "type";
        if (textMode is not ("type" or "paste"))
        {
            problems.Add(new LayoutProblem(Line(Node(root, "text_mode")), null, null, "text_mode must be type or paste."));
            textMode = "type";
        }

        // paths:
        var rawPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathLines = new Dictionary<string, int>(StringComparer.Ordinal);
        var pathsNode = Node(root, "paths");
        if (pathsNode is YamlMappingNode pm)
        {
            foreach (var (k, v) in pm.Children)
            {
                var name = (k as YamlScalarNode)?.Value ?? "";
                pathLines[name] = Line(k);
                if (v is not YamlScalarNode vs || string.IsNullOrWhiteSpace(vs.Value))
                {
                    problems.Add(new LayoutProblem(Line(k), null, null, $"paths: {name} needs a text value."));
                    continue;
                }

                rawPaths[name] = vs.Value!;
            }
        }
        else if (pathsNode != null && !(pathsNode is YamlScalarNode ns && string.IsNullOrEmpty(ns.Value)))
        {
            problems.Add(new LayoutProblem(Line(pathsNode), null, null, "paths must be a mapping of name: value."));
        }

        var resolved = PathVariables.Resolve(rawPaths, pathLines, problems);
        foreach (var (name, value) in resolved)
        {
            if (SecretPatterns.LooksSecret(value))
            {
                problems.Add(new LayoutProblem(pathLines.GetValueOrDefault(name), null, null, $"paths: {name} looks like an API key or password; refused."));
            }
        }

        // Pages.
        var pages = new KeysPage[pageCount];
        for (var p = 1; p <= pageCount; p++)
        {
            var pageNode = Node(root, $"page_{p}");
            if (pageNode is not YamlMappingNode pmap)
            {
                if (pageNode != null && !(pageNode is YamlScalarNode e && string.IsNullOrEmpty(e.Value)))
                {
                    problems.Add(new LayoutProblem(Line(pageNode), p, null, $"page_{p} must be a mapping with name and buttons."));
                }

                pages[p - 1] = new KeysPage { Number = p, Name = $"Page {p}", Line = Line(pageNode) };
                continue;
            }

            var page = new KeysPage { Number = p, Name = Scalar(pmap, "name") is { Length: > 0 } nm ? nm : $"Page {p}", Line = Line(pmap) };
            pages[p - 1] = page;
            var buttonsNode = Node(pmap, "buttons");
            if (buttonsNode == null || (buttonsNode is YamlScalarNode bs && string.IsNullOrEmpty(bs.Value)))
            {
                continue;
            }

            if (buttonsNode is not YamlSequenceNode buttons)
            {
                problems.Add(new LayoutProblem(Line(buttonsNode), p, null, $"page_{p}: buttons must be a list."));
                continue;
            }

            foreach (var bn in buttons.Children)
            {
                if (bn is not YamlMappingNode bmap)
                {
                    problems.Add(new LayoutProblem(Line(bn), p, null, $"Page {p}: each button must be a mapping."));
                    continue;
                }

                LoadButton(bmap, p, pageCount, page, resolved, textMode, problems, Scalar, Node);
            }
        }

        var layout = new KeysLayout
        {
            ProfileName = Scalar(root, "profile_name") is { Length: > 0 } pn ? pn : "Keypad Layout",
            ProfileId = Scalar(root, "profile_id"),
            PageCount = pageCount,
            TextMode = textMode,
            Paths = rawPaths,
            ResolvedPaths = resolved,
            Pages = pages,
        };
        return new LoadResult { Layout = layout, Problems = problems };

        LoadResult Fail(int line, string message) =>
            new() { Layout = null, Problems = new[] { new LayoutProblem(line, null, null, message) } };
    }

    private static void LoadButton(
        YamlMappingNode bmap, int p, int pageCount, KeysPage page, IReadOnlyDictionary<string, string> resolved, string textMode,
        List<LayoutProblem> problems, Func<YamlMappingNode, string, string?> scalar, Func<YamlMappingNode, string, YamlNode?> node)
    {
        var idText = scalar(bmap, "id");
        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0 || id > 8)
        {
            problems.Add(new LayoutProblem(Line(bmap), p, null, $"Page {p}: a button needs id 0..8."));
            return;
        }

        if (page.Buttons[id] != null)
        {
            problems.Add(new LayoutProblem(Line(bmap), p, id, $"Page {p} button {id}: id used twice on this page."));
            return;
        }

        var where = $"Page {p} button {id}";
        var color = scalar(bmap, "color");
        if (color != null && !Palette.TryParse(color, out _))
        {
            problems.Add(new LayoutProblem(Line(node(bmap, "color")), p, id, $"{where}: color must look like #1F4E8C."));
            color = null;
        }

        var mode = scalar(bmap, "text_mode");
        if (mode != null && mode is not ("type" or "paste"))
        {
            problems.Add(new LayoutProblem(Line(node(bmap, "text_mode")), p, id, $"{where}: text_mode must be type or paste."));
            mode = null;
        }

        var label = scalar(bmap, "label") ?? "";
        var button = new KeysButton { Id = id, Label = label, Icon = scalar(bmap, "icon"), Color = color, TextMode = mode, Line = Line(bmap) };
        page.Buttons[id] = button;
        if (label.Length > 12)
        {
            problems.Add(new LayoutProblem(Line(bmap), p, id, $"{where}: label is over 12 characters and may wrap.", Severity.Warning));
        }

        var stepsNode = node(bmap, "steps");
        if (stepsNode == null || (stepsNode is YamlScalarNode es && string.IsNullOrEmpty(es.Value)))
        {
            return;
        }

        if (stepsNode is not YamlSequenceNode steps)
        {
            problems.Add(new LayoutProblem(Line(stepsNode), p, id, $"{where}: steps must be a list."));
            return;
        }

        var si = 0;
        foreach (var sn in steps.Children)
        {
            si++;
            var line = Line(sn);
            if (sn is not YamlMappingNode smap || smap.Children.Count != 1)
            {
                problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: needs exactly one of {string.Join(", ", StepKinds)}."));
                continue;
            }

            var (k, v) = smap.Children.First();
            var kind = (k as YamlScalarNode)?.Value ?? "";
            if (!StepKinds.Contains(kind))
            {
                problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: unknown step '{Trim(kind, 20)}'."));
                continue;
            }

            if (v is not YamlScalarNode vs || string.IsNullOrWhiteSpace(vs.Value))
            {
                problems.Add(new LayoutProblem(line, p, id, $"{where} step {si} ({kind}): value is empty."));
                continue;
            }

            var value = vs.Value!;
            var ok = true;
            if (kind is "wait" or "go_to_page")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: {kind} must be a whole number."));
                    ok = false;
                }
                else if (kind == "wait" && (n < 0 || n > MaxWaitMs))
                {
                    problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: wait must be 0..{MaxWaitMs} ms."));
                    ok = false;
                }
                else if (kind == "go_to_page")
                {
                    if (n < 1 || n > pageCount)
                    {
                        problems.Add(new LayoutProblem(line, p, id, $"{where}: go_to_page {n} is out of range 1..{pageCount}."));
                        ok = false;
                    }

                    if (steps.Children.Count > 1)
                    {
                        problems.Add(new LayoutProblem(line, p, id, $"{where}: go_to_page must be the button's only step."));
                        ok = false;
                    }
                }
            }
            else if (kind == "key" && KeyCombo.TryParse(value, out var why) is null)
            {
                problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: {why}."));
                ok = false;
            }

            var expanded = value;
            if (ExpandedKinds.Contains(kind))
            {
                foreach (var name in PathVariables.References(value).Distinct())
                {
                    if (!resolved.ContainsKey(name))
                    {
                        problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: ${{{Trim(name, 30)}}} is not in paths."));
                        ok = false;
                    }
                }

                expanded = PathVariables.Expand(value, resolved);
            }

            if (SecretPatterns.LooksSecret(value) || SecretPatterns.LooksSecret(expanded))
            {
                // Never echo the value.
                problems.Add(new LayoutProblem(line, p, id, $"{where} step {si}: looks like an API key or password; refused."));
                ok = false;
            }

            if (ok)
            {
                button.Steps.Add(new KeysStep { Kind = kind, Value = value, Line = line });
            }
        }
    }

    private static int Line(YamlNode? n) => n == null ? 0 : (int)n.Start.Line;

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
