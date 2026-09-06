namespace NetAndroidProfiler.Core.Weaving;

/// <summary>
/// Method selector for the weaver, using the same grammar as the Mono
/// callspec so users learn one syntax: comma-separated entries, each
/// optionally prefixed with '-' (exclude), of the forms
/// <c>all</c>, <c>N:Namespace</c> (prefix match), <c>T:Full.Type.Name</c>,
/// <c>M:Full.Type.Name:Method</c>. Excludes win over includes.
/// </summary>
public sealed class WeaveFilter
{
    private readonly List<(bool exclude, char kind, string a, string? b)> _entries = new();

    public static WeaveFilter Parse(string spec)
    {
        var f = new WeaveFilter();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string s = raw;
            bool exclude = false;
            if (s.StartsWith('-') || s.StartsWith('+')) { exclude = s[0] == '-'; s = s[1..]; }
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) { f._entries.Add((exclude, 'A', "", null)); continue; }
            if (s.Length > 2 && s[1] == ':')
            {
                char kind = char.ToUpperInvariant(s[0]);
                string rest = s[2..];
                switch (kind)
                {
                    case 'N': f._entries.Add((exclude, 'N', rest, null)); continue;
                    case 'T': f._entries.Add((exclude, 'T', rest, null)); continue;
                    case 'M':
                        int colon = rest.LastIndexOf(':');
                        if (colon <= 0) throw new FormatException($"Invalid M: entry '{raw}' (expected M:Full.Type:Method)");
                        f._entries.Add((exclude, 'M', rest[..colon], rest[(colon + 1)..]));
                        continue;
                }
            }
            throw new FormatException($"Invalid filter entry '{raw}' (use all, N:Namespace, T:Full.Type, M:Full.Type:Method, each optionally prefixed with '-')");
        }
        if (f._entries.Count == 0) throw new FormatException("Empty weave filter");
        return f;
    }

    /// <summary>True when the method identified by namespace/type/method matches (excludes win).</summary>
    public bool Matches(string ns, string typeFullName, string methodName)
    {
        bool included = false;
        foreach (var (exclude, kind, a, b) in _entries)
        {
            bool hit = kind switch
            {
                'A' => true,
                'N' => ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal),
                'T' => typeFullName == a,
                'M' => typeFullName == a && methodName == b,
                _ => false,
            };
            if (!hit) continue;
            if (exclude) return false;
            included = true;
        }
        return included;
    }
}
