using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Frontends;

/// <summary>
/// One launch configuration, resolved: variables expanded and relative paths made absolute against
/// the workspace folder.
/// </summary>
public sealed record LaunchConfig(
    string Name,
    string Origin,
    string DeviceSerial,
    string PackageName,
    string? ActivityName = null,
    string? ProjectPath = null,
    bool Deploy = false,
    string Configuration = "Debug",
    int BasePort = 10000,
    int? PropertyLifetimeSeconds = null,
    bool KeepPropertyFresh = false,
    IReadOnlyList<ExceptionRule>? ExceptionRules = null,
    bool UseGlobalExceptionRules = true,
    string? GlobalExceptionRulesPath = null)
{
    /// <summary>Rules carried by the configuration itself; empty when it names none.</summary>
    public IReadOnlyList<ExceptionRule> Rules => ExceptionRules ?? [];
}

/// <summary>
/// Reads a VS Code <c>launch.json</c> (or any file shaped like one) so that how an app is launched
/// lives in the project rather than in whoever is typing. The field names are exactly the ones the
/// DAP adapter reads from its launch request, so one file describes the app for both frontends:
/// pressing F5 in VS Code and launching through MCP cannot drift apart.
/// <para>
/// Kept out of Core, which stays JSON-free, and linked into both frontends.
/// </para>
/// </summary>
public static class LaunchConfigFile
{
    /// <summary>The debug type this reader accepts, matching the VS Code extension.</summary>
    public const string DebugType = "net-android";

    /// <summary>Where VS Code keeps launch configurations.</summary>
    public static string DefaultPathFor(string workspaceFolder) =>
        Path.Combine(workspaceFolder, ".vscode", "launch.json");

    /// <summary>
    /// Reads one configuration from disk.
    /// </summary>
    /// <param name="configFile">The file; defaults to <c>.vscode/launch.json</c> under the workspace folder.</param>
    /// <param name="configName">Which configuration by its <c>name</c>; defaults to the first of the right type.</param>
    /// <param name="workspaceFolder">
    /// Base for <c>${workspaceFolder}</c> and for relative paths. Defaults to the folder holding
    /// the file, or its parent when that folder is <c>.vscode</c>, which is where the project root
    /// actually is.
    /// </param>
    public static LaunchConfig Read(string? configFile = null, string? configName = null, string? workspaceFolder = null)
    {
        string path;
        if (!string.IsNullOrWhiteSpace(configFile))
        {
            path = Path.GetFullPath(configFile);
        }
        else
        {
            var root = workspaceFolder is { Length: > 0 } w ? Path.GetFullPath(w) : Directory.GetCurrentDirectory();
            path = DefaultPathFor(root);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"No launch configuration at {path}. Pass configFile, or launch with explicit arguments.", path);

        var workspace = workspaceFolder is { Length: > 0 } given ? Path.GetFullPath(given) : WorkspaceOf(path);
        return Parse(File.ReadAllText(path), path, workspace, configName);
    }

