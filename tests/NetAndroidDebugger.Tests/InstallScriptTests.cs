using System.Text.RegularExpressions;
using NetAndroidDebugger.Tests.Harness;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// The two install scripts are the only path a new machine has to a working setup, and nothing else
/// in the build looks at them: they name folders and file names as strings, so a rename anywhere
/// else in the repo breaks them silently and only shows up on the machine being set up.
/// </summary>
public sealed class InstallScriptTests
{
    private static string ScriptPath => Path.Combine(TestEnvironment.RepoRoot, "vscode", "install-vscode-extension.cmd");
    private static string RegisterPath => Path.Combine(TestEnvironment.RepoRoot, "register-mcp-debugger.cmd");

    /// <summary>The folder the installer links must be the folder the extension actually lives in.</summary>
    [Fact]
    public void TheInstaller_PointsAtTheExtensionThatExists()
    {
        var script = File.ReadAllText(ScriptPath);

        var source = Regex.Match(script, @"set\s+""SOURCE=%REPO%(?<path>[^""]+)""");
        Assert.True(source.Success, "install-vscode-extension.cmd no longer sets SOURCE in the expected form");

        var folder = Path.Combine(TestEnvironment.RepoRoot, source.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(Path.Combine(folder, "package.json")),
            $"the installer links {folder}, which is not a VS Code extension folder");
    }

    /// <summary>
    /// The installer warns when the adapter has not been published, by probing for it under the
    /// same install folder register-mcp-debugger.cmd publishes to. Two scripts, one convention.
    /// </summary>
    [Fact]
    public void BothScripts_AgreeOnWhereTheAdapterIsPublished()
    {
        var installer = File.ReadAllText(ScriptPath);
        var register = File.ReadAllText(RegisterPath);

        const string defaultDir = @"set ""NAD_INSTALL_DIR=%LOCALAPPDATA%\net-android-debugger""";
        Assert.Contains(defaultDir, installer, StringComparison.Ordinal);
        Assert.Contains(defaultDir, register, StringComparison.Ordinal);

        Assert.Contains(@"NetAndroidDebugger.Dap.dll", installer, StringComparison.Ordinal);
        Assert.Contains(@"NetAndroidDebugger.Dap.dll", register, StringComparison.Ordinal);
    }

    /// <summary>
    /// The extension has to contribute the debug type the documented launch.json entries use;
    /// nothing at runtime would report a mismatch beyond VS Code refusing to start a session.
    /// </summary>
    [Fact]
    public void TheExtension_ContributesTheDocumentedDebugType()
    {
        var manifest = File.ReadAllText(Path.Combine(
            TestEnvironment.RepoRoot, "vscode", "net-android-debugger", "package.json"));

        Assert.Contains("\"type\": \"net-android\"", manifest, StringComparison.Ordinal);
    }
}
