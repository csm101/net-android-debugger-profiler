# Task resume

## Current task
M0 spike - prove the end-to-end attach chain.

## Current substep
Workspace just initialized (2026-08-20). Nothing of M0 started.

## Next action if interrupted right now
Start M0 step 1 below.

## Exact next steps (in order)
1. Vendor mono/debugger-libs: clone into ThirdParty/debugger-libs (plus
   side-by-side cecil if required), try building Mono.Debugger.Soft,
   Mono.Debugging, Mono.Debugging.Soft on net8.0. Check what
   microsoft/vscode-mono-debug PR #37 (replace NuGet packages with source
   code) kept - likely the minimal file set. Resolves U1.
2. Create TestTarget/ minimal net-android app (dotnet new android),
   Debug-buildable, one button incrementing a counter (breakpoint fodder).
3. Boot emulator pixel_7_-_api_33_0; run the attach recipe from
   ANDROID_ATTACH_NOTES.md against TestTarget; observe behavior (U2).
4. Console spike (throwaway, in DevTools/): connect Mono.Debugger.Soft to
   localhost:10000, list threads, set a source-line breakpoint, hit it, read
   a local. Resolves U3.
5. Record all findings in ANDROID_ATTACH_NOTES.md / KNOWN_UNKNOWNS.md; then
   plan the M1 engine API from what the spike taught.

## What works
- Solution scaffold builds (Core/Mcp/Tests, net8.0).

## What is failing
- Nothing yet (no functionality exists).

## Traps / hypotheses
- debugger-libs may not build unmodified on net8.0 (old csproj style, cecil
  dependency) - budget time for pruning.
- Direction of the SDB connection (listen vs connect) must be confirmed
  before writing engine code; do not assume.
- Emulator first; physical device later (U9).

## Open items outside M0
- LICENSE file not chosen yet (user decision before publishing to GitHub).
- Initial commit pushed to https://github.com/csm101/net-android-debugger (private); repo-local git author set to csm101 <carlo.sirna@gmail.com>.
