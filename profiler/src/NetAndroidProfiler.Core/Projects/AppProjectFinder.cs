using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mono.Cecil;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Core.Projects;

/// <summary>
/// What a solution or folder holds that can be profiled, read from the project files
/// themselves. A frontend that knows the sources should not ask the user to retype what
/// the <c>.csproj</c> already says: the package name, the build output where the pdbs
/// live, and which assemblies are the application's own (the ones worth weaving).
/// <para>
/// Targeting an <c>-android</c> framework is not the test - libraries do too, and in a
/// real product most of them are libraries (the reference application: thirty-odd projects target Android,
/// two are applications). An application declares an <c>ApplicationId</c>, or is an
/// <c>Exe</c>, or carries an <c>AndroidManifest.xml</c> with a package.
/// </para>
/// </summary>
public static class AppProjectFinder
{
    /// <summary>Folders that hold no project worth profiling and are expensive to walk.</summary>
    private static readonly string[] SkippedFolders =
        ["bin", "obj", "node_modules", "packages", "TestResults", "artifacts"];

    /// <summary>
    /// Every profilable application project at <paramref name="solutionOrFolder"/>, which may
    /// be a solution (<c>.sln</c>/<c>.slnx</c>), a single <c>.csproj</c>, or a folder to walk.
    /// </summary>
    /// <param name="solutionOrFolder">Solution, project or folder to look at.</param>
    /// <param name="configuration">Build configuration whose output directory is reported.</param>
    public static IReadOnlyList<AppProjectInfo> Find(string solutionOrFolder, string configuration = "Debug")
    {
        if (string.IsNullOrWhiteSpace(solutionOrFolder))
            throw new ProfilerException("A solution, project or folder is required.");
        string full = Path.GetFullPath(solutionOrFolder);
        if (string.IsNullOrWhiteSpace(configuration)) configuration = "Debug";

        if (File.Exists(full))
        {
            string extension = Path.GetExtension(full);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return Describe(full, configuration, inSolution: false) is { } single ? [single] : [];

            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                return ProjectsOf(full)
                    .Select(p => Describe(p, configuration, inSolution: true))
                    .OfType<AppProjectInfo>()
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            throw new ProfilerException($"{full} is neither a solution nor a .csproj.");
        }

        if (!Directory.Exists(full))
            throw new ProfilerException($"No such solution, project or folder: {full}");

        // Walking a folder finds projects that merely sit next to the product as well as
        // those in it, so mark the ones a solution here names: that is the difference
        // between "our app" and "a sample someone left in the tree".
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in Directory.EnumerateFiles(full, "*.sln").Concat(Directory.EnumerateFiles(full, "*.slnx")))
        {
            try { named.UnionWith(ProjectsOf(solution)); }
            catch (ProfilerException) { /* an unreadable solution just marks nothing */ }
        }

