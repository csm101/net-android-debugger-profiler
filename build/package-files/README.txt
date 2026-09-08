net-android @VERSION@
Copyright (c) 2026 MCA Software s.a.s. di Sirna Carlo & C. All rights reserved.

A debugger and a profiler for .NET for Android and .NET MAUI Android applications
(C# on MonoVM), in one MCP server for Claude Code and other agents, with the skill
that tells an agent how to use them well, and a desktop GUI for the profiler. The
debugger sets breakpoints, steps, reads locals and applies exception rules; the
profiler samples the CPU, instruments methods and takes heap snapshots; screen tools
drive the app on the device through adb.


WHAT IS IN THE BOX
------------------

  install.cmd     installs into %LOCALAPPDATA%\Programs, registers the plugin with Claude
                  Code and puts a shortcut to the GUI on the desktop; what you unpacked can
                  then be deleted (install.cmd /remove undoes all of it, /here skips the copy)
  .claude-plugin\ this folder as a Claude Code plugin: the server registration and
  skills\         the skill (skills\net-android\SKILL.md and its references)
  bin\            NetAndroid.Mcp.exe (the unified server), the profiler's own server,
                  nap.exe (control service and one-shot commands), nap-weave
  tools\          dotnet-dsrouter, used to reach the device's diagnostics port
  build\          MSBuild targets for build-time weaving, and the nap-weave they run
  gui\            NapGui.exe, the Delphi GUI of the profiler (Windows)


REQUIREMENTS
------------

  * .NET 10 runtime for the server and the tools; the .NET SDK with the android
    workload to build apps for profiling or debugging (the build_app tool does it)
  * Android platform-tools: adb on PATH, or ANDROID_HOME / ANDROID_SDK_ROOT set
  * A device or emulator with USB debugging on


GETTING STARTED
---------------

  Agent-driven (Claude Code):

      install.cmd
      then restart Claude Code and ask it to profile or debug the app. The plugin
      brings the server and the skill; the skill starts with list_devices,
      list_app_projects, check_app and build_app, and knows which mode answers which
      question. Without the plugin route, install.cmd /mcp-only registers the server
      alone and copies the skill under %USERPROFILE%\.claude\skills.

  GUI:

      gui\NapGui.exe (or the desktop shortcut)   the whole profiling flow, no prompt

      It starts bin\nap.exe serve on loopback itself, so keep the package together.
      New session > Solution: point it at a .sln, .csproj or source folder and it
      lists the Android applications in it, fills in the package, the build output and
      the assemblies from the project file, and offers the app's own namespaces and
      types as the callspec. Build & install builds the app with the settings a
      session needs and deploys it to the selected device. File > Open picks a
      session recorded by any frontend: the database is the product, and any SQLite
      client reads it too.

  Command line:

      bin\nap.exe doctor      what this machine offers: adb, dsrouter
      bin\nap.exe devices     attached devices, as JSON
      bin\nap.exe run --package com.example.app --mode sampling --duration 20

      run profiles, analyses, and prints where the result database is;
      bin\nap.exe help lists every option.


BUILD-TIME WEAVING
------------------

An app that must keep its assemblies inside the APK can be instrumented while it is
built instead of on the device: the build_app tool does it with the purpose
instrumenting-build-time and a callspec, or by hand:

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
