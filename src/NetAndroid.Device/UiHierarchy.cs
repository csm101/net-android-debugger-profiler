using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetAndroid.Device;

/// <summary>Screen rectangle of a UI node, in physical pixels (the same space as screenshots and taps).</summary>
public sealed record UiBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
    public override string ToString() => $"[{Left},{Top}][{Right},{Bottom}]";
}

/// <summary>
/// One node of the accessibility hierarchy <c>uiautomator dump</c> reports. Strings are never null:
/// the dump writes an empty attribute for what a view does not have.
/// </summary>
public sealed record UiNode(
    int Depth,
    string ClassName,
    string ResourceId,
    string Text,
    string ContentDescription,
    string Package,
    UiBounds Bounds,
    bool Clickable,
    bool LongClickable,
    bool Enabled,
    bool Focusable,
    bool Focused,
    bool Scrollable,
    bool Checkable,
    bool Checked,
    bool Selected,
    bool Password,
    IReadOnlyList<UiNode> Children)
{
    /// <summary>The id without its <c>package:id/</c> prefix, which is how a layout names it.</summary>
    public string ShortResourceId
    {
        get
        {
            var slash = ResourceId.LastIndexOf('/');
            return slash < 0 ? ResourceId : ResourceId[(slash + 1)..];
        }
    }

    /// <summary>The class without its namespace, e.g. <c>Button</c>.</summary>
    public string ShortClassName
    {
        get
        {
            var dot = ClassName.LastIndexOf('.');
            return dot < 0 ? ClassName : ClassName[(dot + 1)..];
        }
    }

    /// <summary>Whether the node carries anything a person or an agent would look for: text, an id, a description, or an action.</summary>
    public bool IsInteresting =>
        Text.Length > 0 || ResourceId.Length > 0 || ContentDescription.Length > 0 || Clickable || LongClickable || Scrollable || Checkable;
}

/// <summary>The dumped hierarchy: its root and the display rotation the dump was taken at.</summary>
public sealed record UiTree(UiNode Root, int Rotation);

/// <summary>
/// What to look for in a hierarchy. Every given field must match; a null field does not take part.
/// Text and description compare case-insensitively; a resource id given without <c>package:id/</c>
/// matches on the short id; a class name given without namespace matches on the short class.
/// </summary>
public sealed record UiSelector(
    string? Text = null,
    string? TextContains = null,
    string? ResourceId = null,
    string? ContentDescription = null,
    string? ClassName = null,
    string? Package = null,
    bool? Clickable = null,
    bool? Enabled = null)
{
    public bool IsEmpty =>
        Text is null && TextContains is null && ResourceId is null && ContentDescription is null
        && ClassName is null && Package is null && Clickable is null && Enabled is null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Text is not null) parts.Add($"text=\"{Text}\"");
        if (TextContains is not null) parts.Add($"textContains=\"{TextContains}\"");
        if (ResourceId is not null) parts.Add($"resourceId={ResourceId}");
        if (ContentDescription is not null) parts.Add($"contentDescription=\"{ContentDescription}\"");
        if (ClassName is not null) parts.Add($"class={ClassName}");
        if (Package is not null) parts.Add($"package={Package}");
        if (Clickable is not null) parts.Add($"clickable={Clickable}");
        if (Enabled is not null) parts.Add($"enabled={Enabled}");
        return parts.Count == 0 ? "(anything)" : string.Join(' ', parts);
    }
}

