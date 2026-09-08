# What the app's build needs, mode by mode

`check_app` tells you where an installed app stands; `build_app` gets it where it needs to be.
This file is the why behind both, so that you can read `check_app`'s answer and explain a
refusal to the user.

## The one property every profiling mode needs

`EnableDiagnostics=true` adds the runtime's diagnostics component to the APK and bakes a
diagnostics port (`127.0.0.1:9000, connect, nosuspend`) into the app's environment. Cost when
not profiling: a small native library and one background connection attempt at startup.
Without it `check_app` reports every profiling mode as not available and `profile_run` refuses
the session; the debugger does not need it, but the same build serves both engines, which is
why every on-device purpose of `build_app` turns it on.

## Purposes of `build_app`

| purpose | Build | What it serves |
|---|---|---|
| `debug` | Debug, diagnostics on, fast deployment | `launch_app`; the profiler can attach to the same process |
| `sampling` | the same | `profile_run` sampling, restart or attach |
| `heap` | the same | `profile_run` with `mode: heap` |
| `instrumenting` | the same | instrumenting with the weaver engines: the assemblies are rewritten on the device |
| `instrumenting-build-time` | Debug, diagnostics on, assemblies kept inside the APK, woven while building with `callspec` | an app that must keep its assemblies embedded; the session passes `engine: weaver` and the `weaveMapPath` the build answers |

`configuration` overrides Debug for a project that maintains its own profiling configuration
(a Release-like configuration with symbols, no optimizations, diagnostics on, no AOT). The
purpose still decides the deployment shape and the weaving.

## Fast deployment versus embedded assemblies

- **Fast deployment** (`EmbedAssembliesIntoApk=false`, the Debug default of most projects):
  the assemblies live as files in the app's sandbox, where the weaver can rewrite them. Needed
  by on-device instrumenting; harmless for everything else.
- **Embedded assemblies** (`EmbedAssembliesIntoApk=true`): the assemblies are inside the APK's
  assembly store. Sampling and heap work; on-device weaving finds nothing to rewrite and the
  session says so. Instrument such an app at build time (`instrumenting-build-time`), which
  deploys nothing on the device and needs the callspec at build time: changing the callspec
  means another build.
- **Switching shape** leaves the old copies in the sandbox, and the runtime prefers them: the
  app can stop starting at all, silently. `build_app` with `clearDeployedAssemblies: true` when
  moving an app from embedded to fast deployment, or ask the user to uninstall it first.

## AOT

Ahead-of-time compiled methods are never instrumented, and in sampling their short callees are
attributed to the caller. `check_app` reports AOT libraries as a warning. Profile a JIT build
(`RunAOTCompilation=false`, no profiled AOT) for exact attribution; the purposes above build
Debug, which is JIT.

## Symbols

Every result that names a source file comes from the portable pdbs of the build:
`list_app_projects` reports the output folder that holds them as `symbolsDir`; pass it to
`profile_run` / `profile_start`, and `profile_annotate_source` reads it. Keep the pdbs next to
the assemblies; a build that deletes them can still be profiled, but only by method name.

## Rebuild, or not

Rebuild when `check_app` says the mode is not available, when the deployment shape must
change, when the sources changed, or when a build-time weave needs another callspec.
Do not rebuild when `check_app` says the mode is available: a rebuild restarts the app,
loses its state, and may invalidate the results you were about to compare with.

## A Release build the user wants to profile

Sampling and heap need only diagnostics on. Instrumenting a Release build needs JIT and, for the
weaver, either fast deployment or build-time weaving; the runtime's own instrumenting provider
would also need `MONO_DIAGNOSTICS` baked into the app and crashes .NET 9 apps, so leave it. Tell
the user what their configuration must set (symbols, `Optimize=false` for exact lines,
`EnableDiagnostics=true`, no AOT) and pass that configuration's name to `build_app`.
