# Known unknowns (repository level)

Questions that span both products. Each product keeps its own list in
`docs/<component>/KNOWN_UNKNOWNS.md`, and the rule is the same everywhere: when an entry
is resolved, move the answer into the owning document and delete the entry.

## R2 - CoreCLR: the debugging engine and the runtime profiler provider

Mono is gone in `net11.0-android`: apps built for it run on CoreCLR, where the Mono Soft
Debugger protocol (the debugger's whole engine, `ThirdParty/debugger-libs` included) and the
`Microsoft-DotNETRuntimeMonoProfiler` EventPipe provider (the profiler's runtime
instrumenting engine) do not exist. The Mono engine stays valid for `net10.0-android` apps
until November 2028, when .NET 10 leaves support, and for the reference application (`net9.0-android35.0`)
until it retargets.

What each product needs on CoreCLR is tracked in its own list (debugger U8, profiler U9):
a different wire protocol and a different debug-property mechanism for the debugger;
sampling over EventPipe survives for the profiler, the provider does not, and the IL
weaver is the instrumenting engine there. Decide after decision 2 of
`docs/ARCHITECTURE.md`; meanwhile nothing in `NetAndroid.Device` should assume Mono beyond
the two `debug.mono.*` names, which are what the Mono runtime reads and are named as such.
