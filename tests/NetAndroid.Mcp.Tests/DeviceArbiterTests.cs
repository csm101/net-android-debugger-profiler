using NetAndroid.Mcp;

namespace NetAndroid.Mcp.Tests;

/// <summary>The arbiter's decision on its own, with the live state of the engines given as values.</summary>
public sealed class DeviceArbiterTests
{
    private const string DebuggedApp = "com.example.debugged";
    private static readonly DeviceArbiter.Holder[] NoProfiling = [];
    private static readonly DeviceArbiter.Holder[] ProfilingOn5554 = [new("20260906-120000-com.example.app-sampling", "emulator-5554")];
    private static readonly DeviceArbiter.DebugHold DebuggingOn5554 = new("emulator-5554", DebuggedApp);

    [Fact]
    public void NothingHeld_NothingRefused()
    {
        foreach (var tool in DeviceArbiter.DebuggerStarts.Concat(DeviceArbiter.ProfilerStarts))
            Assert.Null(DeviceArbiter.Refusal(tool, "emulator-5554", attachTo: null, debugging: null, NoProfiling));
    }

    [Fact]
    public void ProfileRun_OnTheDebuggersDevice_IsRefused_NamingStopDebugging()
    {
        var refusal = DeviceArbiter.Refusal("profile_run", "emulator-5554", attachTo: null, DebuggingOn5554, NoProfiling);
        Assert.NotNull(refusal);
        Assert.Contains("emulator-5554", refusal);
        Assert.Contains("stop_debugging", refusal);
        Assert.Contains(DeviceGlobals.DebugMonoExtra, refusal);
    }

    [Fact]
    public void ProfileRun_OnAnotherDevice_Proceeds()
    {
        Assert.Null(DeviceArbiter.Refusal("profile_run", "emulator-5556", attachTo: null, DebuggingOn5554, NoProfiling));
    }

    [Fact]
    public void ProfileStart_WithoutADevice_IsRefused_WhileTheDebuggerHoldsOne()
    {
        Assert.NotNull(DeviceArbiter.Refusal("profile_start", requestedSerial: null, attachTo: null, DebuggingOn5554, NoProfiling));
    }

    [Fact]
    public void ProfileStart_AttachingToTheDebuggedApp_Proceeds()
    {
        Assert.Null(DeviceArbiter.Refusal("profile_start", "emulator-5554", attachTo: DebuggedApp, DebuggingOn5554, NoProfiling));
        Assert.Null(DeviceArbiter.Refusal("profile_run", "emulator-5554", attachTo: DebuggedApp, DebuggingOn5554, NoProfiling));
    }

    [Fact]
    public void ProfileStart_AttachingToAnotherApp_OnTheDebuggersDevice_IsRefused()
    {
        Assert.NotNull(DeviceArbiter.Refusal("profile_start", "emulator-5554", attachTo: "com.example.other", DebuggingOn5554, NoProfiling));
    }

    [Fact]
    public void ProfileStart_AttachingWithoutADevice_IsRefused_EvenForTheDebuggedApp()
    {
        Assert.NotNull(DeviceArbiter.Refusal("profile_start", requestedSerial: null, attachTo: DebuggedApp, DebuggingOn5554, NoProfiling));
    }

    [Fact]
    public void Refusal_OfARestart_TellsHowToAttachInstead()
    {
        var refusal = DeviceArbiter.Refusal("profile_start", "emulator-5554", attachTo: null, DebuggingOn5554, NoProfiling);
        Assert.NotNull(refusal);
        Assert.Contains("attach", refusal);
        Assert.Contains(DebuggedApp, refusal);
        Assert.Contains("remove_all_breakpoints", refusal);
    }

    [Fact]
    public void LaunchApp_OnADeviceBeingProfiled_IsRefused_NamingTheSession()
    {
        var refusal = DeviceArbiter.Refusal("launch_app", "emulator-5554", attachTo: null, debugging: null, ProfilingOn5554);
        Assert.NotNull(refusal);
        Assert.Contains(ProfilingOn5554[0].Id, refusal);
        Assert.Contains("profile_stop", refusal);
        Assert.Contains(DeviceGlobals.DebugMonoProfile, refusal);
    }

    [Fact]
    public void AttachToApp_WithoutADevice_IsRefused_WhileAProfilingSessionRuns()
    {
        Assert.NotNull(DeviceArbiter.Refusal("attach_to_app", requestedSerial: null, attachTo: null, debugging: null, ProfilingOn5554));
    }

    [Fact]
    public void LaunchApp_OnAnotherDevice_Proceeds_WhileAProfilingSessionRuns()
    {
        Assert.Null(DeviceArbiter.Refusal("launch_app", "emulator-5556", attachTo: null, debugging: null, ProfilingOn5554));
    }

    [Fact]
    public void ToolsThatStartNothing_AreNeverRefused()
    {
        foreach (var tool in new[] { "get_locals", "profile_hotspots", "list_devices", "get_app_output", "profile_stop", "stop_debugging" })
            Assert.Null(DeviceArbiter.Refusal(tool, "emulator-5554", attachTo: null, DebuggingOn5554, ProfilingOn5554));
    }
}
