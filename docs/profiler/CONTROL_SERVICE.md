# `nap serve` - the local control service

The non-MCP way into Core, and what the Delphi GUI drives (KNOWN_UNKNOWNS U7).
HTTP + JSON on loopback, no web framework: `HttpListener` and
`System.Text.Json`, so it adds nothing to what Core already ships.

**Results do not travel over this channel.** The GUI opens `session.db` itself.
What goes over the wire is only what a database cannot answer: which devices
exist, whether an app can be profiled, and the live state of a running session.

## Starting it

```
nap serve [--port <n>] [--sessions-root <dir>] [--parent-pid <n>]
```

With no `--port` (or `--port 0`) a free port is taken and printed on stdout as a
single JSON line, which is how a parent process learns it:

```json
{"port":56006,"sessionsRoot":"C:\\Users\\me\\AppData\\Local\\net-android-profiler\\sessions"}
```

The GUI spawns the process, reads that line, and ends it with `POST /shutdown`
(or by killing it - sessions are cleaned up on dispose either way). Ctrl+C also
stops it.

`--parent-pid` names the process that owns this one: when it exits, the service ends
itself. A frontend that is killed rather than closed never gets to say `/shutdown`, and
without this the service would keep listening for the rest of the day. The GUI always
passes its own pid.

## Endpoints

| method | route | answer |
|---|---|---|
| GET | `/health` | version, sessions root, port |
| GET | `/prereqs` | the external tools (adb, dotnet-dsrouter, dotnet): where each was found, what it is for, and the command that installs it |
| POST | `/prereqs/install` | installs one of them (`{"tool":"dotnet-dsrouter"}`), as a job; an unknown tool, or one with no install command, is a `400` |
| GET | `/projects?path=<dir\|sln\|csproj>&configuration=Debug` | the Android application projects there: package, target framework, assembly name, build output, the assemblies of the referenced projects, and `EnableDiagnostics`/`EmbedAssembliesIntoApk` as declared |
| GET | `/projects/candidates?outputDir=<dir>&assemblies=A,B` | namespaces and types those assemblies declare, with how many methods each covers: what a frontend offers instead of a typed callspec |
| POST | `/builds` | builds and installs an app project, as a job |
| GET | `/jobs/{id}?from=<line>` | state of a job with the log lines from `from` on |
| POST | `/jobs/{id}/cancel` | ends a running job |
| GET | `/devices` | attached devices (serial, model, API level, ABI) |
| GET | `/apps/{package}/check?device=<serial>&mode=<mode>` | APK prerequisites plus the blocking problems and warnings for that mode |
| GET | `/sessions` | session directories under the root, newest first, with `ready` |
| POST | `/sessions` | starts a session in the background, `201` with its id and state |
| GET | `/sessions/{id}` | state, error, warnings and the last 20 log lines |
| GET | `/sessions/{id}/counters` | state, elapsed seconds, bytes collected (trace or event files) and how many snapshots were taken - cheap enough to poll once a second, never touches the device |
| POST | `/sessions/{id}/stop` | ends collection; the session then analyzes and becomes `Ready` |
| POST | `/sessions/{id}/pause` | stops recording without stopping the app |
| POST | `/sessions/{id}/resume` | starts recording again |
| POST | `/sessions/{id}/snapshot` | refreshes the results from what is collected so far; answers the new segment id |
| POST | `/sessions/{id}/clear` | throws away what was collected and keeps going |
| POST | `/sessions/{id}/archive` | keeps the current results under a name (`{"name":"the customers screen"}`) and goes on profiling |
| GET | `/sessions/{id}/archives` | the archived results of a session, newest first - answered from its directory, so a session nobody is running answers too |
| POST | `/shutdown` | ends the service |

## Jobs: the work that is not a session

Building an app takes minutes and its output is the point, so `/builds` and
`/prereqs/install` answer `201` with a job rather than blocking:

```json
{ "id": "build-1", "kind": "build", "state": "Running", "logFrom": 0, "logTotal": 0, "log": [] }
```

Poll `GET /jobs/{id}?from=<logTotal of the last answer>` and each reply carries only the
new lines. `state` ends at `Succeeded`, `Failed` or `Canceled`; `exitCode` is the tool's
own. A log that outgrows its cap drops its oldest lines and says so through `logFrom`,
which then runs ahead of what the caller has seen.

