using System.Text;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Mcp;

/// <summary>Plain-text rendering of Core records for the model. Kept deliberately compact.</summary>
internal static class TextFormat
{
    public static string Status(SessionStatus st)
    {
        var sb = new StringBuilder();
        sb.Append($"state={st.State} generation={st.StopGeneration}");
        if (st.DeviceSerial is not null) sb.Append($" device={st.DeviceSerial}");
        if (st.PackageName is not null) sb.Append($" package={st.PackageName}");
        sb.AppendLine();
        foreach (var p in st.Processes)
            sb.AppendLine($"  process pid={p.Pid} name={p.Name} port={p.SdbPort} {(p.HasExited ? "exited" : p.IsStopped ? "stopped" : "running")}");
        if (st.LastStop is not null) sb.AppendLine("  last stop: " + Stop(st.LastStop));
        if (st.LastError is not null) sb.AppendLine("  last error: " + st.LastError);
        return sb.ToString().TrimEnd();
    }

    public static string Stop(StopEvent e)
    {
        var s = $"#{e.Generation} pid={e.Pid} thread={e.ThreadId} reason={e.Reason}";
        if (e.Location is not null) s += " at " + Location(e.Location);
        if (e.ExceptionType is not null) s += $" exception={e.ExceptionType}";
        if (e.Message is not null) s += $" message={e.Message}";
        return s;
    }

    public static string StopOrTimeout(DebugSession s, StopEvent? stop)
    {
        if (stop is not null) return "Stopped: " + Stop(stop);
        var st = s.GetStatus();
        return st.State == SessionState.Exited
            ? "Session exited (app terminated)." + (st.LastError is null ? "" : " " + st.LastError)
            : $"timeout (state={st.State}, generation={st.StopGeneration})";
    }

    public static string Location(SourceLocationInfo l)
        => $"{l.Method} {l.File ?? "<no source>"}:{l.Line}" + (l.Column > 0 ? $":{l.Column}" : "");

    public static string Breakpoint(BreakpointInfo b)
        => $"bp {b.Id} {b.Spec.File}:{b.Spec.Line}" +
           (b.Spec.Condition is null ? "" : $" when ({b.Spec.Condition})") +
           (b.Spec.HitCount > 0 ? $" hitCount>={b.Spec.HitCount}" : "") +
           (b.Verified ? " [verified]" : " [pending]") +
           (b.Message is null ? "" : $" {b.Message}");

    public static string Frames(IReadOnlyList<FrameSnapshot> frames)
    {
        if (frames.Count == 0) return "(no frames)";
        var sb = new StringBuilder();
        foreach (var f in frames)
        {
            sb.Append($"#{f.Index} {f.Method}");
            if (f.File is not null) sb.Append($" {f.File}:{f.Line}");
            if (f.IsExternal) sb.Append(" [external]");
            else if (!f.HasDebugInfo) sb.Append(" [no debug info]");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string Variables(IReadOnlyList<VariableSnapshot> vars)
        => vars.Count == 0 ? "(none)" : string.Join('\n', vars.Select(Variable));

    public static string Variable(VariableSnapshot v)
    {
        var s = $"{v.Name} : {v.TypeName} = {v.DisplayValue}";
        if (v.IsError) s += " [error]";
        if (v.ExpansionHandle is not null) s += $" [expand: {v.ExpansionHandle}]";
        return s;
    }
}
