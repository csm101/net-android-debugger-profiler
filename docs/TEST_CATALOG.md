# Test catalog: NetAndroid.Device

What `tests/NetAndroid.Device.Tests` covers: the device layer both products share. The two
products' own catalogs (`docs/debugger/TEST_CATALOG.md`, `docs/profiler/TEST_CATALOG.md`)
say which of their sections moved here on 2026-09-06 and which stayed.

Conventions, the same as the products': every edge case is a named test; device classes
carry `Category=Device` and belong to the xUnit collection `device` (one at a time); the
device is the one `NAD_DEVICE_SERIAL` or `NAP_TEST_SERIAL` names, else the only one online,
and with several attached and none named the fixture refuses rather than guesses. The
screen tests use the debugger's TestTarget (its package read from `TestTarget/Debugger/TestTarget.csproj`)
and skip when it is not installed; the debugger's suite deploys it.

    dotnet test tests/NetAndroid.Device.Tests/NetAndroid.Device.Tests.csproj
    dotnet test tests/NetAndroid.Device.Tests/NetAndroid.Device.Tests.csproj --filter "Category!=Device"

## A. Finding adb (`AdbLocatorTests`, no device except the last one)

Moved from the debugger (its catalog section N2). Every source the locator consults is
injected; what is tested is the order and the rule that a named source which is wrong stops
the search rather than being replaced by a guess.

- [x] An explicit path wins and may name the exe, its folder, or the SDK -
      `ExplicitPath_Wins_AndAcceptsTheExe_ItsFolder_OrTheSdk`
- [x] An explicit path that is wrong is an error even when PATH would do -
      `ExplicitPath_ThatIsWrong_IsAnError_NotAFallback`
- [x] `NAD_ADB_PATH` beats the SDK variables and is an error when stale -
      `NadAdbPath_Wins_OverTheSdkVariables_AndIsAnErrorWhenWrong`
- [x] `ANDROID_HOME`, `ANDROID_SDK_ROOT`, the registry, the default folders, then PATH,
      in that order - `SdkVariables_Registry_Defaults_ThenPath_InThatOrder`
- [x] Nothing found: the message lists every source tried - `NothingFound_ListsEverySourceTried`
- [x] A missing adb is an `AdbNotFoundException`, which is an `AdbException` and a
      `ToolException`: whatever a product catches for tool failures catches it -
      `AMissingAdb_IsAnAdbFailure_AndAToolFailure`
- [x] On the machine running the suite, adb is found with PATH emptied -
      `OnThisMachine_AdbIsFound_WithoutPath`

## B. The adb client (`AdbClientTests`, device)

The union of the two products' clients, against a real device.

- [x] The listing describes an online device with every field either product had: state,
      model, API level, ABI, whether it is an emulator and its AVD name -
      `ListDevices_DescribesAnOnlineDevice_WithEveryFieldEitherProductHad`
- [x] A failed command is an `AdbException` and a `ToolException`, naming the exit code -
      `AFailedCommand_IsAnAdbException_AndAToolException`
- [x] `ShellAsync` answers trimmed output; `GetPropAsync` of an unset property is empty -
      `Shell_ReturnsTrimmedOutput_AndGetPropOfAnUnsetProperty_IsEmpty`

## C. Logcat (`LogcatTests`, device)

Moved from the debugger (the adb-level half of its section A2/H entry; the launch-level
half, three launches in a row attaching to their own new process, stayed there).

- [x] A logcat stream started at the buffer's own boundary does not replay a line logged
      before it, even where `logcat -c` leaves the buffer readable (Android 11) -
      `StreamLogcat_StartedSinceNow_DoesNotReplayLinesLoggedBefore_EvenWhenClearIsIneffective`

## D. Reading the screen (`UiHierarchyTests`, no device)

Moved whole from the debugger (its section S): the uiautomator XML shape, the noise a
vendor prints before it, and the pure helpers of `DeviceControl`.

- [x] `Parse_ReadsNodes_WithBoundsAndFlags`
- [x] `Parse_SkipsTheNoise_AVendorPrintsBeforeTheXml`
- [x] `Parse_RejectsOutputWithoutAHierarchy`
- [x] `Find_MatchesShortIds_ShortClasses_AndTextCaseInsensitively`
- [x] `Parse_WrapsSeveralTopLevelWindows_InOneRoot`
- [x] `NormalizeKey_AcceptsNames_WithOrWithoutPrefix_AndCodes`
- [x] `QuoteForInputText_EscapesSpaces_AndShellQuotes`
- [x] `LooksLikeInjectionDenied_RecognisesTheVendorRefusal`
- [x] `ReadPngDimensions_ReadsTheHeader_AndRejectsOtherBytes`

## E. Driving the screen (`DeviceControlTests`, device)

The adb-only half of the debugger's section S, against the debugger's TestTarget started
through adb alone. What needs a debug session (a tap that lands on a breakpoint, the
hierarchy of a suspended app) stayed in the debugger's `DeviceControlTests`.

- [x] Input injection is allowed on this device, or the refusal says which switch to flip -
      `InputInjection_IsAllowed_OnThisDevice`
- [x] A screenshot is a PNG the size of the display - `Screenshot_IsAPng_TheSizeOfTheDisplay`
- [x] The hierarchy lists the app's controls by resource id -
      `UiHierarchy_ListsTestTargetsControls_ByResourceId`
- [x] Typed text shows up in the focused field, an IME crash dialog notwithstanding -
      `TypeText_IntoTheFocusedField_ShowsUpInTheHierarchy`
- [x] Non-ASCII text is refused instead of typed as garbage -
      `TypeText_RefusesNonAscii_InsteadOfTypingGarbage`

## F. Device-global properties (`DevicePropertyOverrideWarningTests` no device, `DevicePropertyOverrideDeviceTests` device)

The shared backup, restore and foreign-mark logic behind the debugger's `debug.mono.extra`
and the profiler's `debug.mono.profile`. The debugger's `DebugPropertyTests` (its section O)
still run the same logic through the launcher.

- [x] No value is nothing to say, for either property - `NoValue_IsNothingToSay_ForEitherProperty`
- [x] An expired debugger property is not a conflict - `AnExpiredDebuggerProperty_IsNotAConflict`
- [x] A fresh debugger property says which port and for how long -
      `AFreshDebuggerProperty_SaysWhichPortAndForHowLong`
- [x] A debugger property without a readable deadline is not judged -
      `ADebuggerPropertyWithoutAReadableDeadline_IsNotJudged`
- [x] A profiler property is a mark whatever it holds - `AProfilerProperty_IsAMark_WhateverItHolds`
- [x] Apply remembers what was there, later writes just write, restore puts it back -
      `Apply_RemembersWhatWasThere_AndRestorePutsItBack`
- [x] Restore of an empty previous value clears the property, and does nothing when nothing
      was applied - `Restore_OfAnEmptyPrevious_ClearsTheProperty_AndDoesNothingWhenNeverApplied`

## Gaps

- [ ] `AppEnvironment`'s override-file round trip has no test of its own here: the profiler's
      `Session_restores_app_environment_and_leaves_no_dsrouter` covers it through a session.
- [ ] `ProcessRunner` has no direct tests; every adb test goes through it.
