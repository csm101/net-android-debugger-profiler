# Driving the DAP adapter from an editor

`NetAndroidDebugger.Dap` is a Debug Adapter Protocol adapter: a process that speaks DAP on stdin
and stdout. Any DAP client can run it. `register-mcp-debugger.cmd` publishes it next to the MCP server, at
`%LOCALAPPDATA%\net-android-debugger\NetAndroidDebugger.Dap.dll` by default; run it with

    dotnet "%LOCALAPPDATA%\net-android-debugger\NetAndroidDebugger.Dap.dll"

Nothing registers it — the client owns that command line.

## Arguments for `launch` / `attach`

Both take the same arguments and behave identically except that `attach` never deploys: on Mono
Android, attaching *is* restarting the app with the debugger agent enabled. There is no way to
attach to an already-running process.

| Argument | Required | Meaning |
|---|---|---|
| `packageName` | yes | The app's ApplicationId, e.g. `App.Droid`. |
| `deviceSerial` | no | adb serial, from `adb devices`. Omit it and the adapter uses `NAD_DEVICE_SERIAL`, or the only ready device; several ready devices fail listing them. A serial names a machine, so leaving it out is usually what belongs in a committed file. |
| `adbPath` | no | adb executable, or the SDK / platform-tools folder holding it. Omit it and the adapter uses `NAD_ADB_PATH`, then the SDK named by `ANDROID_HOME` / `ANDROID_SDK_ROOT` or recorded in the registry by the .NET Android workload, then PATH. A given path that is wrong is an error, never replaced by a guess. |
| `activityName` | no | `pkg/fully.qualified.Activity`; resolved automatically when omitted. |
| `projectPath` | no | Android `.csproj`; needed only with `deploy`. |
| `deploy` | no | Build and install before launching (`launch` only). |
| `configuration` | no | msbuild configuration for the deploy, default `Debug`. |
| `basePort` | no | First SDB port, default 10000; each further process takes the next one. |
| `propertyLifetimeSeconds` | no | How long the device-side debug property stays valid, default 180. |
| `keepPropertyFresh` | no | Keep it valid for the whole session, so processes the app starts much later are still debugged. Costs: any other Mono app starting meanwhile stalls waiting for a debugger on our port. |
| `exceptionRules` | no | Per-exception rules, in order; the first match decides (`break`, `log`, `logStack`, `ignore`). Criteria (`type`, `typeContains`, `messageContains`, `messageRegex`, `sourceFileContains`) are AND-ed, an unset one matches anything. Belongs in the project: it describes the app, not the person debugging it. |
| `useGlobalExceptionRules` | no | Also consult the shared rules file after the rules above, default true. |
| `globalExceptionRulesPath` | no | The shared rules file, default `%USERPROFILE%\.net-android-debugger\exceptionRules.json`. Re-read whenever it changes, so a rule edited while the app is stopped governs the next resume. |

## Behaviour worth knowing before you wire a client

- **`disconnect` always terminates the app.** `terminateDebuggee: false` cannot be honoured: the
  Mono runtime exits when the debugger disconnects. The response is sent before the teardown runs.
- **One thread list covers every process** of the app, so thread names carry their pid.
- **Frame and variable ids die with each stop.** A stale id is refused with a message rather than
  silently addressing something else.
- **Exception filters**: `uncaught` is always on; `all` adds first-chance stops on every managed
  exception. Filtering by specific type is available through the MCP frontend, not through DAP's
  filter list.

## Neovim (nvim-dap)

Works out of the box — nvim-dap runs an arbitrary adapter command.

```lua
local dap = require("dap")

dap.adapters["net-android"] = {
  type = "executable",
  command = "dotnet",
  args = { vim.fn.expand("$LOCALAPPDATA/net-android-debugger/NetAndroidDebugger.Dap.dll") },
}

dap.configurations.cs = {
  {
    type = "net-android",
    request = "launch",
    name = "Android app (restart under debugger)",
    deviceSerial = "emulator-5554",
    packageName = "net.androiddebugger.testtarget",
    keepPropertyFresh = false,
  },
}
```

## Emacs (dap-mode), and other clients that take a command

Same shape: register an adapter whose command is `dotnet <path to NetAndroidDebugger.Dap.dll>`, and
pass the launch arguments above in the configuration.

## VS Code

VS Code cannot run an arbitrary adapter from `launch.json` — a debug *type* has to be contributed by
an extension. So a small extension is needed; it holds no logic, only the declaration. Its
`package.json` needs:

```json
{
  "contributes": {
    "debuggers": [
      {
        "type": "net-android",
        "label": ".NET for Android (Mono)",
        "program": "C:\\Users\\<you>\\AppData\\Local\\net-android-debugger\\NetAndroidDebugger.Dap.dll",
        "runtime": "dotnet",
        "configurationAttributes": {
          "launch": {
            "required": ["deviceSerial", "packageName"],
            "properties": {
              "deviceSerial": { "type": "string" },
              "packageName": { "type": "string" },
              "activityName": { "type": "string" },
              "projectPath": { "type": "string" },
              "deploy": { "type": "boolean", "default": false },
              "keepPropertyFresh": { "type": "boolean", "default": false }
            }
          }
        }
      }
    ]
  }
}
```

with `launch.json` then reading:

```json
{
  "type": "net-android",
  "request": "launch",
  "name": "App.Droid on the emulator",
  "deviceSerial": "emulator-5554",
  "packageName": "App.Droid",
  "keepPropertyFresh": true
}
```

That extension lives in `net-android-debugger/` next to this file — it declares the type and
nothing else, and resolves the adapter path itself rather than hardcoding it as above. Install it
with `vscode\install-vscode-extension.cmd` from the repository root, which junctions it into the user's
extensions folder. See its README for the details and for what is and is not verified about it.
