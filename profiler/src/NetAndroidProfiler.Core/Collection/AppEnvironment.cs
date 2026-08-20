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
        return bytes is null ? [] : EnvironmentOverrideFile.Parse(bytes);
    }

    /// <summary>True when the app already has an override environment file.</summary>
    private async Task<bool> OverrideExistsAsync(CancellationToken ct)
    {
        var r = await _adb.RunAsync(_serial, ["shell", $"run-as {_package} ls {OverridePath}"], ct).ConfigureAwait(false);
        return r.Success && !r.StdOut.Contains("No such", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read the app's override environment file. Throws when the file exists but cannot
    /// be read: the caller must not proceed, because a half-known file would be restored
    /// wrongly - and deleting the app's environment file leaves it unable to start.
    /// </summary>
    private async Task<byte[]?> ReadOverrideBytesAsync(CancellationToken ct)
    {
        if (!await OverrideExistsAsync(ct).ConfigureAwait(false)) return null;
        var data = await _adb.ExecOutAsync(_serial, $"run-as {_package} cat {OverridePath}", ct).ConfigureAwait(false);
        if (data.Length == 0)
            throw new ToolException($"The app's environment file ({OverridePath}) exists but could not be read; refusing to modify it.");
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
        var current = _backup is null ? [] : EnvironmentOverrideFile.Parse(_backup);
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
            await _adb.RunAsAsync(_serial, _package, $"mkdir -p {dir} && rm -f {OverridePath} && cp {remoteTmp} {OverridePath} && chmod 400 {OverridePath}", ct).ConfigureAwait(false);
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
