using System.Text.RegularExpressions;
using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;

namespace NetAndroidDebugger.Tests.Harness;

/// <summary>
/// Resolves everything the integration tests need from the environment and the repo
/// layout: the target device, the TestTarget project, its package, and source lines
/// located by code markers (never by hardcoded numbers).
/// </summary>
public static class TestEnvironment
{
    /// <summary>Env var naming the adb serial to use. Mandatory when more than one device is attached.</summary>
    public const string DeviceSerialVariable = "NAD_DEVICE_SERIAL";

    /// <summary>Env var: when set to 1 the suite does not redeploy TestTarget before running.</summary>
    public const string SkipDeployVariable = "NAD_SKIP_DEPLOY";

    public static string RepoRoot { get; } = FindRepoRoot();
    public static string TestTargetProject => Path.Combine(RepoRoot, "TestTarget", "TestTarget.csproj");
    public static string TestTargetDir => Path.Combine(RepoRoot, "TestTarget");
    public static string MainActivitySource => Path.Combine(TestTargetDir, "MainActivity.cs");
    public static string HelperServiceSource => Path.Combine(TestTargetDir, "HelperService.cs");

    public static string TestTargetPackage { get; } = ReadApplicationId(TestTargetProject);

    public static bool SkipDeploy => Environment.GetEnvironmentVariable(SkipDeployVariable) == "1";

    public static async Task<string> ResolveDeviceSerialAsync(CancellationToken ct)
    {
        var fromEnv = Environment.GetEnvironmentVariable(DeviceSerialVariable);
        var devices = await new AdbClient().ListDevicesAsync(ct);
        var online = devices.Where(d => d.State == "device").ToList();
        if (!string.IsNullOrEmpty(fromEnv))
        {
            if (online.All(d => d.Serial != fromEnv))
                throw new InvalidOperationException($"{DeviceSerialVariable}={fromEnv} but that device is not online. Online: {string.Join(", ", online.Select(d => d.Serial))}");
            return fromEnv;
        }
        if (online.Count == 1) return online[0].Serial;
        throw new InvalidOperationException(
            $"{online.Count} devices online ({string.Join(", ", online.Select(d => d.Serial))}); set {DeviceSerialVariable} to choose one. The suite never relies on the adb default device.");
    }

    /// <summary>1-based line of the first source line containing <paramref name="marker"/>.</summary>
    public static int LineOf(string sourceFile, string marker)
    {
        var lines = File.ReadAllLines(sourceFile);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        throw new InvalidOperationException($"marker '{marker}' not found in {sourceFile}");
    }

    public static AppTarget TestTargetApp() => new(TestTargetPackage, ActivityName: null, ProjectPath: TestTargetProject);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetAndroidDebugger.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root (NetAndroidDebugger.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static string ReadApplicationId(string csproj)
    {
        var m = Regex.Match(File.ReadAllText(csproj), @"<ApplicationId>(?<id>[^<]+)</ApplicationId>");
        if (!m.Success) throw new InvalidOperationException("ApplicationId not found in " + csproj);
        return m.Groups["id"].Value.Trim();
    }
}
