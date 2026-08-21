using NetAndroidProfiler.Core.Devices;

namespace NetAndroidProfiler.Core.Collection;

/// <summary>
/// Per-app runtime configuration on the device: the environment override file
/// of debuggable (Debug-runtime) apps, and the device-global
/// <c>debug.mono.profile</c> property as the fallback for release apps.
/// </summary>
public sealed class AppEnvironment
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly string _package;
    private readonly string _abi;
    private byte[]? _backup;
    private bool _backupExisted;
    private bool _applied;
    private string? _propBackup;

    public AppEnvironment(AdbClient adb, string serial, string package, string abi)
    {
        _adb = adb; _serial = serial; _package = package; _abi = abi;
    }

    private string OverridePath => $"files/.__override__/{_abi}/environment";

    /// <summary>Current override variables (empty when the file does not exist).</summary>
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadOverrideAsync(CancellationToken ct)
    {
        var bytes = await ReadOverrideBytesAsync(ct).ConfigureAwait(false);
        return bytes is null || bytes.Length == 0 ? [] : EnvironmentOverrideFile.Parse(bytes);
    }

    /// <summary>Size of the override file in bytes, or null when it does not exist.</summary>
    private async Task<long?> OverrideSizeAsync(CancellationToken ct)
    {
        var r = await _adb.RunAsync(_serial, ["shell", $"run-as {_package} stat -c %s {OverridePath}"], ct).ConfigureAwait(false);
        return r.Success && long.TryParse(r.StdOut.Trim(), out long size) ? size : null;
    }

    /// <summary>
    /// Read the app's override environment file, or null when there is none. An existing
    /// but empty file is data, not a failure: it carries no variables, and a session that
    /// refused to run because of one would stay blocked until someone removed the file by
    /// hand. A short read of a non-empty file is a failure and throws - proceeding on a
    /// half-known file would restore it wrongly, and an app whose environment file is
    /// mangled does not start.
    /// </summary>
    private async Task<byte[]?> ReadOverrideBytesAsync(CancellationToken ct)
    {
        long? size = await OverrideSizeAsync(ct).ConfigureAwait(false);
        if (size is null) return null;
        if (size == 0) return [];
        var data = await _adb.ExecOutAsync(_serial, $"run-as {_package} cat {OverridePath}", ct).ConfigureAwait(false);
        if (data.Length != size)
            throw new ToolException($"The app's environment file ({OverridePath}) is {size} bytes but only {data.Length} could be read; refusing to modify it.");
        return data;
    }

    /// <summary>
    /// Apply <paramref name="updates"/> to the override file (null value removes a
    /// variable), keeping a backup for <see cref="RestoreAsync"/>. Requires a
    /// debuggable app.
    /// </summary>
    public async Task ApplyOverrideAsync(IEnumerable<KeyValuePair<string, string?>> updates, CancellationToken ct)
    {
        if (!_applied)
        {
            _backup = await ReadOverrideBytesAsync(ct).ConfigureAwait(false);
            _backupExisted = _backup is not null;
            _applied = true;
        }
        var current = _backup is null || _backup.Length == 0 ? [] : EnvironmentOverrideFile.Parse(_backup);
        var merged = EnvironmentOverrideFile.Merge(current, updates);
        await WriteOverrideAsync(EnvironmentOverrideFile.Serialize(merged), ct).ConfigureAwait(false);
    }

    private async Task WriteOverrideAsync(byte[] content, CancellationToken ct)
    {
        string local = Path.Combine(Path.GetTempPath(), $"nap-env-{Guid.NewGuid():N}");
        string remoteTmp = $"/data/local/tmp/nap-env-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(local, content, ct).ConfigureAwait(false);
            await _adb.PushAsync(_serial, local, remoteTmp, ct).ConfigureAwait(false);
            await _adb.ShellAsync(_serial, $"chmod 644 {remoteTmp}", ct).ConfigureAwait(false);
            string dir = Path.GetDirectoryName(OverridePath)!.Replace('\\', '/');
            // Stage next to the target and rename over it: copying straight onto the live
            // path leaves a truncated (or empty) environment file behind if anything
            // dies mid-write, and the app then starts without its variables - or not at all.
            string staged = OverridePath + ".napnew";
            await _adb.RunAsAsync(_serial, _package, $"mkdir -p {dir} && cp {remoteTmp} {staged} && chmod 400 {staged} && mv -f {staged} {OverridePath}", ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(local); } catch { }
            await _adb.RunAsync(_serial, ["shell", $"rm -f {remoteTmp}"], ct).ConfigureAwait(false);
        }
    }

    /// <summary>Undo <see cref="ApplyOverrideAsync"/> and <see cref="SetDeviceProfilePropertyAsync"/>.</summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (_applied)
        {
            if (_backupExisted && _backup is not null)
            {
                await WriteOverrideAsync(_backup, ct).ConfigureAwait(false);
            }
            else if (!_backupExisted)
            {
                // Only remove a file that did not exist before this session: deleting the
                // app's own environment file makes it unable to start, and the failure is
                // silent (no crash in logcat).
                await _adb.RunAsync(_serial, ["shell", $"run-as {_package} rm -f {OverridePath}"], ct).ConfigureAwait(false);
            }
        }
        _backup = null; _backupExisted = false; _applied = false;
        if (_propBackup is not null)
        {
            await _adb.SetPropAsync(_serial, "debug.mono.profile", _propBackup, ct).ConfigureAwait(false);
            _propBackup = null;
        }
    }

    /// <summary>Set the device-global <c>debug.mono.profile</c> (DOTNET_DiagnosticPorts for every .NET app on the device), remembering the old value.</summary>
    public async Task SetDeviceProfilePropertyAsync(string value, CancellationToken ct)
    {
        _propBackup ??= await _adb.GetPropAsync(_serial, "debug.mono.profile", ct).ConfigureAwait(false);
        await _adb.SetPropAsync(_serial, "debug.mono.profile", value, ct).ConfigureAwait(false);
    }

    /// <summary>Remember to restore even if we never changed the property.</summary>
    public bool HasPendingChanges => _backup is not null || _backupExisted || _propBackup is not null;
}
