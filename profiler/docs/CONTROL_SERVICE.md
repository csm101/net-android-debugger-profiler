# `nap serve` - the local control service

The non-MCP way into Core, and what the Delphi GUI drives (KNOWN_UNKNOWNS U7).
HTTP + JSON on loopback, no web framework: `HttpListener` and
`System.Text.Json`, so it adds nothing to what Core already ships.

**Results do not travel over this channel.** The GUI opens `session.db` itself.
What goes over the wire is only what a database cannot answer: which devices
exist, whether an app can be profiled, and the live state of a running session.

## Starting it

```
nap serve [--port <n>] [--sessions-root <dir>]
```

With no `--port` (or `--port 0`) a free port is taken and printed on stdout as a
single JSON line, which is how a parent process learns it:

```json
{"port":56006,"sessionsRoot":"C:\\Users\\me\\AppData\\Local\\net-android-profiler\\sessions"}
```

The GUI spawns the process, reads that line, and ends it with `POST /shutdown`
(or by killing it - sessions are cleaned up on dispose either way). Ctrl+C also
stops it.

## Endpoints

| method | route | answer |
|---|---|---|
| GET | `/health` | version, sessions root, port |
| GET | `/devices` | attached devices (serial, model, API level, ABI) |
| GET | `/apps/{package}/check?device=<serial>&mode=<mode>` | APK prerequisites plus the blocking problems and warnings for that mode |
| GET | `/sessions` | session directories under the root, newest first, with `ready` |
| POST | `/sessions` | starts a session in the background, `201` with its id and state |
| GET | `/sessions/{id}` | state, error, warnings and the last 20 log lines |
| POST | `/sessions/{id}/stop` | ends collection; the session then analyzes and becomes `Ready` |
| POST | `/sessions/{id}/pause`&#124;`resume`&#124;`snapshot`&#124;`clear` | `501` for now - see below |
| POST | `/shutdown` | ends the service |

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

## The four verbs that answer 501

`pause`, `resume`, `snapshot` and `clear` are part of the contract already so the
GUI can be written against the final shape, but the engine work behind them is
not done: it needs session segments in Core and, for a real pause, a way to
toggle the on-device collector's `Enabled` flag. The 501 body says so rather than
pretending the route does not exist. See U7 for what each verb means per engine -
notably that a real pause is only possible with the weaver.

## One Windows quirk worth knowing

Windows' HTTP stack answers **411 Length Required** to a POST that carries
neither `Content-Length` nor chunked encoding, *before* the service sees the
request. Every real HTTP client sets the header even for an empty body, but
`curl -X POST http://.../shutdown` does not - use `curl -X POST -d '' ...`.
There is a test pinning this behaviour so it is not rediscovered as a bug.
