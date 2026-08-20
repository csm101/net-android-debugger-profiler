using Mono.Cecil;

namespace NetAndroidProfiler.Core.Weaving;

/// <summary>
/// Cecil assembly resolver that satisfies references by pulling
/// <c>&lt;name&gt;.dll</c> from the app's override directory to a local cache on
/// demand, so the weaver can resolve dependencies (e.g. a constant's type in
/// another app assembly) without pulling every deployed assembly up front.
/// </summary>
public sealed class DeviceAssemblyResolver : DefaultAssemblyResolver
{
    private readonly string _cacheDir;
    private readonly Func<string, string, bool> _pull; // (remoteDllName, localPath) -> success
    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="cacheDir">Local directory holding already-pulled assemblies (also a search dir).</param>
    /// <param name="pull">Pulls "&lt;name&gt;.dll" from the device into the given local path; returns false when absent.</param>
    public DeviceAssemblyResolver(string cacheDir, Func<string, string, bool> pull)
    {
        _cacheDir = cacheDir;
        _pull = pull;
        Directory.CreateDirectory(cacheDir);
        AddSearchDirectory(cacheDir);
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        try { return base.Resolve(name); }
        catch (AssemblyResolutionException)
        {
            string dll = name.Name + ".dll";
            string local = Path.Combine(_cacheDir, dll);
            if (!File.Exists(local) && _tried.Add(dll) && _pull(dll, local))
                return base.Resolve(name);
            throw;
        }
    }
}
