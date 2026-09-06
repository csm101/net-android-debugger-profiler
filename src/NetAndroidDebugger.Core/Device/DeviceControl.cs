using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace NetAndroidDebugger.Core.Device;

/// <summary>Thrown when a device-control command fails or its output cannot be understood.</summary>
public class DeviceControlException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Thrown when the device refuses to inject input. Stock Android always lets adb inject; MIUI
/// requires the developer option "USB debugging (Security settings)" to be on first.
/// </summary>
public sealed class InputInjectionDeniedException(string message) : DeviceControlException(message);

/// <summary>Display geometry in physical pixels, the coordinate space of screenshots, the UI hierarchy and taps.</summary>
/// <param name="Rotation">0-3 (quarter turns clockwise from natural orientation), or null when the device did not report it.</param>
public sealed record DisplayInfo(int Width, int Height, int Density, int? Rotation);

/// <summary>A PNG screenshot with the dimensions read from its header.</summary>
public sealed record Screenshot(byte[] Png, int Width, int Height);

/// <summary>What the device lets this module do, probed once so a refusal is explained rather than discovered mid-task.</summary>
public sealed record DeviceControlCapabilities(
    string Manufacturer,
    string Model,
    string AndroidVersion,
    int SdkLevel,
    string? MiuiVersion,
    DisplayInfo? Display,
    bool ScreenshotWorks,
    bool UiDumpWorks,
    bool InputInjectionAllowed,
    IReadOnlyList<string> Notes);

/// <summary>
/// Drives a device's screen through adb alone: screenshots, the accessibility hierarchy, taps,
/// swipes, key presses and text. Nothing here touches the debugger: it exists so an agent can
/// bring an app to the point worth debugging and see what the screen shows when it stops there.
/// <para>
/// Everything works on stock Android with USB debugging alone. Input injection is the one part a
/// vendor may gate: MIUI refuses it until "USB debugging (Security settings)" is enabled, and the
/// refusal surfaces as <see cref="InputInjectionDeniedException"/> with that instruction, while
/// screenshots and the hierarchy keep working regardless.
/// </para>
/// </summary>
public sealed class DeviceControl(AdbClient adb, string serial, Action<string>? log = null)
{
    private static readonly Regex SizePattern = new(@"(?<kind>Override|Physical) size:\s*(?<w>\d+)x(?<h>\d+)", RegexOptions.Compiled);
    private static readonly Regex DensityPattern = new(@"(?<kind>Override|Physical) density:\s*(?<d>\d+)", RegexOptions.Compiled);
    private static readonly Regex OrientationPattern = new(@"(?:Surface)?Orientation:\s*(?<r>[0-3])\b", RegexOptions.Compiled);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>What MIUI prints when the security toggle is off; other vendors word it the same way, since the text is AOSP's.</summary>
    private static readonly string[] InjectionDeniedMarkers = ["INJECT_EVENTS", "SecurityException"];

    public string Serial { get; } = serial;

    // ------------------------------------------------------------------ observing

