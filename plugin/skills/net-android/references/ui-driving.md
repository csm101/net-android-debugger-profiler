# Driving the app's screen

The screen tools go through adb: `capture_screenshot`, `get_ui_hierarchy`, `tap_screen`,
`swipe_screen`, `press_key`, `type_text`, `wake_screen`, with `check_device_control` saying what
the device allows. They pick the device of the current debug session when no `deviceSerial` is
given; give it anyway.

## Structure first, pixels second

`get_ui_hierarchy` lists one line per view that carries text, an id, a description or an action:

```
(cx,cy) ShortClass id=short_id text="..." desc="..." bounds=[l,t][r,b] clickable,scrollable,focused,...
```

The centre coordinates are physical pixels, the same space as screenshots and `tap_screen`.
Filters (`text`, `textContains`, `resourceId`, `contentDescription`, `className`,
`clickableOnly`) narrow the list; `includeAll` adds the layouts, which are noise.

`capture_screenshot` shows what the user sees: visual state, an overlay, a spinner, a dialog
the hierarchy also lists as a second window. It is the one screen tool that works while the app
is suspended at a breakpoint; `get_ui_hierarchy` needs an idle UI and fails then (resume first).

## Selecting a view

`tap_screen` takes coordinates, or one selector that must match exactly one view; several
matches come back as a list to disambiguate with `className` or `resourceId`, or coordinates.
`text` and `contentDescription` compare case-insensitively; `resourceId` matches the short id
(`login_button`) or the full one (`<package>:id/login_button`); `className` the short
(`Button`) or the full name.

## MAUI apps and native apps

- **MAUI** renders its controls as native views without Android resource ids: every row shows
  `id=` empty. Select by `text`, by `contentDescription` (a control's `AutomationId`, when the
  app sets one, lands there), or by `className` plus position. `Entry` is an `EditText`,
  `Button` a `Button` or `MaterialButton`, `Label` a `TextView`, `CollectionView` a
  `RecyclerView`, a `Shell` tab bar a `BottomNavigationView`.
- **Native .NET for Android** apps expose the ids of `Resources/layout/*.xml`
  (`android:id="@+id/increment_button"` → `id=increment_button`), so `resourceId` is the
  reliable selector there.
- One app can hold both: a MAUI app with a native view handler, a native app hosting a MAUI
  page. Read the hierarchy as it comes; the class names say which world a view belongs to.

## Correlating the screen with the sources

Before tapping into a flow, and before choosing where to put a breakpoint, know which code
owns what is on screen:

1. `get_ui_hierarchy` (structure) and, when the state matters, `capture_screenshot`.
2. Find the owner in the sources: a MAUI page is a `.xaml` with its code-behind or view model,
   reached by a Shell route or a navigation call; a native screen is an Activity or Fragment
   and a layout file under `Resources/layout`. Texts on screen are the search key: a label's
   text, a button's title, a resource string name.
3. Name the target by what the code names: the handler behind the button, the command bound to
   it, the method the breakpoint should stop in.

Reading a screenshot alone leaves you guessing which of two similar screens you are on; the
hierarchy plus the page source removes the guess.

## Input

- Input tools are refused while the app is suspended at a stop: the event would block until the
  main thread consumes it. `continue_and_wait` or a step first.
- `type_text` types printable ASCII only, `%s` for a space; tap the field first. ENTER, TAB, BACK,
  HOME and any Android key go through `press_key`.
- Some emulator images crash the on-screen keyboard as soon as a text field gets focus, in a
  loop that shows a crash dialog every few seconds. Type what is needed, `press_key` BACK, and
  tell the user; they can disable the keyboard app on that image.
- Some vendor ROMs refuse input injection over adb until a developer setting allows it;
  `check_device_control` reports it, and the message names the setting.
- `swipe_screen` scrolls a list; a long swipe over a short duration is a fling.
- `wake_screen` turns the screen on and dismisses a lock screen without a PIN; a screen that is
  off has no UI hierarchy.

## Bringing the app to the point worth debugging

Drive the app with the screen tools to the screen before the operation, then set the breakpoint
or attach the profiler, then trigger the operation from the screen. Two round trips of
`get_ui_hierarchy` per screen are cheaper than one wrong tap; a screenshot after every action
is not needed.
