namespace NetAndroid.Device;

/// <summary>
/// Thrown when an adb command fails. An adb failure is a tool failure, so code that guards
/// against <see cref="ToolException"/> catches it too.
/// </summary>
public class AdbException : ToolException
{
    public AdbException(string message) : base(message) { }
    public AdbException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when adb itself cannot be found, or when a source that names it points at nothing.
/// The message lists every place that was tried, so a fresh machine learns what to set.
/// </summary>
public sealed class AdbNotFoundException(string message) : AdbException(message);
