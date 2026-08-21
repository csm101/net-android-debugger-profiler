// Checks the extension's own logic without VS Code, by standing in for the `vscode` module.
// The extension is small on purpose - path resolution and argument checks - and that is exactly
// the part worth testing, because getting it wrong shows up as an unexplained failure to start.
//
//   node test-extension.js

const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const Module = require("module");

let settings = {};

class DebugAdapterExecutable {
    constructor(command, args) {
        this.command = command;
        this.args = args;
    }
}

const registered = {};
const vscodeStub = {
    DebugAdapterExecutable,
    workspace: {
        getConfiguration: (section) => ({
            get: (key) => settings[`${section}.${key}`],
        }),
    },
    debug: {
        registerDebugAdapterDescriptorFactory: (type, factory) => {
            registered.factory = { type, factory };
            return { dispose() { } };
        },
        registerDebugConfigurationProvider: (type, provider) => {
            registered.provider = { type, provider };
            return { dispose() { } };
        },
    },
};

// Make `require("vscode")` resolve to the stub.
const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
    if (request === "vscode") return vscodeStub;
    return originalLoad.apply(this, arguments);
};

const extension = require("./extension.js");
const context = { subscriptions: [] };
extension.activate(context);

assert.strictEqual(registered.factory.type, "net-android", "the adapter factory is registered for the debug type");
assert.strictEqual(registered.provider.type, "net-android", "the configuration provider is registered too");
assert.strictEqual(context.subscriptions.length, 2, "both registrations are disposed with the extension");

// A missing adapter must name the path it looked for and how to get one.
settings = { "netAndroidDebugger.adapterPath": path.join(os.tmpdir(), "definitely-not-here.dll") };
assert.throws(
    () => registered.factory.factory.createDebugAdapterDescriptor(),
    (err) => err.message.includes("definitely-not-here.dll") && err.message.includes("register-mcp.cmd"),
    "a missing adapter is reported with its path and the fix");

// With an adapter present, the descriptor runs it through the configured dotnet.
const fakeAdapter = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "nad-ext-")), "NetAndroidDebugger.Dap.dll");
fs.writeFileSync(fakeAdapter, "");
settings = { "netAndroidDebugger.adapterPath": fakeAdapter, "netAndroidDebugger.dotnetPath": "dotnet-x" };
const descriptor = registered.factory.factory.createDebugAdapterDescriptor();
assert.strictEqual(descriptor.command, "dotnet-x", "the configured dotnet is used");
assert.deepStrictEqual(descriptor.args, [fakeAdapter], "the adapter dll is the only argument");

// With no configured path, the default install directory is used.
settings = {};
assert.throws(
    () => registered.factory.factory.createDebugAdapterDescriptor(),
    (err) => err.message.includes("net-android-debugger") && err.message.includes("NetAndroidDebugger.Dap.dll"),
    "the default location is the one register-mcp.cmd publishes to");

// Configurations are checked before anything is started, so the message names the missing field.
const provider = registered.provider.provider;
assert.throws(() => provider.resolveDebugConfiguration(undefined, {}),
    (err) => err.message.includes("launch.json"), "an empty configuration explains itself");
assert.throws(() => provider.resolveDebugConfiguration(undefined, { type: "net-android", packageName: "a.b" }),
    (err) => err.message.includes("deviceSerial"), "a missing deviceSerial is named");
assert.throws(() => provider.resolveDebugConfiguration(undefined, { type: "net-android", deviceSerial: "emulator-5554" }),
    (err) => err.message.includes("packageName"), "a missing packageName is named");

const good = { type: "net-android", deviceSerial: "emulator-5554", packageName: "a.b" };
assert.deepStrictEqual(provider.resolveDebugConfiguration(undefined, good), good, "a complete configuration passes through");

fs.rmSync(path.dirname(fakeAdapter), { recursive: true, force: true });
console.log("extension checks passed");
