using NetAndroidDebugger.Core.Launch;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Noticing that another debugger already owns the device. `debug.mono.extra` is device-global, so
/// two debuggers overwrite each other in silence and the loser's app hangs for the agent timeout on
/// a port nobody listens on — a symptom nowhere near its cause. Pure, so every case is testable.
/// </summary>
public sealed class DebugPropertyTests
{
    private const long Now = 1_787_473_000;

    [Fact]
    public void NoPropertySet_IsNothingToSay()
    {
        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning(null, Now));
        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning("", Now));
        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning("   ", Now));
    }

    /// <summary>
    /// An expired property diverts nothing — every Mono process reads it at startup and ignores a
    /// deadline in the past — so warning about it would be noise on every second launch.
    /// </summary>
    [Fact]
    public void AnExpiredProperty_IsNotAConflict()
    {
        var expired = $"debug=127.0.0.1:10000,timeout={Now - 1},loglevel=0,server=y";

        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning(expired, Now));
    }

    [Fact]
    public void AFreshProperty_SaysWhichPortAndForHowLong()
    {
        var fresh = $"debug=127.0.0.1:10500,timeout={Now + 120},loglevel=0,server=y";

        var warning = AndroidLauncher.ForeignDebugPropertyWarning(fresh, Now);

        Assert.NotNull(warning);
        Assert.Contains("10500", warning);
        Assert.Contains("120s", warning);
        Assert.Contains("device-global", warning);
    }

    /// <summary>
    /// Anything without a readable deadline cannot be judged fresh or stale, and guessing "conflict"
    /// would cry wolf on every launch.
    /// </summary>
    [Fact]
    public void APropertyWithoutAReadableDeadline_IsNotJudged()
    {
        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning("debug=127.0.0.1:10000,server=y", Now));
        Assert.Null(AndroidLauncher.ForeignDebugPropertyWarning("something else entirely", Now));
    }

    /// <summary>A value whose port cannot be read is still worth reporting: the deadline is what makes it a conflict.</summary>
    [Fact]
    public void AFreshPropertyWithAnUnreadablePort_IsStillReported()
    {
        var warning = AndroidLauncher.ForeignDebugPropertyWarning($"timeout={Now + 30},server=y", Now);

        Assert.NotNull(warning);
        Assert.Contains("unknown port", warning);
    }
}
