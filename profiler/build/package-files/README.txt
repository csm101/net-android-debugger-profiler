net-android-profiler @VERSION@
Copyright (c) 2026 MCA Software s.a.s. di Sirna Carlo & C. All rights reserved.

A profiler for .NET for Android applications: CPU sampling, memory and allocation
analysis, and deterministic instrumenting profiling. It drives a device or an
emulator through adb and writes every result into a SQLite database that both
frontends read.


WHAT IS IN THE BOX
------------------

  install.cmd   registers the MCP server with Claude Code (install.cmd /remove undoes it)
  bin\          the MCP server, nap.exe and nap-weave
  tools\        dotnet-dsrouter, used to reach the device's diagnostics port
  build\        MSBuild targets for build-time weaving, and the nap-weave they run
  gui\          NapGui.exe, the Delphi GUI (present when this package was built with it)


REQUIREMENTS
------------

  * .NET 10 runtime (the tools are framework-dependent)
  * Android platform-tools: adb on PATH, or ANDROID_HOME / ANDROID_SDK_ROOT set
  * The app to profile must be built with diagnostics enabled:
        dotnet build -f net9.0-android35.0 -c Debug -p:EnableDiagnostics=true
    Instrumenting on the weaver engine also needs fast deployment, which a Debug
    build gives you by default (-p:EmbedAssembliesIntoApk=false).


GETTING STARTED
---------------

  Agent-driven (MCP):

      install.cmd
      then ask Claude Code to profile the app: it exposes list_devices, check_app,
      profile_run, profile_hotspots, profile_tree, alloc_report, heap_report and the
      rest of the tool surface.

  Command line:

      bin\nap.exe doctor      what this machine offers: adb, dsrouter
      bin\nap.exe devices     attached devices, as JSON
      bin\nap.exe run --package com.example.app --mode sampling --duration 20

      run profiles, analyses, and prints where the result database is;
      bin\nap.exe help lists every option. The database is the product:
      both frontends read it, and so can any SQLite client.

  GUI:

      gui\NapGui.exe            opens the last session; File > Open picks another
      It starts bin\nap.exe serve on loopback for the live controls, so keep the
      package together.


BUILD-TIME WEAVING
------------------

An app that must keep its assemblies inside the APK can be instrumented while it is
built, instead of on the device:

      <Import Project="<this package>\build\NetAndroidProfiler.Weaving.targets" />
      dotnet build -p:NapWeave=true -p:NapCallspec="N:My.Namespace"

The build writes nap-weave.map next to the app; point the session at it and the
results carry the same names as any other instrumenting run.


LICENSING
---------

This is proprietary software. The third-party components it embeds, and their
licenses in full, are listed in THIRD-PARTY-NOTICES.txt. dotnet-dsrouter and adb are
Microsoft and Google tools invoked as separate processes; dotnet-dsrouter ships in
tools\ under its own MIT license, reproduced in the notices.