        return EnumerateProjects(full)
            .Select(p => Describe(p, configuration, named.Contains(p)))
            .OfType<AppProjectInfo>()
            .OrderByDescending(p => p.InSolution)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a project file and returns it only if it is an Android application. Anything
    /// unreadable is simply not a candidate: a folder walk meets generated and broken
    /// project files, and none of them should stop the listing.
    /// </summary>
    public static AppProjectInfo? Describe(string projectPath, string configuration = "Debug", bool inSolution = false)
    {
        XDocument doc;
        try { doc = XDocument.Load(projectPath); }
        catch (Exception) { return null; }

        string? Property(string name)
        {
            string? value = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        string frameworks = Property("TargetFramework") ?? Property("TargetFrameworks") ?? "";
        string? android = frameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(f => f.Contains("-android", StringComparison.OrdinalIgnoreCase));
        if (android is null) return null;

        string full = Path.GetFullPath(projectPath);
        string directory = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);
        string? applicationId = Property("ApplicationId") ?? ManifestPackage(directory);
        bool isExecutable = string.Equals(Property("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase);
        if (applicationId is null && !isExecutable) return null;

        string outputDir = Path.Combine(directory, "bin", configuration, android);
        return new AppProjectInfo(
            full,
            name,
            applicationId,
            android,
            Property("AssemblyName") ?? name,
            configuration,
            outputDir,
            Directory.Exists(outputDir),
            AssembliesOf(full),
            Flag(doc, "EnableDiagnostics"),
            Flag(doc, "EmbedAssembliesIntoApk"),
            inSolution);
    }

    /// <summary>
    /// Namespaces and types of the application's own assemblies, so a frontend can offer a
    /// callspec instead of asking for one. Read from the build output: an app that was
    /// never built has nothing to offer, which is the honest answer.
    /// </summary>
    /// <param name="outputDir">Build output holding the assemblies (bin/&lt;Configuration&gt;/&lt;tfm&gt;).</param>
    /// <param name="assemblies">Assembly names to read, without extension.</param>
    /// <param name="max">Cap on the returned entries; the largest types win.</param>
    public static IReadOnlyList<CallspecCandidate> Candidates(
        string outputDir, IReadOnlyList<string> assemblies, int max = 400)
    {
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir)) return [];

        // Per candidate, which assemblies hold it: a caller that weaves needs the assembly,
        // not only the callspec, and a namespace can be split across two of them.
        var namespaces = new Dictionary<string, (int Methods, List<string> In)>(StringComparer.Ordinal);
        var types = new Dictionary<string, (int Methods, List<string> In)>(StringComparer.Ordinal);
        foreach (var assembly in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.Combine(outputDir, assembly + ".dll");
            if (!File.Exists(file)) continue;
            ModuleDefinition module;
            try { module = ModuleDefinition.ReadModule(file); }
            catch (Exception) { continue; }        // not managed, or being written right now
            using (module)
            {
                foreach (var type in module.GetTypes())
                {
                    // Compiler-generated types (state machines, closures) are not something
                    // anyone types into a callspec, and they would drown the real ones.
                    if (type.Name.StartsWith('<') || type.Namespace.Contains('<')) continue;
                    int methods = type.Methods.Count(m => m.HasBody);
                    if (methods == 0) continue;
                    if (!string.IsNullOrEmpty(type.Namespace))
                        Add(namespaces, type.Namespace, methods, assembly);
                    // Cecil's FullName verbatim, nested types included: this is the string
                    // the weave filter compares a T: callspec against (CecilWeaver).
                    Add(types, type.FullName, methods, assembly);
                }
            }
        }

        var result = namespaces
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => new CallspecCandidate("N:" + n.Key, "namespace", n.Value.Methods, string.Join(", ", n.Value.In)))
            .ToList();
        result.AddRange(types
            .OrderByDescending(t => t.Value.Methods)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, max - result.Count))
            .Select(t => new CallspecCandidate("T:" + t.Key, "type", t.Value.Methods, string.Join(", ", t.Value.In))));
        return result;
    }

    private static void Add(Dictionary<string, (int Methods, List<string> In)> to, string key, int methods, string assembly)
    {
        if (to.TryGetValue(key, out var current))
        {
            if (!current.In.Contains(assembly, StringComparer.OrdinalIgnoreCase)) current.In.Add(assembly);
            to[key] = (current.Methods + methods, current.In);
        }
        else
        {
            to[key] = (methods, [assembly]);
        }
    }

    /// <summary>The assembly names of a project and of the projects it references, transitively.</summary>
    private static IReadOnlyList<string> AssembliesOf(string projectPath)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(projectPath);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!seen.Add(current)) continue;
            XDocument doc;
            try { doc = XDocument.Load(current); }
            catch (Exception) { continue; }

            string? declared = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("AssemblyName", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
            string assemblyName = string.IsNullOrEmpty(declared) ? Path.GetFileNameWithoutExtension(current) : declared;
            if (!found.Contains(assemblyName, StringComparer.OrdinalIgnoreCase)) found.Add(assemblyName);

            string folder = Path.GetDirectoryName(current)!;
            foreach (var reference in doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase)))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                string resolved;
                try { resolved = Path.GetFullPath(include.Replace('\\', Path.DirectorySeparatorChar), folder); }
                catch (Exception) { continue; }
                if (File.Exists(resolved)) pending.Push(resolved);
            }
        }
        return found;
    }

    /// <summary>The package of the project's AndroidManifest.xml, for apps that declare it there.</summary>
    private static string? ManifestPackage(string projectDirectory)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(projectDirectory, "Properties", "AndroidManifest.xml"),
            Path.Combine(projectDirectory, "AndroidManifest.xml"),
        })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                string? package = XDocument.Load(candidate).Root?.Attribute("package")?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(package)) return package;
            }
            catch (Exception) { /* a broken manifest is simply not an answer */ }
        }
        return null;
    }

    /// <summary>
    /// A boolean msbuild property as declared anywhere in the file. Conditions are not
    /// evaluated - this reports what the project says, not what a given build will do,
    /// which is why the profiler still checks the installed APK before a session.
    /// </summary>
    private static bool? Flag(XDocument doc, string name)
    {
        var element = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (element is null) return null;
        return bool.TryParse(element.Value.Trim(), out bool parsed) ? parsed : null;
    }

    /// <summary>The .csproj paths a solution names, whichever of the two formats it is written in.</summary>
    private static IEnumerable<string> ProjectsOf(string solutionPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        string text;
        try { text = File.ReadAllText(solutionPath); }
        catch (IOException e) { throw new ProfilerException($"{solutionPath} cannot be read: {e.Message}"); }

        IEnumerable<string> declared;
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch (Exception e) { throw new ProfilerException($"{solutionPath} is not valid XML: {e.Message}"); }
            declared = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("Path")?.Value)
                .OfType<string>();
        }
        else
        {
            declared = SolutionProjectPattern.Matches(text).Select(m => m.Groups["path"].Value);
        }

        return declared
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(p.Replace('\\', Path.DirectorySeparatorChar), folder))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Project("{type}") = "Name", "relative\path.csproj", "{id}"</summary>
    private static readonly Regex SolutionProjectPattern = new(
        @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static IEnumerable<string> EnumerateProjects(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            string[] projects, children;
            try
            {
                projects = Directory.GetFiles(folder, "*.csproj");
                children = Directory.GetDirectories(folder);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var project in projects) yield return project;
            foreach (var child in children)
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }
}

