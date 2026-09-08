namespace NetAndroid.Mcp;

/// <summary>What the server says about itself in the MCP handshake.</summary>
public static class UnifiedServer
{
    /// <summary>The registration name (<c>claude mcp add net-android ...</c>) and the server's name in the handshake.</summary>
    public const string Name = "net-android";

    public const string Version = "0.1.0";

    public const string Instructions =
        "Debugger and profiler for .NET for Android (MonoVM) apps, MAUI included, in one server. " +
        "Start with list_devices and list_app_projects. " +
        "Debugging: launch_app (deploy optional) or attach_to_app -> set_breakpoint -> wait_until_stopped / continue_and_wait -> " +
        "get_locals / get_call_stack / evaluate_expression -> step_* -> terminate_app or stop_debugging. Helper processes of the " +
        "app are attached automatically; every stop reports its pid and thread id. Screen tools (capture_screenshot, " +
        "get_ui_hierarchy, tap_screen, swipe_screen, press_key, type_text) drive the device through adb; check_device_control " +
        "says what the device allows. " +
        "Profiling (apps built with -p:EnableDiagnostics=true): profile_run (mode sampling|instrumenting|heap; restart or attach) -> " +
        "profile_hotspots / profile_tree / profile_callers / profile_timings / alloc_report / heap_report -> profile_report; long " +
        "sessions: profile_start ... profile_stop. Results live in a SQLite database per session (profile_sessions lists them; " +
        "any tool accepts sessionId, default = the last session). Sampling counts are samples (~1 ms each); *_cpu columns " +
        "exclude samples of threads blocked in Sleep/Wait; a method's exclusive samples include its very short callees. " +
        "Instrumenting needs a callspec (e.g. N:My.Namespace) and restarts the app. " +
        "The two engines hold device-global Mono state (debug.mono.extra, debug.mono.profile): a call that would start the " +
        "second engine on a device the first holds is refused, with the session to stop named in the answer. The one " +
        "combination that works is profiling the app the debugger runs: stop at a breakpoint, remove_all_breakpoints and " +
        "clear the exception rules, profile_start with launch=attach on the same package and device, continue_and_wait, " +
        "profile_stop; the debug session survives and breakpoints can be set again afterwards. get_app_output reads the debug session while one is active, otherwise the device's logcat " +
        "for the deviceSerial and packageName given.";
}
