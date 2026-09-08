using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// The on-device weaver deployer against TestTarget, outside a session. Same device and
/// package conventions as <see cref="SessionTests"/>.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public class WeaveDeployerTests
{
    private static string Serial => Environment.GetEnvironmentVariable("NAP_TEST_SERIAL") ?? "emulator-5556";
    private static string Package => Environment.GetEnvironmentVariable("NAP_TEST_PACKAGE") ?? "com.mcasoftware.testtarget";

    /// <summary>
    /// A weaver session killed half-way leaves the pdb it moved aside as <c>.pdb.naporig</c>;
    /// the app then runs without symbols and the debugger binds no breakpoint in it. The next
    /// deploy of that assembly has to put the pdb back, as it already did for the dll.
    /// </summary>
    [SkippableFact]
    public async Task Deployer_restores_a_pdb_left_aside_by_a_killed_session()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var adb = new AdbClient();
        var device = (await adb.ListDevicesAsync(ct)).Single(d => d.Serial == Serial);
        string overrideDir = $"files/.__override__/{device.Abi}";
        string pdb = $"{overrideDir}/TestTarget.pdb";
        await adb.ForceStopAsync(Serial, Package, ct);
        // The starting state is the one a killed session leaves; an app already in it (a previous run of this
        // very test against the unfixed engine, or a real killed session) is taken as it is.
        var before = Names(await adb.RunAsAsync(Serial, Package, $"ls {overrideDir}", ct));
        Skip.IfNot(before.Contains("TestTarget.pdb") || before.Contains("TestTarget.pdb.naporig"),
            $"{Package} on {Serial} has no TestTarget.pdb in {overrideDir}: deploy a Debug build first");
        if (before.Contains("TestTarget.pdb"))
            await adb.RunAsAsync(Serial, Package, $"chmod 600 {pdb} 2>/dev/null; mv {pdb} {pdb}.naporig", ct);

        string workDir = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "deployer-" + Guid.NewGuid().ToString("N"));
        var deployer = new WeaveDeployer(adb, Serial, Package, device.Abi, workDir);
        try
        {
            await deployer.WeaveAndDeployAsync(["TestTarget"], WeaveFilter.Parse("T:TestTarget.Workloads.CpuBurner"), null, ct);
        }
        finally
        {
            await deployer.RestoreAsync(CancellationToken.None);
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }

        Assert.Empty(deployer.RestoreErrors);
        var names = Names(await adb.RunAsAsync(Serial, Package, $"ls {overrideDir}", ct));
        Assert.Contains("TestTarget.pdb", names);
        Assert.DoesNotContain("TestTarget.pdb.naporig", names);
        Assert.DoesNotContain("TestTarget.dll.naporig", names);
    }

    private static HashSet<string> Names(string listing) =>
        listing.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
}
