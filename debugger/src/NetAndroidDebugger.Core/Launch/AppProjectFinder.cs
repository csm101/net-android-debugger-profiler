using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetAndroidDebugger.Core.Launch;

/// <summary>
/// Finds the .NET for Android application projects a solution or folder holds, so that what a
/// project already declares is not restated when launching it. A copy of the package name in a
/// launch configuration is not just redundant: it can contradict the <c>.csproj</c>, and then the
/// debugger launches an app nobody is building.
/// <para>
/// Targeting an <c>-android</c> framework is not the test — libraries do too, and in a real product
/// most of them are libraries (the reference application: thirty-odd projects target Android, two are applications).
/// An application declares an <c>ApplicationId</c>, or is an <c>Exe</c>.
/// </para>
/// </summary>
public static class AppProjectFinder
{
    /// <summary>Folders that hold no project worth launching and are expensive to walk.</summary>
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "packages", "TestResults"];

    /// <summary>
    /// Every launchable application project at <paramref name="solutionOrFolder"/>, which may be a
    /// solution (<c>.sln</c>/<c>.slnx</c>), a single <c>.csproj</c>, or a folder to walk.
    /// </summary>
    public static IReadOnlyList<AppProjectInfo> Find(string solutionOrFolder)
    {
        var full = Path.GetFullPath(solutionOrFolder);

        if (File.Exists(full))
        {
            var extension = Path.GetExtension(full);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return Describe(full, inSolution: false) is { } single ? [single] : [];

            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectsOf(full)
                    .Select(p => Describe(p, inSolution: true))
                    .OfType<AppProjectInfo>()
                    .OrderBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            throw new LaunchException($"{full} is neither a solution nor a .csproj");
        }

        if (!Directory.Exists(full))
            throw new LaunchException($"No such solution, project or folder: {full}");

        // Walking a folder finds projects that merely sit next to the product as well as those in
        // it, so mark the ones a solution here names: that is the difference between "our app" and
        // "a tool someone left in the tree".
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in Directory.EnumerateFiles(full, "*.sln").Concat(Directory.EnumerateFiles(full, "*.slnx")))
        {
            try { named.UnionWith(ProjectsOf(solution)); }
            catch (LaunchException) { /* an unreadable solution just marks nothing */ }
        }

        return EnumerateProjects(full)
            .Select(p => Describe(p, named.Contains(p)))
            .OfType<AppProjectInfo>()
            .OrderByDescending(p => p.InSolution)
            .ThenBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The one launchable project, or a failure listing the candidates. It never picks between
    /// several: launching the wrong app looks exactly like the debugger not working, and the caller
    /// can point at a solution or name the project instead.
    /// </summary>
    public static AppProjectInfo Single(string solutionOrFolder)
    {
        var found = Find(solutionOrFolder);
        var where = Path.GetFullPath(solutionOrFolder);
        return found.Count switch
        {
            1 => found[0],
            0 => throw new LaunchException(
                $"No .NET for Android application project under {where}. One targets an -android " +
                "framework and declares an ApplicationId (or is an Exe); libraries that target Android are not launchable."),
            _ => throw new LaunchException(
                $"{found.Count} launchable projects under {where}. Name one with projectPath, or point at the solution "
                + "that holds the one you mean:\n" + string.Join('\n', found.Select(Line))),
        };
    }

    /// <summary>One project as a line of text, for a tool listing or an ambiguity message.</summary>
    public static string Line(AppProjectInfo p) =>
        $"  {p.ProjectPath}  {p.ApplicationId ?? "(no ApplicationId in the project file)"}  {p.TargetFramework}"
        + (p.InSolution ? "  [in solution]" : "");

    /// <summary>
    /// Reads a project file and returns it only if it is an Android application. Anything
    /// unreadable is simply not a candidate: a folder walk meets generated and broken project files,
    /// and none of them should stop a launch.
    /// </summary>
    public static AppProjectInfo? Describe(string projectPath, bool inSolution = false)
    {
        XDocument doc;
        try { doc = XDocument.Load(projectPath); }
        catch (Exception) { return null; }

        string? Property(string name) => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

        var frameworks = Property("TargetFramework") ?? Property("TargetFrameworks") ?? "";
        var android = frameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(f => f.Contains("-android", StringComparison.OrdinalIgnoreCase));
        if (android is null) return null;

        var applicationId = Property("ApplicationId");
        var isExecutable = string.Equals(Property("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(applicationId) && !isExecutable) return null;

        return new AppProjectInfo(
            Path.GetFullPath(projectPath),
            string.IsNullOrWhiteSpace(applicationId) ? null : applicationId,
            android,
            inSolution);
    }

    /// <summary>The .csproj paths a solution names, whichever of the two formats it is written in.</summary>
    private static IEnumerable<string> ProjectsOf(string solutionPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        string text;
        try { text = File.ReadAllText(solutionPath); }
        catch (IOException ex) { throw new LaunchException($"{solutionPath} cannot be read: {ex.Message}"); }

        IEnumerable<string> declared;
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch (Exception ex) { throw new LaunchException($"{solutionPath} is not valid XML: {ex.Message}"); }
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
            var folder = pending.Pop();
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
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }
}
