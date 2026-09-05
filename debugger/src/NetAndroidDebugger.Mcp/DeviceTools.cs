using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;
using NetAndroidDebugger.Core.Device;

namespace NetAndroidDebugger.Mcp;

/// <summary>
/// Screen tools: see what the device shows and act on it, so an agent can bring the app to the
/// point worth debugging by itself. Thin over <see cref="DeviceControl"/>; the device defaults to
/// the one the current debug session runs on.
/// </summary>
[McpServerToolType]
public sealed class DeviceTools(SessionHost host, ILogger<DeviceTools> logger)
{
    private const string SerialArgument =
        "adb serial of the device. Omit to use the current debug session's device, else NAD_DEVICE_SERIAL, else the only ready device.";

    private const string SelectorHelp =
        "Selectors match the UI hierarchy (see get_ui_hierarchy): text and contentDescription compare case-insensitively, " +
        "resourceId matches the short id (increment_button) or the full one (pkg:id/increment_button), className the short " +
        "(Button) or full class name.";

    [McpServerTool(Name = "check_device_control", ReadOnly = true), Description(
        "Reports what the screen tools can do on a device: display size, whether screenshots and the UI hierarchy work, " +
        "and whether adb may inject input (taps, keys, text). Stock Android allows all of it with USB debugging alone; " +
        "MIUI refuses input injection until \"USB debugging (Security settings)\" is enabled - the report says so. " +
        "Debugging itself never depends on any of this.")]
    public async Task<string> CheckDeviceControl([Description(SerialArgument)] string? deviceSerial = null, CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        var caps = await Run(() => control.ProbeCapabilitiesAsync(ct));
        var sb = new StringBuilder();
        sb.AppendLine($"device {control.Serial}: {caps.Manufacturer} {caps.Model}, Android {caps.AndroidVersion} (API {caps.SdkLevel})"
                      + (caps.MiuiVersion is not null ? $", MIUI {caps.MiuiVersion}" : ""));
        if (caps.Display is not null) sb.AppendLine("display: " + Display(caps.Display));
        sb.AppendLine($"screenshot: {Yes(caps.ScreenshotWorks)}");
        sb.AppendLine($"ui hierarchy: {Yes(caps.UiDumpWorks)}");
        sb.AppendLine($"input injection (tap/swipe/key/text): {Yes(caps.InputInjectionAllowed)}");
        foreach (var note in caps.Notes) sb.AppendLine("  " + note);
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "capture_screenshot", ReadOnly = true), Description(
        "Takes a screenshot of the device and returns it as an image (PNG, physical pixels - the coordinate space of " +
        "tap_screen and get_ui_hierarchy). Works while the app is suspended at a breakpoint. Optionally also saves it to a file.")]
    public async Task<CallToolResult> CaptureScreenshot(
        [Description(SerialArgument)] string? deviceSerial = null,
        [Description("Host path to save the PNG to (folders are created)")] string? savePath = null,
        [Description("Return the image inline (default true). Set false with savePath to only save it.")] bool inline = true,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        var shot = await Run(() => control.CaptureScreenshotAsync(ct));
        var text = $"screenshot of {control.Serial}: {shot.Width}x{shot.Height} px, {shot.Png.Length} bytes";
        if (savePath is not null)
        {
            var full = Path.GetFullPath(savePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, shot.Png, ct);
            text += $", saved to {full}";
        }
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        if (inline) result.Content.Add(ImageContentBlock.FromBytes(shot.Png, "image/png"));
        return result;
    }

    [McpServerTool(Name = "get_ui_hierarchy", ReadOnly = true), Description(
        "Lists the views on screen (accessibility hierarchy via uiautomator): one line per node with its centre " +
        "coordinates, class, resource id, text, description, bounds and flags - what tap_screen needs. By default only " +
        "nodes that carry text, an id, a description or an action are listed; layouts are noise. Filters narrow the list. " +
        "Fails while the foreground app is suspended by the debugger (uiautomator waits for an idle UI): resume first, " +
        "or use capture_screenshot, which has no such limit.")]
    public async Task<string> GetUiHierarchy(
        [Description(SerialArgument)] string? deviceSerial = null,
        [Description("Only nodes with exactly this text")] string? text = null,
        [Description("Only nodes whose text contains this")] string? textContains = null,
        [Description("Only nodes with this resource id (short or full)")] string? resourceId = null,
        [Description("Only nodes with this content description")] string? contentDescription = null,
        [Description("Only nodes of this class (short or full)")] string? className = null,
        [Description("Only clickable nodes")] bool clickableOnly = false,
        [Description("List every node, layouts included")] bool includeAll = false,
        [Description("Max nodes listed (default 200)")] int maxNodes = 200,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        var tree = await Run(() => control.DumpUiAsync(ct));
        var selector = new UiSelector(text, textContains, resourceId, contentDescription, className, Clickable: clickableOnly ? true : null);
        var nodes = UiHierarchy.Find(tree.Root, selector).Where(n => includeAll || n.IsInteresting).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{nodes.Count} node(s) on {control.Serial}, rotation {tree.Rotation}, screen {tree.Root.Bounds}"
                      + (selector.IsEmpty ? "" : $", filter {selector}"));
        foreach (var n in nodes.Take(maxNodes)) sb.AppendLine(Node(n));
        if (nodes.Count > maxNodes) sb.AppendLine($"... {nodes.Count - maxNodes} more; narrow with a filter or raise maxNodes");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "tap_screen"), Description(
        "Taps the screen, either at coordinates (physical pixels, as in screenshots) or on the one view a selector " +
        "identifies - a selector matching several views is an error listing them. " + SelectorHelp +
        " longPressMs holds the press. Refused while the debuggee is suspended at a stop: input blocks until the app's " +
        "main thread consumes it, so resume first.")]
    public async Task<string> TapScreen(
        [Description("X in pixels (with y)")] int? x = null,
        [Description("Y in pixels (with x)")] int? y = null,
        [Description("View with exactly this text")] string? text = null,
        [Description("View whose text contains this")] string? textContains = null,
        [Description("View with this resource id")] string? resourceId = null,
        [Description("View with this content description")] string? contentDescription = null,
        [Description("View of this class, to disambiguate")] string? className = null,
        [Description("Hold the press this long (ms); 0 = a tap")] int longPressMs = 0,
        [Description(SerialArgument)] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        RefuseWhileSuspended(control.Serial, "a tap");
        var selector = new UiSelector(text, textContains, resourceId, contentDescription, className);
        string target;
        int px, py;
        if (x is not null && y is not null)
        {
            if (!selector.IsEmpty) throw new McpException("Give either x/y or a selector, not both.");
            (px, py) = (x.Value, y.Value);
            target = $"({px},{py})";
        }
        else if (!selector.IsEmpty)
        {
            if (x is not null || y is not null) throw new McpException("x and y go together.");
            var node = await ResolveSingleAsync(control, selector, ct);
            (px, py) = (node.Bounds.CenterX, node.Bounds.CenterY);
            target = $"{Node(node).Trim()}";
        }
        else throw new McpException("Nothing to tap: give x and y, or a selector (text, resourceId, contentDescription...).");

        await Run(() => longPressMs > 0 ? control.LongPressAsync(px, py, longPressMs, ct) : control.TapAsync(px, py, ct));
        return $"{(longPressMs > 0 ? $"long-pressed {longPressMs} ms" : "tapped")} {target} at ({px},{py}) on {control.Serial}";
    }

    [McpServerTool(Name = "swipe_screen"), Description(
        "Swipes from one point to another over durationMs (physical pixels). Scrolling a list is a swipe; a long swipe " +
        "over a short duration is a fling.")]
    public async Task<string> SwipeScreen(
        [Description("Start X")] int fromX, [Description("Start Y")] int fromY,
        [Description("End X")] int toX, [Description("End Y")] int toY,
        [Description("Duration in ms (default 300)")] int durationMs = 300,
        [Description(SerialArgument)] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        RefuseWhileSuspended(control.Serial, "a swipe");
        await Run(() => control.SwipeAsync(fromX, fromY, toX, toY, durationMs, ct));
        return $"swiped ({fromX},{fromY}) -> ({toX},{toY}) in {durationMs} ms on {control.Serial}";
    }

    [McpServerTool(Name = "press_key"), Description(
        "Presses a key: BACK, HOME, ENTER, TAB, DEL, APP_SWITCH, WAKEUP, VOLUME_UP..., any Android KEYCODE_ name " +
        "(with or without the prefix) or a numeric keycode.")]
    public async Task<string> PressKey(
        [Description("Key name or keycode, e.g. BACK, KEYCODE_ENTER, 66")] string key,
        [Description(SerialArgument)] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        RefuseWhileSuspended(control.Serial, "a key press");
        await Run(() => control.PressKeyAsync(key, ct));
        return $"pressed {DeviceControl.NormalizeKey(key)} on {control.Serial}";
    }

    [McpServerTool(Name = "type_text"), Description(
        "Types text into the focused field (tap it first). Printable ASCII only - that is all `adb shell input text` " +
        "can do; use press_key for ENTER or TAB.")]
    public async Task<string> TypeText(
        [Description("Text to type")] string text,
        [Description(SerialArgument)] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        RefuseWhileSuspended(control.Serial, "typing");
        await Run(() => control.TypeTextAsync(text, ct));
        return $"typed {text.Length} character(s) on {control.Serial}";
    }

    [McpServerTool(Name = "wake_screen"), Description(
        "Turns the screen on and dismisses the lock screen when it has no PIN or pattern. uiautomator finds no window " +
        "on a screen that is off.")]
    public async Task<string> WakeScreen([Description(SerialArgument)] string? deviceSerial = null, CancellationToken ct = default)
    {
        var control = await ControlAsync(deviceSerial, ct);
        await Run(() => control.WakeScreenAsync(ct));
        return $"screen woken on {control.Serial}";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The device to act on. A running session names it best: the screen an agent wants to see is the
    /// one its app is on. The other sources are the same as launch_app's.
    /// </summary>
    private async Task<DeviceControl> ControlAsync(string? deviceSerial, CancellationToken ct)
    {
        var fromSession = host.Current?.GetStatus().DeviceSerial;
        var requested = deviceSerial ?? fromSession ?? Environment.GetEnvironmentVariable("NAD_DEVICE_SERIAL");
        var source = deviceSerial is not null ? null : fromSession is not null ? "the debug session" : requested is not null ? "NAD_DEVICE_SERIAL" : null;
        try
        {
            var serial = await new DebugSession().ResolveDeviceSerialAsync(requested, ct, requestedFrom: source);
            return new DeviceControl(new AdbClient(), serial, line => logger.LogInformation("{Line}", line));
        }
        catch (LaunchException ex) { throw new McpException(ex.Message); }
    }

    private async Task<UiNode> ResolveSingleAsync(DeviceControl control, UiSelector selector, CancellationToken ct)
    {
        var tree = await Run(() => control.DumpUiAsync(ct));
        var matches = UiHierarchy.Find(tree.Root, selector);
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0)
            throw new McpException($"No view matches {selector} on {control.Serial}. get_ui_hierarchy lists what is on screen.");
        var listed = string.Join('\n', matches.Take(10).Select(Node));
        throw new McpException($"{matches.Count} views match {selector}; add className, resourceId or use coordinates:\n{listed}");
    }

    /// <summary>Device-control failures are the caller's to act on, so they travel as tool errors, not server faults.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is DeviceControlException or AdbException) { throw new McpException(ex.Message); }
    }

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is DeviceControlException or AdbException) { throw new McpException(ex.Message); }
    }

    /// <summary>
    /// `input` blocks until the focused window has consumed the event, and a main thread suspended
    /// by the debugger never does: the call would sit there for its whole timeout and then fail.
    /// Refusing up front, when the session on that device is stopped, saves the wait and says why.
    /// </summary>
    private void RefuseWhileSuspended(string serial, string what)
    {
        var session = host.Current;
        if (session is null || session.State != SessionState.Stopped) return;
        if (!string.Equals(session.GetStatus().DeviceSerial, serial, StringComparison.OrdinalIgnoreCase)) return;
        throw new McpException(
            $"The debuggee on {serial} is suspended at a stop, and {what} would block until its main thread consumes it. " +
            "Resume first (continue_and_wait, step_*), then act on the screen; capture_screenshot works meanwhile.");
    }

    private static string Node(UiNode n)
    {
        var sb = new StringBuilder();
        sb.Append($"({n.Bounds.CenterX},{n.Bounds.CenterY}) {n.ShortClassName}");
        if (n.ResourceId.Length > 0) sb.Append($" id={n.ShortResourceId}");
        if (n.Text.Length > 0) sb.Append($" text=\"{n.Text}\"");
        if (n.ContentDescription.Length > 0) sb.Append($" desc=\"{n.ContentDescription}\"");
        sb.Append($" bounds={n.Bounds}");
        var flags = new List<string>();
        if (n.Clickable) flags.Add("clickable");
        if (n.LongClickable) flags.Add("long-clickable");
        if (n.Scrollable) flags.Add("scrollable");
        if (n.Checkable) flags.Add(n.Checked ? "checked" : "unchecked");
        if (n.Focused) flags.Add("focused");
        if (n.Selected) flags.Add("selected");
        if (n.Password) flags.Add("password");
        if (!n.Enabled) flags.Add("disabled");
        if (flags.Count > 0) sb.Append(' ').Append(string.Join(',', flags));
        return sb.ToString();
    }

    private static string Display(DisplayInfo d)
        => $"{d.Width}x{d.Height} px, {d.Density} dpi" + (d.Rotation is not null ? $", rotation {d.Rotation}" : "");

    private static string Yes(bool b) => b ? "yes" : "NO";
}
