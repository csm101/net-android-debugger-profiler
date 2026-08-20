# Android attach notes

Living specification: everything empirically known about deploying, launching
and attaching to a .NET for Android app. Facts marked **[verified]** were
confirmed on this machine or in primary sources; everything else is
**[unverified]** until exercised by the spike/tests.

## Protocol fundamentals

- C# code on Android runs inside **MonoVM** and is **invisible to JDWP**
  (JDWP only sees the Java side). **[verified — primary docs]**
- Debugging uses the **Mono Soft Debugger protocol (SDB)**: an agent inside the
  app's Mono runtime, TCP transport, reached from the host via `adb forward`.
  **[verified — primary docs]**
- net9.0-android still uses MonoVM by default → SDB applies to the reference application.
  **[verified — App.Droid.csproj has no UseMonoRuntime=false / CoreCLR opt-in]**

## Attach recipe (msbuild does the whole dance)

```bash
dotnet build --no-restore -t:Run <Project>.csproj \
  -p:Configuration=Debug \
  -p:AndroidAttachDebugger=true \
  -p:AndroidSdbHostPort=10000 \
  -p:AndroidSdbTargetPort=10000
```

- The `Run` target deploys (fast deployment), sets the `debug.mono.*` system
  properties, starts the activity and wires the port. **[verified — dotnet/android
  docs + Avalonia guide; not yet exercised locally]**
- `AndroidSdbHostPort` and `AndroidSdbTargetPort` must match, and the debugger
  client must connect to that same port on localhost. **[verified — Avalonia guide]**
- Reference DAP client that works with this recipe: `vscode-mono-debug`
  (`"type": "mono", "request": "attach", "address": "localhost", "port": 10000`).
  MIT, usable as a protocol reference and as an interim VS Code frontend.
  **[verified — Avalonia guide]**

Open details to nail during the spike → see KNOWN_UNKNOWNS.md (does the app
wait for the debugger, timeout behavior, reconnect behavior, who listens/who
connects).

## Requirements on the app build

- Debug configuration: debuggable app, fast deployment, Mono debug agent
  enabled. Release APKs are not debuggable. **[verified — toolchain docs]**

## Licensing constraint

The VS Code ".NET MAUI" extension's debug adapter is part of the proprietary
C# Dev Kit family (license bound to VS Code). Do not drive it from our code and
do not reuse its binaries. Open alternatives: `mono/debugger-libs`,
`microsoft/vscode-mono-debug` (both MIT). **[verified — C# Dev Kit FAQ]**

## This machine

- adb 1.0.41 (36.0.0) at `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe` (on PATH)
- Emulator AVD: `pixel_7_-_api_33_0`
- .NET SDK 10.0.301, workloads: android 36.1.43 (via VS 18.7), ios, maccatalyst, maui-windows
- No desktop Mono runtime installed

## Real target: the reference application

- Solution: `C:\Work\ReferenceApp\the reference application.sln` (branch `master`)
- App project: `App.Droid\App.Droid.csproj`, TFM `net9.0-android35.0`,
  `ApplicationId=App.Droid`
- Companion projects: App.Core, App.Shared, App.Sync, App.DroidLibs, …
- Old checkout at `C:\Work\stable` is classic Xamarin (2022) — ignore it.

## Sources

- https://docs.avaloniaui.net/docs/guides/platforms/android/configure-vscode-debug-linux
- https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties
- https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq
- https://github.com/mono/debugger-libs
- https://github.com/microsoft/vscode-mono-debug
