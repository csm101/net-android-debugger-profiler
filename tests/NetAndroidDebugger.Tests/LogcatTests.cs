using NetAndroidDebugger.Core;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// The launcher learns which processes to attach to from logcat, so a stale line is an attach to a
/// pid that is already dead. The adb-level half of this (a stream started at the buffer's own
/// boundary replays nothing) lives in NetAndroid.Device.Tests; this is the launch-level half.
/// </summary>
[Collection(DeviceCollection.Name)]
[Trait("Category", "Device")]
public sealed class LogcatTests(DeviceFixture device, ITestOutputHelper output)
{
    [Fact]
    public async Task Launch_RightAfterAPreviousSession_AttachesToTheNewProcess_NotTheDeadOne()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pids = new List<int>();
        // Three back-to-back launches: the alternating failure showed on every second one.
        for (var i = 0; i < 3; i++)
        {
            await using var session = device.NewSession(output.WriteLine);
            await session.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options(), cts.Token);
            var main = session.GetProcesses().Single(p => p.Name == TestEnvironment.TestTargetPackage && !p.HasExited);
            Assert.DoesNotContain(main.Pid, pids);
            pids.Add(main.Pid);
            Assert.Equal(SessionState.Running, session.State);
        }
    }
}
