---
name: resume-session
description: Resume work in this repository from PROJECT_STATE.md and TASK_RESUME.md after a Claude Code interruption or token exhaustion.
disable-model-invocation: true
---

Resume the current repository task using the persisted state files.

Workflow:

1. Read PROJECT_STATE.md.
2. Read TASK_RESUME.md.
3. Read the living specifications relevant to the next step:
   - attach/deploy/adb/msbuild work → `ANDROID_ATTACH_NOTES.md`
   - engine, session, frontends, vendoring → `ARCHITECTURE.md`
   - test work → `TEST_CATALOG.md`
   - in every session, regardless of focus → `KNOWN_UNKNOWNS.md`
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