    /// <summary>
    /// The project root a configuration file belongs to: the folder holding it, unless that folder
    /// is <c>.vscode</c>, in which case the root is one level up.
    /// </summary>
    public static string WorkspaceOf(string configFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configFilePath));
        if (string.IsNullOrEmpty(dir)) return Directory.GetCurrentDirectory();
        var parent = Path.GetDirectoryName(dir);
        return string.Equals(Path.GetFileName(dir), ".vscode", StringComparison.OrdinalIgnoreCase) && parent is { Length: > 0 }
            ? parent
            : dir;
    }

    /// <summary>
    /// Parses a launch configuration. Accepts what VS Code writes (an object with a
    /// <c>configurations</c> array, comments and trailing commas included), a bare array of
    /// configurations, or a single configuration object written by hand.
    /// </summary>
    /// <param name="json">The file contents.</param>
    /// <param name="origin">Named in error messages, so the reader knows which file to fix.</param>
    /// <param name="workspaceFolder">Base for <c>${workspaceFolder}</c> and relative paths.</param>
    /// <param name="configName">Which configuration by its <c>name</c>; null takes the first usable one.</param>
    public static LaunchConfig Parse(string json, string origin, string workspaceFolder, string? configName = null)
    {
        JsonNode? root;
        try
        {
            // launch.json is JSONC: VS Code writes comments into the file it generates, and people
            // comment out configurations rather than deleting them.
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{origin} is not valid JSON: {ex.Message}");
        }

        var candidates = root switch
        {
            JsonObject o when o["configurations"] is JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonArray bare => bare.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => throw new FormatException($"{origin} must be a launch.json object, an array of configurations, or a single configuration"),
        };

        if (candidates.Count == 0)
            throw new FormatException($"{origin} holds no configurations");

        var chosen = Choose(candidates, configName, origin);
        var name = Text(chosen, "name") ?? "(unnamed)";
        string? Field(string key) => Text(chosen, key) is { } raw ? Expand(raw, workspaceFolder, origin) : null;

        var serial = Field("deviceSerial")
            ?? throw new FormatException($"{origin}: configuration '{name}' has no deviceSerial (the adb serial from `adb devices`)");
        var package = Field("packageName")
            ?? throw new FormatException($"{origin}: configuration '{name}' has no packageName (the app's ApplicationId)");

        var rules = chosen["exceptionRules"] is JsonArray ruleArray
            ? ExceptionRuleFile.Parse(ruleArray.ToJsonString(), $"{origin}: configuration '{name}'")
            : [];

        return new LaunchConfig(
            name,
            origin,
            serial,
            package,
            Field("activityName"),
            Absolute(Field("projectPath"), workspaceFolder),
            Bool(chosen, "deploy") ?? false,
            Field("configuration") ?? "Debug",
            Int(chosen, "basePort") ?? 10000,
            Int(chosen, "propertyLifetimeSeconds"),
            Bool(chosen, "keepPropertyFresh") ?? false,
            rules,
            Bool(chosen, "useGlobalExceptionRules") ?? true,
            Absolute(Field("globalExceptionRulesPath"), workspaceFolder));
    }

    /// <summary>
    /// Picks the configuration to use. Every failure here lists what the file actually offers:
    /// the usual cause is a name typed from memory, and the answer is on screen.
    /// </summary>
    private static JsonObject Choose(List<JsonObject> candidates, string? configName, string origin)
    {
        // A file written by hand for this tool need not carry a type; one that does must be ours.
        static bool IsOurs(JsonObject c) =>
            Text(c, "type") is not { } t || string.Equals(t, DebugType, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configName))
        {
            var named = candidates.FirstOrDefault(c =>
                string.Equals(Text(c, "name"), configName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException(
                    $"{origin} has no configuration named '{configName}'. It offers: {Names(candidates)}");

            return IsOurs(named)
                ? named
                : throw new FormatException(
                    $"{origin}: configuration '{configName}' has type '{Text(named, "type")}', not '{DebugType}'");
        }

        return candidates.FirstOrDefault(IsOurs)
            ?? throw new FormatException(
                $"{origin} has no '{DebugType}' configuration. It offers: {Names(candidates)}");
    }

    private static string Names(List<JsonObject> candidates) =>
        string.Join(", ", candidates.Select(c => $"'{Text(c, "name") ?? "(unnamed)"}' (type {Text(c, "type") ?? "unset"})"));

    private static readonly Regex VariablePattern = new(@"\$\{([^}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Expands the launch.json variables that mean something outside VS Code. The ones only VS Code
    /// can answer are refused by name: <c>${command:pickDevice}</c> reaching adb as a serial would
    /// fail somewhere far from its cause.
    /// </summary>
    private static string Expand(string value, string workspaceFolder, string origin) =>
        VariablePattern.Replace(value, m =>
        {
            var variable = m.Groups[1].Value;
            if (variable.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                return Environment.GetEnvironmentVariable(variable[4..]) ?? "";

            if (variable.StartsWith("command:", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    $"{origin} uses {m.Value}, which only VS Code can resolve. Put a literal value in the " +
                    "configuration, or pass that argument to this call.");
            }

            return variable.ToLowerInvariant() switch
            {
                "workspacefolder" or "workspaceroot" or "cwd" => workspaceFolder,
                "workspacefolderbasename" => Path.GetFileName(workspaceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "userhome" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "pathseparator" => Path.DirectorySeparatorChar.ToString(),
                _ => m.Value, // Left alone rather than emptied, so an unknown variable is visible in the error it causes.
            };
        });

    /// <summary>Paths are relative to the project, not to whatever directory the frontend happens to run in.</summary>
    private static string? Absolute(string? path, string workspaceFolder) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, workspaceFolder);

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static bool? Bool(JsonObject o, string name)
        => o[name] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
                : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) ? parsed
                : null
            : null;

    private static int? Int(JsonObject o, string name)
        => o[name] is JsonValue v
            ? v.TryGetValue<int>(out var i) ? i
                : v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) ? parsed
                : null
            : null;
}
