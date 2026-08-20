namespace NetAndroidProfiler.Core.Store;

/// <summary>
/// SQLite schema of a session database. This is the contract read by the
/// Delphi GUI; every change bumps <see cref="Version"/> and is documented in
/// ARCHITECTURE.md in the same change set.
/// </summary>
public static class ResultSchema
{
    public const int Version = 1;

    public const string CreateScript = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE schema_info (
            version      INTEGER NOT NULL,
            created_utc  TEXT    NOT NULL,
            tool_version TEXT    NOT NULL
        );

        -- One row: what was profiled and how.
        CREATE TABLE session (
            id                 TEXT PRIMARY KEY,
            mode               TEXT NOT NULL,          -- Sampling | Instrumenting | Memory
            state              TEXT NOT NULL,          -- Ready | Failed
            package            TEXT,
            device_serial      TEXT,
            started_utc        TEXT,
            duration_ms        REAL,
            trace_file         TEXT,
            total_samples      INTEGER,
            samples_with_stack INTEGER,
            spec_json          TEXT,
            error              TEXT
        );

        -- Shared dictionaries -------------------------------------------------
        CREATE TABLE method (
            id                INTEGER PRIMARY KEY,
            module            TEXT NOT NULL,
            namespace         TEXT NOT NULL,
            type_name         TEXT NOT NULL,
            name              TEXT NOT NULL,
            signature         TEXT NOT NULL,
            full_name         TEXT NOT NULL,
            runtime_method_id INTEGER NOT NULL,
            is_wait_frame     INTEGER NOT NULL DEFAULT 0,
            token             INTEGER NOT NULL DEFAULT 0   -- metadata token (0x06xxxxxx) for pdb lookup
        );
        CREATE INDEX ix_method_module_token ON method(module, token);
        CREATE INDEX ix_method_full_name ON method(full_name);

        CREATE TABLE thread (
            id       INTEGER PRIMARY KEY,
            os_tid   INTEGER NOT NULL,
            name     TEXT,
            samples  INTEGER NOT NULL DEFAULT 0,
            first_ms REAL,
            last_ms  REAL
        );

        CREATE TABLE type (
            id        INTEGER PRIMARY KEY,
            name      TEXT NOT NULL,
            vtable_id INTEGER NOT NULL DEFAULT 0,
            class_id  INTEGER NOT NULL DEFAULT 0
        );

        -- Sampling ------------------------------------------------------------
        -- Units: sample counts. *_cpu excludes samples whose leaf is a wait frame.
        CREATE TABLE sample_stat (
            method_id     INTEGER PRIMARY KEY REFERENCES method(id),
            inclusive     INTEGER NOT NULL,
            exclusive     INTEGER NOT NULL,
            inclusive_cpu INTEGER NOT NULL,
            exclusive_cpu INTEGER NOT NULL
        );

        -- Aggregated call tree; one root per thread with method_id = -1.
        CREATE TABLE sample_tree (
            id            INTEGER PRIMARY KEY,
            parent_id     INTEGER,
            method_id     INTEGER NOT NULL,
            thread_id     INTEGER NOT NULL,
            depth         INTEGER NOT NULL,
            inclusive     INTEGER NOT NULL,
            exclusive     INTEGER NOT NULL,
            inclusive_cpu INTEGER NOT NULL,
            exclusive_cpu INTEGER NOT NULL
        );
        CREATE INDEX ix_sample_tree_parent ON sample_tree(parent_id);
        CREATE INDEX ix_sample_tree_method ON sample_tree(method_id);

        CREATE TABLE sample_edge (
            caller_method_id INTEGER NOT NULL,
            callee_method_id INTEGER NOT NULL,
            samples          INTEGER NOT NULL,
            PRIMARY KEY (caller_method_id, callee_method_id)
        );
        CREATE INDEX ix_sample_edge_callee ON sample_edge(callee_method_id);

        -- Instrumenting (enter/leave) -------------------------------------------
        -- Units: nanoseconds and call counts.
        CREATE TABLE timing_stat (
            method_id        INTEGER PRIMARY KEY REFERENCES method(id),
            calls            INTEGER NOT NULL,
            total_ns         INTEGER NOT NULL,
            self_ns          INTEGER NOT NULL,
            min_ns           INTEGER NOT NULL,
            max_ns           INTEGER NOT NULL,
            exception_leaves INTEGER NOT NULL
        );

        CREATE TABLE timing_tree (
            id        INTEGER PRIMARY KEY,
            parent_id INTEGER,
            method_id INTEGER NOT NULL,
            thread_id INTEGER NOT NULL,
            depth     INTEGER NOT NULL,
            calls     INTEGER NOT NULL,
            total_ns  INTEGER NOT NULL,
            self_ns   INTEGER NOT NULL
        );
        CREATE INDEX ix_timing_tree_parent ON timing_tree(parent_id);
        CREATE INDEX ix_timing_tree_method ON timing_tree(method_id);

        -- Allocations (exact, from the runtime provider) -------------------------
        CREATE TABLE alloc_by_type (
            type_id INTEGER PRIMARY KEY REFERENCES type(id),
            count   INTEGER NOT NULL,
            bytes   INTEGER NOT NULL
        );

        -- method_id = -1 when no instrumented frame was open on the allocating thread.
        CREATE TABLE alloc_by_site (
            type_id   INTEGER NOT NULL,
            method_id INTEGER NOT NULL,
            count     INTEGER NOT NULL,
            bytes     INTEGER NOT NULL,
            PRIMARY KEY (type_id, method_id)
        );

        -- Heap snapshots (live objects by type at a point in time) --------------
        CREATE TABLE heap_snapshot (
            id            INTEGER PRIMARY KEY,
            taken_utc     TEXT NOT NULL,
            total_objects INTEGER NOT NULL,
            total_bytes   INTEGER NOT NULL,
            file          TEXT
        );

        CREATE TABLE heap_by_type (
            snapshot_id INTEGER NOT NULL REFERENCES heap_snapshot(id),
            type_id     INTEGER NOT NULL REFERENCES type(id),
            count       INTEGER NOT NULL,
            bytes       INTEGER NOT NULL,
            PRIMARY KEY (snapshot_id, type_id)
        );
        """;
}
