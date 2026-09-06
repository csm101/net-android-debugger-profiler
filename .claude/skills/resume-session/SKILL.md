---
name: resume-session
description: Resume work in this repository from the TASK_RESUME.md and PROJECT_STATE.md files after a Claude Code interruption or token exhaustion.
disable-model-invocation: true
---

Resume the current repository task using the persisted state files.

Workflow:

1. Read the root `TASK_RESUME.md`: it says which repository-level task is in
   progress, if any, and which component the current work belongs to.
2. Read that component's `docs/<component>/PROJECT_STATE.md`, then its
   `docs/<component>/TASK_RESUME.md` (`<component>` is `debugger` or `profiler`).
3. Read the living specifications relevant to the next step:
   - attach/deploy/adb/msbuild work -> `docs/debugger/ANDROID_ATTACH_NOTES.md`
   - collection/providers/dsrouter work -> `docs/profiler/ANDROID_PROFILING_NOTES.md`
   - engine, session, frontends, vendoring, SQLite schema -> the component's `ARCHITECTURE.md`
   - the shared device library or anything spanning both products -> `docs/ARCHITECTURE.md`
   - test work -> the component's `TEST_CATALOG.md`
   - in every session, regardless of focus -> the component's `KNOWN_UNKNOWNS.md`
     and `docs/KNOWN_UNKNOWNS.md`
4. Inspect only the files or symbols referenced there first.
5. Continue exactly from the recorded next step.
6. Do not restart repository exploration from zero unless the recorded state is
   clearly stale or inconsistent.

If one or both state files are missing:

1. Say that a normal resume is required because persisted state is unavailable.
2. Reconstruct context from the conversation and minimum necessary files.
3. Start maintaining PROJECT_STATE.md and TASK_RESUME.md from that point.

While resuming:

- prefer narrow reads over broad scans
- treat TASK_RESUME.md as the source of truth for the task cursor
- update TASK_RESUME.md again when restart cost begins to rise