/// <summary>Parses and searches the XML that <c>uiautomator dump</c> produces.</summary>
public static class UiHierarchy
{
    private static readonly Regex BoundsPattern = new(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Parses a dump. The text may carry noise before the XML (MIUI's uiautomator prints a stack
    /// trace about a missing theme file first), so parsing starts at the XML declaration or the
    /// root element, whichever comes first.
    /// </summary>
    public static UiTree Parse(string dump)
    {
        var start = IndexOfXml(dump);
        if (start < 0)
            throw new DeviceControlException("no <hierarchy> in the uiautomator output: " + Excerpt(dump));

        XDocument doc;
        try { doc = XDocument.Parse(dump[start..]); }
        catch (System.Xml.XmlException ex) { throw new DeviceControlException("uiautomator output is not well-formed XML: " + ex.Message, ex); }

        var hierarchy = doc.Root ?? throw new DeviceControlException("empty uiautomator output");
        if (hierarchy.Name.LocalName != "hierarchy")
            throw new DeviceControlException($"unexpected root element <{hierarchy.Name.LocalName}> in the uiautomator output");

        var rotation = int.TryParse((string?)hierarchy.Attribute("rotation"), out var r) ? r : 0;
        var children = hierarchy.Elements("node").Select(e => ParseNode(e, 0)).ToList();
        var root = children.Count == 1
            ? children[0]
            // Several top-level windows (a dialog over the activity, an IME): wrap them so callers
            // always get one tree.
            : new UiNode(0, "hierarchy", "", "", "", "", ScreenBoundsOf(children), false, false, true, false, false, false, false, false, false, false, children);
        return new UiTree(root, rotation);
    }

    /// <summary>Depth-first, parents before children.</summary>
    public static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var n in Flatten(child))
                yield return n;
    }

    public static IReadOnlyList<UiNode> Find(UiNode root, UiSelector selector)
        => Flatten(root).Where(n => Matches(n, selector)).ToList();

    public static bool Matches(UiNode node, UiSelector s)
    {
        if (s.Text is not null && !string.Equals(node.Text, s.Text, StringComparison.OrdinalIgnoreCase)) return false;
        if (s.TextContains is not null && !node.Text.Contains(s.TextContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (s.ContentDescription is not null && !string.Equals(node.ContentDescription, s.ContentDescription, StringComparison.OrdinalIgnoreCase)) return false;
        if (s.ResourceId is not null && !ResourceIdMatches(node, s.ResourceId)) return false;
        if (s.ClassName is not null && !ClassNameMatches(node, s.ClassName)) return false;
        if (s.Package is not null && !string.Equals(node.Package, s.Package, StringComparison.Ordinal)) return false;
        if (s.Clickable is not null && node.Clickable != s.Clickable) return false;
        if (s.Enabled is not null && node.Enabled != s.Enabled) return false;
        return true;
    }

    private static bool ResourceIdMatches(UiNode node, string wanted)
        => wanted.Contains('/')
            ? string.Equals(node.ResourceId, wanted, StringComparison.Ordinal)
            : string.Equals(node.ShortResourceId, wanted, StringComparison.Ordinal);

    private static bool ClassNameMatches(UiNode node, string wanted)
        => wanted.Contains('.')
            ? string.Equals(node.ClassName, wanted, StringComparison.Ordinal)
            : string.Equals(node.ShortClassName, wanted, StringComparison.Ordinal);

    private static UiNode ParseNode(XElement e, int depth)
    {
        string Attr(string name) => (string?)e.Attribute(name) ?? "";
        bool Flag(string name) => string.Equals(Attr(name), "true", StringComparison.OrdinalIgnoreCase);
        var children = e.Elements("node").Select(c => ParseNode(c, depth + 1)).ToList();
        return new UiNode(
            depth,
            Attr("class"),
            Attr("resource-id"),
            Attr("text"),
            Attr("content-desc"),
            Attr("package"),
            ParseBounds(Attr("bounds")),
            Flag("clickable"),
            Flag("long-clickable"),
            Flag("enabled"),
            Flag("focusable"),
            Flag("focused"),
            Flag("scrollable"),
            Flag("checkable"),
            Flag("checked"),
            Flag("selected"),
            Flag("password"),
            children);
    }

    private static UiBounds ParseBounds(string text)
    {
        var m = BoundsPattern.Match(text);
        if (!m.Success) return new UiBounds(0, 0, 0, 0);
        return new UiBounds(
            int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
    }

    private static UiBounds ScreenBoundsOf(IReadOnlyList<UiNode> windows)
    {
        if (windows.Count == 0) return new UiBounds(0, 0, 0, 0);
        return new UiBounds(
            windows.Min(w => w.Bounds.Left), windows.Min(w => w.Bounds.Top),
            windows.Max(w => w.Bounds.Right), windows.Max(w => w.Bounds.Bottom));
    }

    private static int IndexOfXml(string dump)
    {
        var declaration = dump.IndexOf("<?xml", StringComparison.Ordinal);
        var root = dump.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (declaration < 0) return root;
        if (root < 0) return declaration;
        return Math.Min(declaration, root);
    }

    private static string Excerpt(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "...";
    }
}