`POST /builds` is the one command a frontend would otherwise send its user to a prompt
for:

```json
{
  "projectPath": "C:\src\acme\Acme.Droid\Acme.Droid.csproj",
  "configuration": "Debug",
  "deviceSerial": "emulator-5556",
  "enableDiagnostics": true,
  "fastDeployment": true,
  "install": true,
  "clearDeployedAssemblies": false,
  "packageName": "com.acme.app"
}
```

`clearDeployedAssemblies` removes the app's `files/.__override__` through `run-as`
before building - what an app changing deployment mode needs, and the reason
`packageName` is there. It is not an msbuild property: the build that follows deploys
the assemblies again, and unlike uninstalling it leaves the app's data alone. An app that
has nothing to clear, or that is not debuggable, is logged and not an error.

which runs `dotnet build <project> -c Debug -t:Install -p:EnableDiagnostics=true
-p:EmbedAssembliesIntoApk=false -p:AdbTarget=-s <serial>`. A project that does not exist
is a `400` before any job starts. The profiler still never rebuilds an app on its own:
sessions profile whatever is installed, and this runs only when a frontend asks.

`POST /sessions` takes the same loose shape every frontend uses; Core validates
it (`SessionSpecFactory`), so the messages match the MCP ones exactly:

```json
{
  "deviceSerial": "emulator-5556",
  "packageName": "com.mcasoftware.testtarget",
  "mode": "sampling",
  "launch": "restart",
  "durationSeconds": 8,
  "callspec": "N:My.App",
  "engine": "provider",
  "maxTraceMb": 512
}
```

Errors are `{"error":"..."}` with the guidance text the engine produces:
`400` for anything the caller can fix (unknown mode, instrumenting without a
callspec, malformed JSON, a session this service did not start), `500` only for
a genuine failure.

## Live control, and which engine can serve it

`pause`, `resume`, `snapshot` and `clear` work on **weaver** sessions: the
collector writes event files it flushes every second, and the method names come
from the weave map, so the results can be rebuilt at any moment without stopping
anything. Pause is real - the collector polls a control file and stops recording
within about a second - and the app keeps running.

On a **runtime-provider** session they answer `400` explaining why: a .nettrace
only resolves its method names when the session ends (the rundown), so there the
honest move is to stop and start another session.

### Keeping a Get Results

A snapshot rewrites the result tables, so the way to keep one is to copy the database.
`POST /sessions/{id}/archive` refreshes the results first and then copies them, through
SQLite's own backup, to `<session>/archives/<name>.db` with a `<name>.json` beside it
holding the display name. That copy is an **ordinary result database**: it needs no schema
of its own, it opens in any frontend with no session running, and nothing is kept that was
not asked for. It answers:

```json
{ "name": "the customers screen", "path": "...\archives\the customers screen.db",
  "createdUtc": "2026-08-26T15:04:11Z", "segment": 3 }
```

Two things the GUI must show rather than hide:

- **Snapshot results are cumulative**, not deltas: the tables are rewritten with
  everything collected since the last clear, which is what AQTime's Get Results
  does. The `segment` table records each refresh.
- **Clearing does not remove instrumentation**: woven methods stay woven, so the
  overhead remains while collection is paused or cleared.

## Two channels, opposite directions

There are two local channels in this product and they are easy to confuse:

| Channel | Who starts whom | What travels |
|---|---|---|
| `nap serve` (this document) | the GUI starts `nap.exe serve` and is its client | devices, prerequisites, projects, builds, live session control - never results |
| the GUI control channel (`NapGui.exe --control`, `docs/profiler/GUI_DESIGN.md`) | the MCP server starts the GUI and is its client | open a session, choose a panel, focus a method, and a rendered PNG back |

Results never travel over either: both sides open the session database themselves. The
picture is the deliberate exception, because a picture is not in the database.
## One Windows quirk worth knowing

Windows' HTTP stack answers **411 Length Required** to a POST that carries
neither `Content-Length` nor chunked encoding, *before* the service sees the
request. Every real HTTP client sets the header even for an empty body, but
`curl -X POST http://.../shutdown` does not - use `curl -X POST -d '' ...`.
There is a test pinning this behaviour so it is not rediscovered as a bug.