/// <summary>An Android application project, as its project file describes it.</summary>
/// <param name="ProjectPath">Full path of the .csproj.</param>
/// <param name="Name">Project file name without extension.</param>
/// <param name="ApplicationId">Android package name, from ApplicationId or the manifest; null when the project declares neither.</param>
/// <param name="TargetFramework">The -android target framework moniker.</param>
/// <param name="AssemblyName">Name of the assembly the project produces.</param>
/// <param name="Configuration">Configuration the output directory was computed for.</param>
/// <param name="OutputDir">bin/&lt;Configuration&gt;/&lt;tfm&gt;: where the pdbs and the app's assemblies land.</param>
/// <param name="OutputExists">Whether that directory is there, i.e. whether the project has been built.</param>
/// <param name="Assemblies">This project's assembly plus those of the projects it references, transitively.</param>
/// <param name="EnableDiagnostics">EnableDiagnostics as declared in the project file, ignoring conditions; null when absent.</param>
/// <param name="EmbedAssembliesIntoApk">EmbedAssembliesIntoApk as declared; false (or absent, in Debug) means the weaver can rewrite assemblies on the device.</param>
/// <param name="InSolution">Whether a solution in the searched folder names this project.</param>
public sealed record AppProjectInfo(
    string ProjectPath,
    string Name,
    string? ApplicationId,
    string TargetFramework,
    string AssemblyName,
    string Configuration,
    string OutputDir,
    bool OutputExists,
    IReadOnlyList<string> Assemblies,
    bool? EnableDiagnostics,
    bool? EmbedAssembliesIntoApk,
    bool InSolution);

/// <summary>A callspec a frontend can offer, with how many methods with a body it covers.</summary>
/// <param name="Callspec">The callspec itself, ready to pass to a session.</param>
/// <param name="Kind">namespace or type.</param>
/// <param name="Methods">Methods with a body it covers - the cost of choosing it.</param>
/// <param name="Assembly">The assemblies that hold it, comma separated: what a weaving session must rewrite, which is rarely every assembly of the app.</param>
public sealed record CallspecCandidate(string Callspec, string Kind, int Methods, string Assembly);
