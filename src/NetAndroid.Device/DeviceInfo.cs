namespace NetAndroid.Device;

/// <summary>
/// An attached Android device or emulator, as <c>adb devices -l</c> lists it and, for a device
/// that is online, as its properties describe it. The first four fields are what the debugger
/// always had; the last three are what the profiler always had, filled for online devices and
/// left at their defaults for the others.
/// </summary>
/// <param name="Serial">The adb serial every device-bound call names.</param>
/// <param name="State"><c>device</c>, <c>offline</c>, <c>unauthorized</c>, as adb reports it.</param>
/// <param name="Model">The model name (<c>ro.product.model</c>, or the <c>model:</c> field of the listing).</param>
/// <param name="IsEmulator">Whether the serial is an emulator's.</param>
/// <param name="AvdName">The emulator's AVD name, when it is one and answers.</param>
/// <param name="ApiLevel"><c>ro.build.version.sdk</c>, or 0 when not read.</param>
/// <param name="Abi"><c>ro.product.cpu.abi</c> (the app's override environment lives under it), or empty when not read.</param>
public sealed record DeviceInfo(
    string Serial,
    string State,
    string? Model,
    bool IsEmulator,
    string? AvdName = null,
    int ApiLevel = 0,
    string Abi = "");
