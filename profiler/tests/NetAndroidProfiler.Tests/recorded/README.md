# Recorded traces

Small traces captured from TestTarget on the emulator (2026-08-20, emulator
DevicePerSviluppoProfiler API 33 x86_64, .NET SDK 10.0.301, tools 9.0.661903)
for fast parser/analyzer tests that need no device.

| file | content |
|---|---|
| testtarget-sampling-jit-20s.nettrace | dotnet-trace default cpu-sampling profile, 20 s, JIT build (RunAOTCompilation=false). Hot methods: CpuBurner.Busy, CpuBurner.Mix, AllocHog.Allocate; Thread.Sleep dominates. |
| testtarget-monoprofiler-4s.nettrace | Microsoft-DotNETRuntimeMonoProfiler:0x48020200011:5, 4 s, second session on an already-instrumented process (no JIT events): MethodEnter/Leave for AllocHog.NewRecord, AllocHeavyRecord..ctor, CpuBurner.Busy, AllocHog.Allocate; 53k GCAllocation events; rundown present. |
| testtarget-heap.gcdump | dotnet-gcdump of TestTarget with 50,000 live AllocHeavyRecord (40 B each) + one AllocHeavyRecord[] (400,032 B). |
