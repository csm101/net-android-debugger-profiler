using System.Text.RegularExpressions;

namespace NetAndroid.Device;

/// <summary>
/// A device-global system property that one product sets for the length of a session and puts
/// back afterwards: <c>debug.mono.extra</c> (the debugger's SDB agent address) and
/// <c>debug.mono.profile</c> (the profiler's diagnostics port), see <see cref="DeviceGlobals"/>.
/// Every Mono app process on the device reads them at startup, so whoever writes last decides
/// what every app started afterwards does. The value found before the first write is remembered
/// and either restored (<see cref="RestoreAsync"/>) or cleared (<see cref="ClearAsync"/>) at the
/// end, each product keeping the semantics its tests specify; a value that was already there is reported first
/// (<see cref="ForeignValueWarning"/>), because it means another tool, or a run that did not shut
/// down, has left its mark on the device.
/// </summary>
public sealed class DevicePropertyOverride(AdbClient adb, string serial, string name)
{
    private static readonly Regex Deadline = new(@"timeout=(\d+)", RegexOptions.Compiled);
    private static readonly Regex Port = new(@"debug=[^,]*?:(\d+)", RegexOptions.Compiled);

    private string? _previous;
    private bool _owned;

    public string Name => name;

    public string Serial => serial;

    /// <summary>Whether a value of ours is on the device right now (applied and not yet restored).</summary>
    public bool IsApplied => _owned;

    /// <summary>What the device held before the first write, once there has been one.</summary>
    public string? PreviousValue => _previous;

    /// <summary>The value on the device right now.</summary>
    public Task<string> ReadAsync(CancellationToken ct) => adb.GetPropAsync(serial, name, ct);

    /// <summary>
    /// Writes <paramref name="value"/>. The first write remembers what was there, so that
    /// <see cref="RestoreAsync"/> can put it back; later writes (a rotation, a renewal) just write.
    /// </summary>
    public async Task ApplyAsync(string value, CancellationToken ct)
    {
        _previous ??= await adb.GetPropAsync(serial, name, ct).ConfigureAwait(false);
        await adb.SetPropAsync(serial, name, value, ct).ConfigureAwait(false);
        _owned = true;
    }

    /// <summary>
    /// Clears the property when a value of ours is on it, whatever was there before: the debugger's
    /// way, where a value left on the device is never worth keeping because every Mono app started
    /// afterwards would wait for a debugger on it. Nothing happens when nothing was applied.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct)
    {
        if (!_owned) return;
        await adb.SetPropAsync(serial, name, "", ct).ConfigureAwait(false);
        _owned = false;
        _previous = null;
    }

    /// <summary>
    /// Puts back what the device held before the first write (the profiler's way for
    /// <c>debug.mono.profile</c>); an empty previous value clears the
    /// property. Nothing happens when nothing was applied, so it is safe to call from any
    /// shutdown path.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!_owned) return;
        await adb.SetPropAsync(serial, name, _previous ?? "", ct).ConfigureAwait(false);
        _owned = false;
        _previous = null;
    }

    /// <summary>
    /// What to say about a value found on the device before taking it over, or null when there is
    /// nothing to say. A value that carries a deadline (<c>timeout=&lt;device epoch seconds&gt;</c>,
    /// as <c>debug.mono.extra</c> does) is only a conflict while the deadline is in the future:
    /// an expired one diverts nothing, and warning about it would be noise on every second launch.
    /// A value without a readable deadline is judged by <paramref name="deadlineRequired"/>: the
    /// debugger's property is not judged (guessing "conflict" would cry wolf), the profiler's carries
    /// no deadline at all, so any value there is a mark left by someone.
    /// </summary>
    /// <param name="name">The property, for the message.</param>
    /// <param name="value">The property as read from the device.</param>
    /// <param name="deviceEpochSeconds">The device clock, in the unit the property's own deadline uses.</param>
    /// <param name="deadlineRequired">Whether a value without a readable deadline is left unjudged (true) or reported as a mark (false).</param>
    public static string? ForeignValueWarning(string name, string? value, long deviceEpochSeconds, bool deadlineRequired = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var deadlineText = Deadline.Match(value);
        if (!deadlineText.Success || !long.TryParse(deadlineText.Groups[1].Value, out var deadline))
        {
            return deadlineRequired
                ? null
                : $"{name} was already set to '{value.Trim()}': another session on this device did not shut down cleanly, "
                  + "or another tool set it. The property is device-global, so this session takes it over and puts the "
                  + "old value back when it ends.";
        }
        if (deadline <= deviceEpochSeconds) return null;

        var port = Port.Match(value) is { Success: true } m ? m.Groups[1].Value : "an unknown port";
        return $"{name} was already set and stays valid for another {deadline - deviceEpochSeconds}s, "
             + $"pointing at port {port}: another debugger is attaching on this device, or a session of ours did not "
             + "shut down cleanly. The property is device-global, so this launch takes it over - if a debug session "
             + "started elsewhere stops working, that is why.";
    }
}
