# VS Code extension

VS Code cannot run an arbitrary debug adapter from `launch.json`: a debug *type* has to be
contributed by an extension. This is that extension, and nothing more — it declares the type
`net-android` and points VS Code at `NetAndroidDebugger.Dap.dll`. No protocol code lives here.

## Install it

Run `register-mcp.cmd` first: it publishes the adapter to
`%LOCALAPPDATA%\net-android-debugger`, which is where this extension looks by default.

Then, for day-to-day use, symlink or copy this folder into your VS Code extensions directory:

```powershell
$target = "$env:USERPROFILE\.vscode\extensions\net-android-debugger"
New-Item -ItemType SymbolicLink -Path $target -Target "C:\GitHub\net-android-debugger\DevTools\vscode\net-android-debugger"
```

Restart VS Code. To develop on it instead, open this folder in VS Code and press F5 — that starts an
Extension Development Host with it loaded.

There is no `.vsix` package and no marketplace entry: this is proprietary software and the
repository is private.

## Use it

`launch.json` gets an entry like:

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

The launch.json editor offers snippets for this. Every argument is documented in
`../DAP_CLIENTS.md`, along with the behaviours worth knowing before you wire anything up — chiefly
that **disconnect always terminates the app**, because the Mono runtime exits when the debugger
disconnects.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `netAndroidDebugger.adapterPath` | *(empty)* | Full path to `NetAndroidDebugger.Dap.dll`. Empty means the folder `register-mcp.cmd` publishes to. |
| `netAndroidDebugger.dotnetPath` | `dotnet` | The dotnet executable used to run the adapter. |

## Checks

```
node test-extension.js
```

Runs the extension's own logic against a stand-in for the `vscode` module: adapter path
resolution, the error text when the adapter is missing, and the configuration checks. It does not
start VS Code.

**Not verified**: nobody has yet run this extension inside a real VS Code against a real device.
The adapter underneath it is covered by `DapEndToEndTests`, which drives it exactly as an editor
does, but the extension itself has only these offline checks. That gap is listed in TEST_CATALOG
section K.
