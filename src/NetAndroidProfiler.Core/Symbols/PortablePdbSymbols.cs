using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace NetAndroidProfiler.Core.Symbols;

/// <summary>Source range of one method as recorded in a portable pdb.</summary>
public sealed record MethodSourceRange(string Module, int Token, string Document, int StartLine, int EndLine);

/// <summary>
/// Method -> source mapping from portable pdb files (the .pdb next to each
/// app assembly in the build output). MonoVM gives no IL offsets for samples,
/// so the mapping is per method: metadata token -> document + line range.
/// </summary>
public sealed class PortablePdbSymbols : IDisposable
{
    private readonly Dictionary<string, (MetadataReaderProvider provider, MetadataReader reader)> _pdbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules (assembly names without extension) for which a pdb was loaded.</summary>
    public IReadOnlyCollection<string> Modules => _pdbs.Keys;

    /// <summary>Load every *.pdb in <paramref name="directory"/> (non-portable pdbs are skipped).</summary>
    public static PortablePdbSymbols LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Symbols directory not found: {directory}");
        var s = new PortablePdbSymbols();
        foreach (var file in Directory.EnumerateFiles(directory, "*.pdb"))
            s.TryAdd(file);
        return s;
    }

    /// <summary>Add one pdb file; returns false when it is not a portable pdb.</summary>
    public bool TryAdd(string pdbPath)
    {
        string module = Path.GetFileNameWithoutExtension(pdbPath);
        if (_pdbs.ContainsKey(module)) return true;
        byte[] bytes = File.ReadAllBytes(pdbPath);
        if (bytes.Length < 4 || bytes[0] != (byte)'B' || bytes[1] != (byte)'S' || bytes[2] != (byte)'J' || bytes[3] != (byte)'B')
            return false; // Windows pdb: not supported (DebugType must be portable)
        var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(bytes));
        _pdbs[module] = (provider, provider.GetMetadataReader());
        return true;
    }

    /// <summary>
    /// A module as this looks it up: the assembly name, with no extension. Callers name
    /// modules both ways - a sampling trace says "App.Droid", the weave map records the file
    /// it rewrote, "App.Droid.dll" - and a lookup that missed on the second silently gave a
    /// whole session no source locations at all.
    /// </summary>
    private static string ModuleKey(string module) =>
        module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? module[..^4]
            : module;

    /// <summary>Source range of a method (first to last sequence point of its primary document), or null when unknown.</summary>
    public MethodSourceRange? Find(string module, int token)
    {
        if (!_pdbs.TryGetValue(ModuleKey(module), out var pdb)) return null;
        if ((token >> 24) != 0x06) return null;
        int row = token & 0xFFFFFF;
        var reader = pdb.reader;
        if (row <= 0 || row > reader.MethodDebugInformation.Count) return null;
        var handle = MetadataTokens.MethodDebugInformationHandle(row);
        var info = reader.GetMethodDebugInformation(handle);
        if (info.SequencePointsBlob.IsNil) return null;
        string? doc = null;
        int start = int.MaxValue, end = 0;
        foreach (var sp in info.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            doc ??= reader.GetString(reader.GetDocument(sp.Document).Name);
            if (sp.StartLine < start) start = sp.StartLine;
            if (sp.EndLine > end) end = sp.EndLine;
        }
        if (doc is null || start == int.MaxValue) return null;
        return new MethodSourceRange(module, token, doc, start, end);
    }

    /// <summary>All methods (token + range) whose primary document matches <paramref name="documentSuffix"/> (case-insensitive, path-separator agnostic).</summary>
    public IReadOnlyList<MethodSourceRange> MethodsInDocument(string documentSuffix)
    {
        string needle = Normalize(documentSuffix);
        var list = new List<MethodSourceRange>();
        foreach (var (module, pdb) in _pdbs)
        {
            var reader = pdb.reader;
            foreach (var h in reader.MethodDebugInformation)
            {
                var info = reader.GetMethodDebugInformation(h);
                if (info.SequencePointsBlob.IsNil) continue;
                string? doc = null; int start = int.MaxValue, end = 0;
                foreach (var sp in info.GetSequencePoints())
                {
                    if (sp.IsHidden) continue;
                    doc ??= reader.GetString(reader.GetDocument(sp.Document).Name);
                    if (sp.StartLine < start) start = sp.StartLine;
                    if (sp.EndLine > end) end = sp.EndLine;
                }
                if (doc is null || !Normalize(doc).EndsWith(needle, StringComparison.OrdinalIgnoreCase)) continue;
                int token = MetadataTokens.GetToken(h.ToDefinitionHandle());
                list.Add(new MethodSourceRange(module, token, doc, start, end));
            }
        }
        return list.OrderBy(m => m.StartLine).ToList();
    }

    /// <summary>
    /// The distinct documents whose path ends with <paramref name="documentSuffix"/>. More than
    /// one means the suffix does not identify a file: two projects can each have a Services/LogService.cs.
    /// </summary>
    public IReadOnlyList<string> DocumentsMatching(string documentSuffix)
    {
        string needle = Normalize(documentSuffix);
        return Documents()
            .Where(d => Normalize(d).EndsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Documents known to the loaded pdbs.</summary>
    public IReadOnlyList<string> Documents()
    {
        var docs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, pdb) in _pdbs)
            foreach (var h in pdb.reader.Documents)
                docs.Add(pdb.reader.GetString(pdb.reader.GetDocument(h).Name));
        return docs.OrderBy(d => d).ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        foreach (var (_, pdb) in _pdbs) pdb.provider.Dispose();
        _pdbs.Clear();
    }
}