    public async Task<Screenshot> CaptureScreenshotAsync(CancellationToken ct)
    {
        // exec-out carries raw bytes; `shell screencap -p` would mangle them through the pty.
        var png = await adb.RunDeviceBytesAsync(Serial, ["exec-out", "screencap", "-p"], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var (width, height) = ReadPngDimensions(png);
        log?.Invoke($"[{Serial}] screenshot {width}x{height}, {png.Length} bytes");
        return new Screenshot(png, width, height);
    }

    public async Task<DisplayInfo> GetDisplayInfoAsync(CancellationToken ct)
    {
        var size = await adb.ShellAsync(Serial, "wm size", ct).ConfigureAwait(false);
        var density = await adb.ShellAsync(Serial, "wm density", ct).ConfigureAwait(false);
        var (width, height) = ParseSize(size);
        var dpi = ParseDensity(density);
        int? rotation = null;
        try
        {
            // The line is "SurfaceOrientation: N" up to Android 10 and "Orientation: N" from 11 on.
            var input = await adb.ShellAsync(Serial, "dumpsys input", ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var m = OrientationPattern.Match(input);
            if (m.Success) rotation = int.Parse(m.Groups["r"].Value);
        }
        catch (AdbException) { /* rotation is a nicety; the size is what callers need */ }
        return new DisplayInfo(width, height, dpi, rotation);
    }

    /// <summary>
    /// The accessibility hierarchy of what is on screen. uiautomator waits for the UI to go idle
    /// first, so a foreground app whose main thread the debugger has suspended makes this fail after
    /// its internal timeout; the error says so. Screenshots do not have that limitation.
    /// </summary>
    public async Task<UiTree> DumpUiAsync(CancellationToken ct)
    {
        var remote = $"/data/local/tmp/nad-ui-{Guid.NewGuid():N}.xml";
        AdbResult r;
        try
        {
            r = await adb.RunAsync(["-s", Serial, "shell", $"uiautomator dump {remote} && cat {remote}"], ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        finally
        {
            try { await adb.ShellAsync(Serial, $"rm -f {remote}", CancellationToken.None, TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (AdbException) { /* a leftover temp file is harmless */ }
        }
        var combined = r.StdOut + "\n" + r.StdErr;
        // Both are what a foreground app whose main thread is suspended looks like: MIUI reports the
        // UI never going idle, the emulator (API 30) reports no root node at all.
        if (combined.Contains("could not get idle state", StringComparison.OrdinalIgnoreCase))
            throw new DeviceControlException(
                "uiautomator could not read the screen: the UI never went idle. That is what happens when the foreground " +
                "app's main thread is suspended by the debugger (resume it first, or take a screenshot instead), " +
                "or while an animation runs.");
        if (combined.Contains("null root node", StringComparison.OrdinalIgnoreCase))
            throw new DeviceControlException(
                "uiautomator found no window to read. Either the foreground app's main thread is suspended by the debugger " +
                "(resume it first, or take a screenshot instead), or the screen is off or locked (wake it first).");
        if (!r.Succeeded && !combined.Contains("<hierarchy", StringComparison.Ordinal))
            throw new DeviceControlException($"uiautomator dump failed ({r.ExitCode}): {Trim(r.StdErr)} {Trim(r.StdOut)}".Trim());
        var tree = UiHierarchy.Parse(r.StdOut.Contains("<hierarchy", StringComparison.Ordinal) ? r.StdOut : combined);
        log?.Invoke($"[{Serial}] ui hierarchy: {UiHierarchy.Flatten(tree.Root).Count()} nodes, rotation {tree.Rotation}");
        return tree;
    }

    // ------------------------------------------------------------------ acting

    public Task TapAsync(int x, int y, CancellationToken ct)
        => InjectAsync($"tap {x} {y}", ct);

    /// <summary>A press held for <paramref name="durationMs"/>: a zero-length swipe, which is how `input` spells it.</summary>
    public Task LongPressAsync(int x, int y, int durationMs, CancellationToken ct)
        => InjectAsync($"swipe {x} {y} {x} {y} {Math.Max(1, durationMs)}", ct);

    public Task SwipeAsync(int fromX, int fromY, int toX, int toY, int durationMs, CancellationToken ct)
        => InjectAsync($"swipe {fromX} {fromY} {toX} {toY} {Math.Max(1, durationMs)}", ct);

    /// <summary>Presses a key by name (<c>BACK</c>, <c>KEYCODE_ENTER</c>) or by numeric keycode.</summary>
    public Task PressKeyAsync(string key, CancellationToken ct)
        => InjectAsync($"keyevent {NormalizeKey(key)}", ct);

    /// <summary>
    /// Types text into the focused field. <c>input text</c> handles printable ASCII only; other
    /// characters are refused up front rather than typed as garbage.
    /// </summary>
    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0) return Task.CompletedTask;
        var offending = text.FirstOrDefault(c => c < 0x20 || c > 0x7E);
        if (offending != default)
            throw new DeviceControlException(
                $"`input text` can type printable ASCII only; '{offending}' (U+{(int)offending:X4}) is not. " +
                "Use press_key for ENTER/TAB, or type the ASCII part and enter the rest by hand.");
        return InjectAsync("text " + QuoteForInputText(text), ct);
    }

    /// <summary>Turns the screen on and dismisses a keyguard that has no credential set.</summary>
    public async Task WakeScreenAsync(CancellationToken ct)
    {
        await InjectAsync("keyevent KEYCODE_WAKEUP", ct).ConfigureAwait(false);
        await adb.ShellAsync(Serial, "wm dismiss-keyguard", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the device lets adb inject input, found out with a key that does nothing
    /// (KEYCODE_UNKNOWN): a vendor that gates injection refuses that one too.
    /// </summary>
    public async Task<(bool Allowed, string Detail)> CheckInputInjectionAsync(CancellationToken ct)
    {
        try
        {
            await InjectAsync("keyevent 0", ct).ConfigureAwait(false);
            return (true, "input injection allowed");
        }
        catch (InputInjectionDeniedException ex) { return (false, ex.Message); }
        catch (DeviceControlException ex) { return (false, ex.Message); }
    }

    /// <summary>Probes everything once. Never throws for a missing capability: the result says what is missing and why.</summary>
    public async Task<DeviceControlCapabilities> ProbeCapabilitiesAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var manufacturer = await PropAsync("ro.product.manufacturer", ct).ConfigureAwait(false);
        var model = await PropAsync("ro.product.model", ct).ConfigureAwait(false);
        var release = await PropAsync("ro.build.version.release", ct).ConfigureAwait(false);
        var sdk = int.TryParse(await PropAsync("ro.build.version.sdk", ct).ConfigureAwait(false), out var s) ? s : 0;
        var miui = await PropAsync("ro.miui.ui.version.name", ct).ConfigureAwait(false);

        DisplayInfo? display = null;
        try { display = await GetDisplayInfoAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AdbException or DeviceControlException) { notes.Add("display size: " + ex.Message); }

        var screenshot = false;
        try { await CaptureScreenshotAsync(ct).ConfigureAwait(false); screenshot = true; }
        catch (Exception ex) when (ex is AdbException or DeviceControlException) { notes.Add("screenshot: " + ex.Message); }

        var uiDump = false;
        try { await DumpUiAsync(ct).ConfigureAwait(false); uiDump = true; }
        catch (Exception ex) when (ex is AdbException or DeviceControlException) { notes.Add("ui hierarchy: " + ex.Message); }

        var (injection, detail) = await CheckInputInjectionAsync(ct).ConfigureAwait(false);
        if (!injection) notes.Add("input injection: " + detail);

        return new DeviceControlCapabilities(manufacturer, model, release, sdk, miui.Length == 0 ? null : miui,
            display, screenshot, uiDump, injection, notes);
    }

    // ------------------------------------------------------------------ helpers (public for the tests and the frontends)

    /// <summary><c>back</c> and <c>KEYCODE_BACK</c> and <c>4</c> all name the same key.</summary>
    public static string NormalizeKey(string key)
    {
        var k = key.Trim();
        if (k.Length == 0) throw new DeviceControlException("empty key name");
        if (k.All(char.IsAsciiDigit)) return k;
        k = k.ToUpperInvariant().Replace(' ', '_');
        return k.StartsWith("KEYCODE_", StringComparison.Ordinal) ? k : "KEYCODE_" + k;
    }

    /// <summary>
    /// Quotes text for <c>adb shell input text</c>: spaces become <c>%s</c> (the command's own
    /// escape), and the whole thing is single-quoted for the device shell.
    /// </summary>
    public static string QuoteForInputText(string text)
        => "'" + text.Replace(" ", "%s").Replace("'", "'\\''") + "'";

    /// <summary>Whether `input` output reports the vendor's refusal to inject into another app.</summary>
    public static bool LooksLikeInjectionDenied(string output)
        => InjectionDeniedMarkers.All(m => output.Contains(m, StringComparison.Ordinal));

    /// <summary>Width and height from the IHDR chunk; throws when the bytes are not a PNG.</summary>
    public static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature))
            throw new DeviceControlException(png.Length == 0
                ? "screencap produced no data (is the screen readable? some devices refuse while a secure window is shown)"
                : $"screencap did not produce a PNG ({png.Length} bytes, starts with {Convert.ToHexString(png[..Math.Min(8, png.Length)])})");
        return (BinaryPrimitives.ReadInt32BigEndian(png[16..20]), BinaryPrimitives.ReadInt32BigEndian(png[20..24]));
    }

    private async Task InjectAsync(string inputArgs, CancellationToken ct)
    {
        // `input` exits 0 on some builds even when it prints an exception, so the text decides.
        // It also blocks until the focused window has consumed the event: with that app's main
        // thread suspended by the debugger it never returns, hence the timeout and its wording.
        AdbResult r;
        try
        {
            r = await adb.RunAsync(["-s", Serial, "shell", "input " + inputArgs], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (AdbException ex) when (ex.Message.Contains("timed out", StringComparison.Ordinal))
        {
            throw new DeviceControlException(
                $"input {inputArgs} did not complete within 30 s: the focused app is not consuming input. That is what a " +
                "main thread suspended by the debugger looks like - resume it first.", ex);
        }
        var combined = (r.StdOut + "\n" + r.StdErr).Trim();
        if (LooksLikeInjectionDenied(combined))
            throw new InputInjectionDeniedException(
                $"The device refused to inject input ({FirstLine(combined)}). On Xiaomi/MIUI enable Developer options > " +
                "\"USB debugging (Security settings)\" - see README.md, \"What to enable on the device\". Screenshots and " +
                "the UI hierarchy keep working without it.");
        if (!r.Succeeded || combined.Contains("Error:", StringComparison.Ordinal) || combined.Contains("Exception", StringComparison.Ordinal))
            throw new DeviceControlException($"input {inputArgs} failed on {Serial}: {FirstLine(combined)}");
        log?.Invoke($"[{Serial}] input {inputArgs}");
    }

    private async Task<string> PropAsync(string name, CancellationToken ct)
    {
        try { return await adb.GetPropAsync(Serial, name, ct).ConfigureAwait(false); }
        catch (AdbException) { return ""; }
    }

    private static (int Width, int Height) ParseSize(string wmSize)
    {
        // An override (from `wm size WxH`) is what the screen really shows.
        var matches = SizePattern.Matches(wmSize);
        var m = matches.FirstOrDefault(x => x.Groups["kind"].Value == "Override") ?? matches.FirstOrDefault();
        if (m is null) throw new DeviceControlException("unexpected `wm size` output: " + Trim(wmSize));
        return (int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value));
    }

    private static int ParseDensity(string wmDensity)
    {
        var matches = DensityPattern.Matches(wmDensity);
        var m = matches.FirstOrDefault(x => x.Groups["kind"].Value == "Override") ?? matches.FirstOrDefault();
        return m is null ? 0 : int.Parse(m.Groups["d"].Value);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains("Exception", StringComparison.Ordinal) || l.Contains("Error", StringComparison.Ordinal))
            ?? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    private static string Trim(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "...";
    }
}
