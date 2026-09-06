const path = require("path");
const fs = require("fs");
const os = require("os");
const vscode = require("vscode");

const ADAPTER_DLL = "NetAndroidDebugger.Dap.dll";

/**
 * Where `register-mcp-debugger.cmd` publishes by default. The adapter is a plain dotnet dll, so the whole
 * extension is one descriptor factory: no protocol code lives here.
 */
function defaultInstallDir() {
    if (process.platform === "win32") {
        const local = process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
        return path.join(local, "net-android-debugger");
    }
    return path.join(os.homedir(), ".local", "share", "net-android-debugger");
}

function resolveAdapterPath() {
    const configured = vscode.workspace.getConfiguration("netAndroidDebugger").get("adapterPath");
    if (configured) return configured;
    return path.join(defaultInstallDir(), ADAPTER_DLL);
}

function activate(context) {
    const factory = {
        createDebugAdapterDescriptor() {
            const adapter = resolveAdapterPath();
            if (!fs.existsSync(adapter)) {
                // Saying which path was tried is the difference between a two-minute fix and a
                // bug report: the usual cause is simply that register-mcp-debugger.cmd has not been run.
                throw new Error(
                    `The debug adapter was not found at ${adapter}. Run register-mcp-debugger.cmd in the ` +
                    `net-android-debugger-profiler repository, or set "netAndroidDebugger.adapterPath".`);
            }
            const dotnet = vscode.workspace.getConfiguration("netAndroidDebugger").get("dotnetPath") || "dotnet";
            return new vscode.DebugAdapterExecutable(dotnet, [adapter]);
        },
    };

    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory("net-android", factory));

    // Attaching on Mono Android *is* a restart with the agent enabled, so an attach configuration
    // is the same thing minus the deploy. Normalising it here keeps that fact in one place.
    context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider("net-android", {
        resolveDebugConfiguration(_folder, config) {
            if (!config.type) {
                throw new Error(
                    "No configuration to launch. Add a 'net-android' entry to launch.json " +
                    "(the snippets in the launch.json editor give you one).");
            }
            if (!config.deviceSerial) {
                throw new Error("'deviceSerial' is required. Run `adb devices` and pick one explicitly.");
            }
            if (!config.packageName) {
                throw new Error("'packageName' is required: the app's ApplicationId.");
            }
            return config;
        },
    }));
}

function deactivate() { }

module.exports = { activate, deactivate };
