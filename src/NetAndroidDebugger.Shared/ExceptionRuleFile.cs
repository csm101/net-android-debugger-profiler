using System.Text.Json;
using System.Text.Json.Nodes;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Frontends;

/// <summary>
/// Reads exception rules from a JSON file, and stands in as the shared source the engine re-reads
/// on resume. Linked into both frontends rather than living in Core, which stays JSON-free.
/// <para>
/// The file is either a bare array of rules, or an object with an <c>exceptionRules</c> array so
/// the same file can carry other settings later.
/// </para>
/// </summary>
public sealed class ExceptionRuleFile(string path) : IExceptionRuleSource
{
    /// <summary>
    /// Where the shared rules live unless told otherwise. One file per machine on purpose: it is
    /// the baseline a person builds up over months of debugging their own apps, and repeating it
    /// per project is how it stops being maintained.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".net-android-debugger",
        "exceptionRules.json");

    public string Description => path;

    public DateTime? LastChangedUtc
    {
        get
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
    }

    public IReadOnlyList<ExceptionRule> Load()
        => File.Exists(path) ? Parse(File.ReadAllText(path), path) : [];

    /// <summary>
    /// Parses a rule array. Errors name the rule that is wrong and what was expected: a rule
    /// quietly dropped looks exactly like the engine ignoring it.
    /// </summary>
    /// <param name="json">A bare array, or an object with an <c>exceptionRules</c> array.</param>
    /// <param name="origin">Named in error messages, so the reader knows which file to fix.</param>
    public static IReadOnlyList<ExceptionRule> Parse(string json, string origin = "rules")
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"{origin} is not valid JSON: {ex.Message}"); }

        var array = root switch
        {
            JsonArray bare => bare,
            JsonObject o when o["exceptionRules"] is JsonArray nested => nested,
            _ => throw new FormatException(
                $"{origin} must be a JSON array of rules, or an object with an 'exceptionRules' array"),
        };

        var rules = new List<ExceptionRule>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject rule)
                throw new FormatException($"{origin}: rule {i + 1} is not an object");

            var actionText = Text(rule, "action")
                ?? throw new FormatException($"{origin}: rule {i + 1} has no action (break, log, logStack or ignore)");
            if (!Enum.TryParse<ExceptionAction>(actionText, ignoreCase: true, out var action))
                throw new FormatException($"{origin}: rule {i + 1}: '{actionText}' is not an action. Use break, log, logStack or ignore.");

            rules.Add(new ExceptionRule(
                action,
                Text(rule, "type"),
                Text(rule, "typeContains"),
                Text(rule, "messageContains"),
                Text(rule, "messageRegex"),
                Text(rule, "sourceFileContains")));
        }
        return rules;
    }

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
