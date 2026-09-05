using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// The launcher learns which processes to attach to from logcat, so a stale line is an attach to a
/// pid that is already dead. `logcat -c` is not enough: on the Android 11 emulator image the
/// buffer stays readable after it (measured 2026-09-05: 260 lines survive the clear), and every
/// second launch of the suite then attached to the previous test's process and failed its
/// handshake eleven seconds later.
/// </summary>
[Collection(DeviceCollection.Name)]
public sealed class LogcatTests(DeviceFixture device, ITestOutputHelper output)
{
    [Fact]
    public async Task StreamLogcat_StartedSinceNow_DoesNotReplayLinesLoggedBefore_EvenWhenClearIsIneffective()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        var adb = new AdbClient();
        var tag = "NADTEST" + Guid.NewGuid().ToString("N")[..8];

        await adb.ShellAsync(device.Serial, $"log -t {tag} before", ct);
        // No delay on purpose: the boundary is the buffer's own millisecond stamp, not a clock read,
        // so a line logged an instant earlier is still an old line.
        await adb.LogcatClearAsync(device.Serial, ct);
        var boundary = await adb.ReadLogcatBoundaryAsync(device.Serial, ct);
        output.WriteLine($"boundary: {boundary ?? "(empty buffer)"}");

        var seen = new List<string>();
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stream = adb.StreamLogcatAsync(device.Serial, line => { lock (seen) seen.Add(line); }, streamCts.Token, AdbClient.LogcatSinceArgs(boundary));

        await Task.Delay(1000, ct);
        await adb.ShellAsync(device.Serial, $"log -t {tag} after", ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !Seen("after")) await Task.Delay(200, ct);
        streamCts.Cancel();
        await stream;

        lock (seen) foreach (var l in seen.Where(l => l.Contains(tag))) output.WriteLine(l);
        Assert.True(Seen("after"), "the line logged after the stream started never arrived");
        Assert.False(Seen("before"), "a line logged before the stream started was replayed");

        bool Seen(string what) { lock (seen) return seen.Any(l => l.Contains(tag) && l.EndsWith(what)); }
    }

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
