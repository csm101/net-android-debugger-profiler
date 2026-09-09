unit uSessionStore;

{
  Read access to a profiling session database.

  The SQLite file is the contract between the engine and this GUI (schema v2,
  see ARCHITECTURE.md): the profiler writes it, we only read. Opening it while a
  session is still collecting is expected - the engine writes in WAL mode and a
  snapshot rewrites the result tables - so every query re-reads from disk instead
  of caching.
}

interface

uses
  System.SysUtils, System.Classes, System.Generics.Collections,
  FireDAC.Stan.Intf, FireDAC.Stan.Def, FireDAC.Stan.Pool, FireDAC.Stan.Option, FireDAC.Stan.Param, FireDAC.Stan.Error,
  FireDAC.DatS, FireDAC.Phys.Intf, FireDAC.DApt.Intf, FireDAC.Stan.Async,
  FireDAC.Phys, FireDAC.Phys.SQLite, FireDAC.Phys.SQLiteDef, FireDAC.Stan.ExprFuncs,
  FireDAC.Comp.Client, FireDAC.Comp.DataSet, FireDAC.DApt,
  System.IOUtils, System.Generics.Defaults, System.JSON, Data.DB;

type
  ESessionStore = class(Exception);

  TSessionMode = (smUnknown, smSampling, smInstrumenting, smHeapSnapshot);

  /// One node of a call tree, as shown in the Call Tree panel.
  TTreeNode = record
    Id: Integer;
    MethodId: Integer;
    FullName: string;
    /// Inclusive samples, or total nanoseconds for an instrumenting session.
    Inclusive: Int64;
    /// Exclusive samples, or self nanoseconds.
    Exclusive: Int64;
    Calls: Int64;
    HasChildren: Boolean;
  end;

  TTreeNodes = TArray<TTreeNode>;

  /// A method as it appears in the Details panel's Parents/Children tables.
  TNeighbour = record
    MethodId: Integer;
    FullName: string;
    Value: Int64;
    /// Calls on this edge, when the mode counts calls; 0 for sampling, where the number
    /// on an arrow would be samples and not a hit count.
    Calls: Int64;
  end;

  /// What a method is worth, for the box the call graph draws for it. Zero calls means
  /// the mode does not count them rather than "it was never called".
  TMethodStats = record
    Calls: Int64;
    SelfValue: Int64;
    Total: Int64;
  end;

  TNeighbours = TArray<TNeighbour>;

  /// Where a method lives, as resolved from the build's portable pdbs.
  TMethodSource = record
    FileName: string;
    StartLine: Integer;
    EndLine: Integer;
    Found: Boolean;
  end;

  /// One point of the heap chart: how much was alive when a snapshot was taken.
  THeapTotal = record
    Id: Integer;
    Objects: Int64;
    Bytes: Int64;
  end;

  TSegment = record
    Id: Integer;
    TakenUtc: string;
    Kind: string;
    Events: Int64;
  end;

  TSessionStore = class
  private
    FConnection: TFDConnection;
    FPath: string;
    FMode: TSessionMode;
    FPackage: string;
    FDevice: string;
    FState: string;
    FStartedUtc: string;
    FTotalSamples: Int64;
    FSchemaVersion: Integer;
    FSymbolsDir: string;
    FSolution: string;
    FSpecJson: string;
    function CreateQuery(const ASql: string): TFDQuery;
    procedure ReadSpec(const AJson: string);
    function TableExists(const ATable: string): Boolean;
    procedure ReadSessionRow;
  public
    constructor Create;
    destructor Destroy; override;
    procedure Open(const APath: string);
    procedure Close;
    function IsOpen: Boolean;
    /// Re-read the session row: a live session changes state and a snapshot replaces the results.
    procedure Refresh;

    /// Report rows for the session's mode; the caller owns the returned query.
    function OpenReport: TFDQuery;
    /// Allocation rows by type (instrumenting sessions with allocation tracking).
    function OpenAllocationsByType: TFDQuery;
    /// Allocations per type and allocating method: which code made the objects.
    function OpenAllocationsBySite: TFDQuery;
    /// Heap snapshots taken during the session, oldest first.
    function OpenHeapSnapshots: TFDQuery;
    /// Live objects per type in one snapshot, or the growth between two.
    function OpenHeapByType(ASnapshotId: Integer): TFDQuery;
    function OpenHeapGrowth(AFromId, AToId: Integer): TFDQuery;
    /// Ids of the heap snapshots, for the pickers.
    function HeapSnapshotIds: TArray<Integer>;
    /// Totals of every snapshot, oldest first: what the chart draws.
    function HeapTotals: TArray<THeapTotal>;
    /// Children of a call-tree node; pass -1 for the roots.
    function TreeChildren(AParentId: Integer): TTreeNodes;
    /// Immediate callers and callees of a method (Details panel).
    function Parents(AMethodId: Integer): TNeighbours;
    function Children(AMethodId: Integer): TNeighbours;
    function Segments: TArray<TSegment>;
    function MethodName(AMethodId: Integer): string;
    /// Source location of a method, when the session was told where the build output is.
    function MethodSource(AMethodId: Integer): TMethodSource;
    /// The figures of one method, as the call graph puts them in its box.
    function MethodStats(AMethodId: Integer): TMethodStats;
    /// Whether this method calls anything at all: what decides that a graph box gets a
    /// [+] to open, without reading the callees of every box on screen.
    function HasCallees(AMethodId: Integer): Boolean;
    /// Whether this session resolved any source location at all. None, on a session that
    /// had symbols, is a different thing from one method having none: it means the results
    /// were written by a build that could not read them, and only a new run repairs it.
    function HasAnySourceLocations: Boolean;
    /// The method's headline figure: time with children, or inclusive samples.
    function MethodInclusive(AMethodId: Integer): Int64;
    function CountOf(const ATable: string): Int64;

    property Path: string read FPath;
    property Mode: TSessionMode read FMode;
    property Package: string read FPackage;
    property Device: string read FDevice;
    property State: string read FState;
    property StartedUtc: string read FStartedUtc;
    property TotalSamples: Int64 read FTotalSamples;
    /// The build output the session read its symbols from, and the solution it came from,
    /// as the session recorded them. Empty means the session was taken without them - which
    /// is the difference between "this method has no source" and "this session has none".
    property SymbolsDir: string read FSymbolsDir;
    property Solution: string read FSolution;
    /// The whole spec the session was started with, as it was written. What lets the same
    /// measurement be run again without anybody retyping it.
    property SpecJson: string read FSpecJson;
    /// Schema version of the open database; older sessions lack the newer tables.
    property SchemaVersion: Integer read FSchemaVersion;
  end;

/// One session on disk, as listed in the Explorer panel.
type
  TSessionEntry = record
    Id: string;
    DatabasePath: string;
    Mode: string;
    Package: string;
    StartedUtc: string;
    /// What the user called it when starting it, or renamed it to later. Empty means the
    /// session is known by its id, which is what sessions started without a name are.
    Name: string;
    /// The solution and project the app was built from, when the session recorded them:
    /// what lets a list of sessions be read as "my runs of this product" instead of a
    /// wall of timestamps. Sessions taken before this existed have neither.
    Solution: string;
    Project: string;
  end;

  TSessionEntries = TArray<TSessionEntry>;

  /// Results kept under a name while a session went on (its Archive button). An archive
  /// is an ordinary result database, so it opens exactly like a session - which is what
  /// makes it readable long after that session ended.
  TArchiveEntry = record
    Name: string;
    DatabasePath: string;
    CreatedUtc: string;
  end;

  TArchiveEntries = TArray<TArchiveEntry>;

/// Sessions under a sessions root, newest first (a session directory holds session.db).
function ListSessions(const ARoot: string): TSessionEntries;

/// The archives of one session directory, newest first. Read from disk, not from the
/// control service: browsing yesterday's results must not need a running profiler.
function ListArchives(const ASessionDirectory: string): TArchiveEntries;

function ModeToString(AMode: TSessionMode): string;

type
  /// How times are shown; Auto picks the readable unit per value.
  TTimeUnit = (tuAuto, tuSeconds, tuMilliseconds, tuMicroseconds, tuNanoseconds);

var
  /// The unit every panel formats with. One setting, so the numbers can be compared.
  GTimeUnit: TTimeUnit = tuAuto;

/// Nanoseconds as a human-readable duration; the grids show this instead of raw ns.
function FormatNs(ANs: Int64): string;
function TimeUnitName(AUnit: TTimeUnit): string;

implementation

/// The mode and package from the session's own session.json. False when there is none,
/// which is the caller's cue to fall back to the database.
function ReadIdentityFromJson(const ADirectory: string; var AEntry: TSessionEntry): Boolean;
var
  LPath: string;
  LJson: TJSONValue;
  LObject: TJSONObject;
  LValue: TJSONValue;
  LMode: string;
begin
  Result := False;
  LPath := TPath.Combine(ADirectory, 'session.json');
  if not TFile.Exists(LPath) then
    Exit;
  try
    LJson := TJSONObject.ParseJSONValue(TFile.ReadAllText(LPath));
  except
    Exit;
  end;
  try
    if not (LJson is TJSONObject) then
      Exit;
    LObject := TJSONObject(LJson);
    // The spec serializes the mode as the enum's ordinal, not its name: 0 sampling,
    // 1 instrumenting, 2 heap snapshot (Core.Apps.ProfilingMode). A name is accepted too,
    // so a hand-written or future spec still reads.
    LValue := LObject.GetValue('Mode');
    if LValue = nil then
      LValue := LObject.GetValue('mode');
    if LValue = nil then
      Exit;
    LMode := LValue.Value;
    if LMode = '0' then
      AEntry.Mode := 'Sampling'
    else if LMode = '1' then
      AEntry.Mode := 'Instrumenting'
    else if LMode = '2' then
      AEntry.Mode := 'Heap snapshot'
    else if SameText(LMode, 'HeapSnapshot') then
      AEntry.Mode := 'Heap snapshot'
    else if LMode = '' then
      Exit
    else
      AEntry.Mode := LMode;
    AEntry.Package := LObject.GetValue<string>('Package', LObject.GetValue<string>('package', ''));
    AEntry.Name := LObject.GetValue<string>('Name', LObject.GetValue<string>('name', ''));
    AEntry.Solution := LObject.GetValue<string>('SolutionPath', LObject.GetValue<string>('solutionPath', ''));
    AEntry.Project := LObject.GetValue<string>('ProjectPath', LObject.GetValue<string>('projectPath', ''));
    Result := True;
  finally
    LJson.Free;
  end;
end;

function ListSessions(const ARoot: string): TSessionEntries;
var
  LDirectories: TArray<string>;
  LList: TList<TSessionEntry>;
  LEntry: TSessionEntry;
  LStore: TSessionStore;
  I: Integer;
begin
  LList := TList<TSessionEntry>.Create;
  try
    if not TDirectory.Exists(ARoot) then
      Exit(nil);
    LDirectories := TDirectory.GetDirectories(ARoot);
    TArray.Sort<string>(LDirectories);
    for I := High(LDirectories) downto Low(LDirectories) do    // newest first
    begin
      LEntry := Default(TSessionEntry);
      LEntry.DatabasePath := TPath.Combine(LDirectories[I], 'session.db');
      if not TFile.Exists(LEntry.DatabasePath) then
        Continue;
      LEntry.Id := TPath.GetFileName(LDirectories[I]);
      // The identity comes from session.json, which the session writes before it runs:
      // opening every session.db to read three fields is what made the window take
      // seconds to appear once a few dozen sessions had piled up. The database is only
      // opened when that file is missing.
      if not ReadIdentityFromJson(LDirectories[I], LEntry) then
      begin
        LStore := TSessionStore.Create;
        try
          try
            LStore.Open(LEntry.DatabasePath);
            LEntry.Mode := ModeToString(LStore.Mode);
            LEntry.Package := LStore.Package;
            LEntry.StartedUtc := LStore.StartedUtc;
          except
            LEntry.Mode := '(unreadable)';
          end;
        finally
          LStore.Free;
        end;
      end;
      LList.Add(LEntry);
    end;
    Result := LList.ToArray;
  finally
    LList.Free;
  end;
end;

function ListArchives(const ASessionDirectory: string): TArchiveEntries;
var
  LFolder: string;
  LFiles: TArray<string>;
  LList: TList<TArchiveEntry>;
  LEntry: TArchiveEntry;
  LJson: TJSONValue;
  I: Integer;
begin
  LFolder := TPath.Combine(ASessionDirectory, 'archives');
  if not TDirectory.Exists(LFolder) then
    Exit(nil);
  LList := TList<TArchiveEntry>.Create;
  try
    LFiles := TDirectory.GetFiles(LFolder, '*.db');
    TArray.Sort<string>(LFiles);
    for I := Low(LFiles) to High(LFiles) do
    begin
      LEntry := Default(TArchiveEntry);
      LEntry.DatabasePath := LFiles[I];
      // The name the user gave it lives in the sidecar; the file name is the fallback,
      // so an archive whose sidecar was lost is still listed and still opens.
      LEntry.Name := TPath.GetFileNameWithoutExtension(LFiles[I]);
      if TFile.Exists(TPath.ChangeExtension(LFiles[I], '.json')) then
        try
          LJson := TJSONObject.ParseJSONValue(TFile.ReadAllText(TPath.ChangeExtension(LFiles[I], '.json')));
          try
            if LJson is TJSONObject then
            begin
              // The engine writes the sidecar PascalCase and the control service speaks
              // camelCase: read both, so a policy on either side cannot empty this.
              LEntry.Name := TJSONObject(LJson).GetValue<string>('Name',
                TJSONObject(LJson).GetValue<string>('name', LEntry.Name));
              LEntry.CreatedUtc := TJSONObject(LJson).GetValue<string>('CreatedUtc',
                TJSONObject(LJson).GetValue<string>('createdUtc', ''));
            end;
          finally
            LJson.Free;
          end;
        except
          // a broken sidecar costs the pretty name, nothing else
        end;
      LList.Add(LEntry);
    end;
    LList.Sort(TComparer<TArchiveEntry>.Construct(
      function(const A, B: TArchiveEntry): Integer
      begin
        Result := CompareStr(B.CreatedUtc, A.CreatedUtc);       // newest first
        if Result = 0 then
          Result := CompareText(A.Name, B.Name);
      end));
    Result := LList.ToArray;
  finally
    LList.Free;
  end;
end;

function ModeToString(AMode: TSessionMode): string;
begin
  case AMode of
    smSampling: Result := 'Sampling';
    smInstrumenting: Result := 'Instrumenting';
    smHeapSnapshot: Result := 'Heap snapshot';
  else
    Result := 'Unknown';
  end;
end;

function TimeUnitName(AUnit: TTimeUnit): string;
begin
  case AUnit of
    tuSeconds: Result := 'Seconds';
    tuMilliseconds: Result := 'Milliseconds';
    tuMicroseconds: Result := 'Microseconds';
    tuNanoseconds: Result := 'Nanoseconds';
  else
    Result := 'Automatic';
  end;
end;

function FormatNs(ANs: Int64): string;
begin
  case GTimeUnit of
    tuSeconds: Exit(FormatFloat('0.000 s', ANs / 1000000000));
    tuMilliseconds: Exit(FormatFloat('0.000 ms', ANs / 1000000));
    tuMicroseconds: Exit(FormatFloat('0.0 us', ANs / 1000));
    tuNanoseconds: Exit(IntToStr(ANs) + ' ns');
  end;
  if ANs >= 1000000000 then
    Result := FormatFloat('0.000 s', ANs / 1000000000)
  else if ANs >= 1000000 then
    Result := FormatFloat('0.00 ms', ANs / 1000000)
  else if ANs >= 1000 then
    Result := FormatFloat('0.0 us', ANs / 1000)
  else
    Result := IntToStr(ANs) + ' ns';
end;

constructor TSessionStore.Create;
begin
  inherited Create;
  FConnection := TFDConnection.Create(nil);
  FConnection.LoginPrompt := False;
end;

destructor TSessionStore.Destroy;
begin
  FConnection.Free;
  inherited Destroy;
end;

procedure TSessionStore.Open(const APath: string);
begin
  if not FileExists(APath) then
    raise ESessionStore.CreateFmt('Session database not found: %s', [APath]);
  Close;
  FConnection.Params.Clear;
  FConnection.Params.Add('DriverID=SQLite');
  FConnection.Params.Add('Database=' + APath);
  // Read-only, and never take a lock the engine would block on: a session may be
  // running and rewriting these tables as we read them.
  FConnection.Params.Add('OpenMode=ReadOnly');
  FConnection.Params.Add('LockingMode=Normal');
  FConnection.Params.Add('SharedCache=False');
  // SQLite has no fixed column widths, so FireDAC believes the declared type: an
  // INTEGER column becomes a 32-bit field and a nanosecond total wraps into a negative
  // number. The schema declares the wide columns BIGINT, and this makes older session
  // files safe as well.
  FConnection.FormatOptions.OwnMapRules := True;
  FConnection.FormatOptions.MapRules.Clear;
  with FConnection.FormatOptions.MapRules.Add do
  begin
    SourceDataType := dtInt32;
    TargetDataType := dtInt64;
  end;
  FConnection.Connected := True;
  FPath := APath;
  FSchemaVersion := 0;
  ReadSessionRow;
end;

procedure TSessionStore.Close;
begin
  FConnection.Connected := False;
  FPath := '';
  FMode := smUnknown;
  FPackage := '';
  FSymbolsDir := '';
  FSolution := '';
  FSpecJson := '';
  FDevice := '';
  FState := '';
  FStartedUtc := '';
  FTotalSamples := 0;
end;

function TSessionStore.IsOpen: Boolean;
begin
  Result := FConnection.Connected;
end;

procedure TSessionStore.Refresh;
begin
  if IsOpen then
    ReadSessionRow;
end;

function TSessionStore.CreateQuery(const ASql: string): TFDQuery;
begin
  Result := TFDQuery.Create(nil);
  try
    Result.Connection := FConnection;
    Result.SQL.Text := ASql;
  except
    Result.Free;
    raise;
  end;
end;

function TSessionStore.TableExists(const ATable: string): Boolean;
var
  LQuery: TFDQuery;
begin
  LQuery := CreateQuery('SELECT COUNT(*) FROM sqlite_master WHERE type = ''table'' AND name = :n');
  try
    LQuery.ParamByName('n').AsString := ATable;
    LQuery.Open;
    Result := LQuery.Fields[0].AsInteger > 0;
  finally
    LQuery.Free;
  end;
end;

procedure TSessionStore.ReadSessionRow;
var
  LQuery: TFDQuery;
  LMode: string;
begin
  LQuery := CreateQuery('SELECT version FROM schema_info LIMIT 1');
  try
    LQuery.Open;
    if not LQuery.Eof then
      FSchemaVersion := LQuery.Fields[0].AsInteger;
  finally
    LQuery.Free;
  end;
  LQuery := CreateQuery('SELECT mode, state, package, device_serial, started_utc, total_samples, spec_json FROM session LIMIT 1');
  try
    LQuery.Open;
    if LQuery.Eof then
    begin
      FMode := smUnknown;
      Exit;
    end;
    LMode := LQuery.FieldByName('mode').AsString;
    if SameText(LMode, 'Sampling') then
      FMode := smSampling
    else if SameText(LMode, 'Instrumenting') then
      FMode := smInstrumenting
    else if SameText(LMode, 'HeapSnapshot') then
      FMode := smHeapSnapshot
    else
      FMode := smUnknown;
    FState := LQuery.FieldByName('state').AsString;
    FPackage := LQuery.FieldByName('package').AsString;
    FDevice := LQuery.FieldByName('device_serial').AsString;
    FStartedUtc := LQuery.FieldByName('started_utc').AsString;
    FTotalSamples := LQuery.FieldByName('total_samples').AsLargeInt;
    ReadSpec(LQuery.FieldByName('spec_json').AsString);
  finally
    LQuery.Free;
  end;
end;

{ The session keeps its whole spec in the database, which is what lets a result opened
  months later still say what it was taken with. Only the two things a reader needs are
  pulled out of it here. }
procedure TSessionStore.ReadSpec(const AJson: string);
var
  LValue: TJSONValue;
begin
  FSymbolsDir := '';
  FSolution := '';
  FSpecJson := AJson;
  if Trim(AJson) = '' then
    Exit;
  LValue := TJSONObject.ParseJSONValue(AJson);
  try
    if not (LValue is TJSONObject) then
      Exit;
    FSymbolsDir := TJSONObject(LValue).GetValue<string>('SymbolsDir', '');
    FSolution := TJSONObject(LValue).GetValue<string>('SolutionPath', '');
  finally
    LValue.Free;
  end;
end;

function TSessionStore.OpenReport: TFDQuery;
const
  SSampling =
    'SELECT m.id AS method_id, m.full_name, m.module, ' +
    '       s.exclusive_cpu AS self_samples, s.inclusive_cpu AS total_samples, ' +
    '       100.0 * s.exclusive_cpu / NULLIF((SELECT SUM(exclusive_cpu) FROM sample_stat), 0) AS pct_self, ' +
    '       100.0 * s.inclusive_cpu / NULLIF((SELECT MAX(inclusive_cpu) FROM sample_stat), 0) AS pct_total, ' +
    '       s.exclusive AS self_wall, s.inclusive AS total_wall ' +
    'FROM sample_stat s JOIN method m ON m.id = s.method_id ' +
    'ORDER BY s.exclusive_cpu DESC, s.inclusive_cpu DESC';
  SInstrumenting =
    'SELECT m.id AS method_id, m.full_name, m.module, t.calls, ' +
    '       t.self_ns, t.total_ns, ' +
    '       100.0 * t.self_ns / NULLIF((SELECT SUM(self_ns) FROM timing_stat), 0) AS pct_self, ' +
    '       100.0 * t.total_ns / NULLIF((SELECT MAX(total_ns) FROM timing_stat), 0) AS pct_total, ' +
    '       t.min_ns, t.max_ns, ' +
    '       CASE WHEN t.calls > 0 THEN t.total_ns / t.calls ELSE 0 END AS avg_ns, ' +
    '       t.exception_leaves ' +
    'FROM timing_stat t JOIN method m ON m.id = t.method_id ' +
    'ORDER BY t.self_ns DESC';
  SHeap =
    'SELECT t.name AS full_name, h.count, h.bytes ' +
    'FROM heap_by_type h JOIN type t ON t.id = h.type_id ' +
    'WHERE h.snapshot_id = (SELECT MAX(id) FROM heap_snapshot) ' +
    'ORDER BY h.bytes DESC';
begin
  case FMode of
    smSampling: Result := CreateQuery(SSampling);
    smInstrumenting: Result := CreateQuery(SInstrumenting);
    smHeapSnapshot: Result := CreateQuery(SHeap);
  else
    raise ESessionStore.Create('This session has no results to report.');
  end;
  Result.Open;
end;

function TSessionStore.OpenAllocationsByType: TFDQuery;
begin
  Result := CreateQuery(
    'SELECT t.name AS type_name, a.count, a.bytes ' +
    'FROM alloc_by_type a JOIN type t ON t.id = a.type_id ' +
    'ORDER BY a.bytes DESC');
  Result.Open;
end;

function TSessionStore.OpenAllocationsBySite: TFDQuery;
begin
  Result := CreateQuery(
    'SELECT COALESCE(m.full_name, ''(no instrumented frame)'') AS allocating_method, ' +
    '       t.name AS type_name, a.count, a.bytes ' +
    'FROM alloc_by_site a JOIN type t ON t.id = a.type_id ' +
    'LEFT JOIN method m ON m.id = a.method_id ' +
    'ORDER BY a.count DESC');
  Result.Open;
end;

function TSessionStore.OpenHeapSnapshots: TFDQuery;
begin
  Result := CreateQuery(
    'SELECT id, taken_utc, total_objects, total_bytes FROM heap_snapshot ORDER BY id');
  Result.Open;
end;

function TSessionStore.OpenHeapByType(ASnapshotId: Integer): TFDQuery;
begin
  Result := CreateQuery(
    'SELECT t.name AS type_name, h.count, h.bytes ' +
    'FROM heap_by_type h JOIN type t ON t.id = h.type_id ' +
    'WHERE h.snapshot_id = :s ORDER BY h.bytes DESC');
  Result.ParamByName('s').AsInteger := ASnapshotId;
  Result.Open;
end;

function TSessionStore.OpenHeapGrowth(AFromId, AToId: Integer): TFDQuery;
begin
  // What grew between two snapshots is the question a leak hunt starts from, so the
  // difference is computed here rather than eyeballed across two grids.
  Result := CreateQuery(
    'SELECT t.name AS type_name, ' +
    '       COALESCE(a.count, 0) AS count_from, COALESCE(b.count, 0) AS count_to, ' +
    '       COALESCE(b.count, 0) - COALESCE(a.count, 0) AS delta_objects, ' +
    '       COALESCE(b.bytes, 0) - COALESCE(a.bytes, 0) AS delta_bytes ' +
    'FROM type t ' +
    'LEFT JOIN heap_by_type a ON a.type_id = t.id AND a.snapshot_id = :f ' +
    'LEFT JOIN heap_by_type b ON b.type_id = t.id AND b.snapshot_id = :t ' +
    'WHERE a.type_id IS NOT NULL OR b.type_id IS NOT NULL ' +
    'ORDER BY delta_bytes DESC');
  Result.ParamByName('f').AsInteger := AFromId;
  Result.ParamByName('t').AsInteger := AToId;
  Result.Open;
end;

function TSessionStore.HeapSnapshotIds: TArray<Integer>;
var
  LQuery: TFDQuery;
  LList: TList<Integer>;
begin
  LList := TList<Integer>.Create;
  LQuery := CreateQuery('SELECT id FROM heap_snapshot ORDER BY id');
  try
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LList.Add(LQuery.Fields[0].AsInteger);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LQuery.Free;
    LList.Free;
  end;
end;

function TSessionStore.HeapTotals: TArray<THeapTotal>;
var
  LQuery: TFDQuery;
  LList: TList<THeapTotal>;
  LItem: THeapTotal;
begin
  LList := TList<THeapTotal>.Create;
  LQuery := CreateQuery('SELECT id, total_objects, total_bytes FROM heap_snapshot ORDER BY id');
  try
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LItem.Id := LQuery.Fields[0].AsInteger;
      LItem.Objects := LQuery.Fields[1].AsLargeInt;
      LItem.Bytes := LQuery.Fields[2].AsLargeInt;
      LList.Add(LItem);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LQuery.Free;
    LList.Free;
  end;
end;

function TSessionStore.TreeChildren(AParentId: Integer): TTreeNodes;
var
  LQuery: TFDQuery;
  LSql: string;
  LList: TList<TTreeNode>;
  LNode: TTreeNode;
begin
  if FMode = smInstrumenting then
    LSql :=
      'SELECT n.id, n.method_id, COALESCE(m.full_name, ''(thread)'') AS full_name, ' +
      '       n.total_ns AS inclusive, n.self_ns AS exclusive, n.calls, ' +
      '       EXISTS(SELECT 1 FROM timing_tree c WHERE c.parent_id = n.id) AS has_children ' +
      'FROM timing_tree n LEFT JOIN method m ON m.id = n.method_id '
  else
    // CPU samples, not wall: the "longest" path through a thread parked in a wait is
    // not a bottleneck, and the critical path is drawn from these numbers.
    LSql :=
      'SELECT n.id, n.method_id, COALESCE(m.full_name, ''(thread)'') AS full_name, ' +
      '       n.inclusive_cpu AS inclusive, n.exclusive_cpu AS exclusive, 0 AS calls, ' +
      '       EXISTS(SELECT 1 FROM sample_tree c WHERE c.parent_id = n.id) AS has_children ' +
      'FROM sample_tree n LEFT JOIN method m ON m.id = n.method_id ';

  if AParentId < 0 then
    LSql := LSql + 'WHERE n.parent_id IS NULL ORDER BY inclusive DESC'
  else
    LSql := LSql + 'WHERE n.parent_id = :parent ORDER BY inclusive DESC';

  LQuery := CreateQuery(LSql);
  LList := TList<TTreeNode>.Create;
  try
    if AParentId >= 0 then
      LQuery.ParamByName('parent').AsInteger := AParentId;
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LNode.Id := LQuery.FieldByName('id').AsInteger;
      LNode.MethodId := LQuery.FieldByName('method_id').AsInteger;
      LNode.FullName := LQuery.FieldByName('full_name').AsString;
      LNode.Inclusive := LQuery.FieldByName('inclusive').AsLargeInt;
      LNode.Exclusive := LQuery.FieldByName('exclusive').AsLargeInt;
      LNode.Calls := LQuery.FieldByName('calls').AsLargeInt;
      LNode.HasChildren := LQuery.FieldByName('has_children').AsInteger <> 0;
      LList.Add(LNode);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LList.Free;
    LQuery.Free;
  end;
end;

function TSessionStore.Parents(AMethodId: Integer): TNeighbours;
var
  LQuery: TFDQuery;
  LList: TList<TNeighbour>;
  LItem: TNeighbour;
begin
  if FMode = smInstrumenting then
    LQuery := CreateQuery(
      'SELECT p.method_id, m.full_name, SUM(n.total_ns) AS value, SUM(n.calls) AS calls ' +
      'FROM timing_tree n JOIN timing_tree p ON p.id = n.parent_id JOIN method m ON m.id = p.method_id ' +
      'WHERE n.method_id = :m GROUP BY p.method_id, m.full_name ORDER BY value DESC')
  else
    LQuery := CreateQuery(
      'SELECT e.caller_method_id AS method_id, m.full_name, e.samples AS value, 0 AS calls ' +
      'FROM sample_edge e JOIN method m ON m.id = e.caller_method_id ' +
      'WHERE e.callee_method_id = :m ORDER BY value DESC');
  LList := TList<TNeighbour>.Create;
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LItem.MethodId := LQuery.FieldByName('method_id').AsInteger;
      LItem.FullName := LQuery.FieldByName('full_name').AsString;
      LItem.Value := LQuery.FieldByName('value').AsLargeInt;
      LItem.Calls := LQuery.FieldByName('calls').AsLargeInt;
      LList.Add(LItem);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LList.Free;
    LQuery.Free;
  end;
end;

function TSessionStore.Children(AMethodId: Integer): TNeighbours;
var
  LQuery: TFDQuery;
  LList: TList<TNeighbour>;
  LItem: TNeighbour;
begin
  if FMode = smInstrumenting then
    LQuery := CreateQuery(
      'SELECT c.method_id, m.full_name, SUM(c.total_ns) AS value, SUM(c.calls) AS calls ' +
      'FROM timing_tree n JOIN timing_tree c ON c.parent_id = n.id JOIN method m ON m.id = c.method_id ' +
      'WHERE n.method_id = :m GROUP BY c.method_id, m.full_name ORDER BY value DESC')
  else
    LQuery := CreateQuery(
      'SELECT e.callee_method_id AS method_id, m.full_name, e.samples AS value, 0 AS calls ' +
      'FROM sample_edge e JOIN method m ON m.id = e.callee_method_id ' +
      'WHERE e.caller_method_id = :m ORDER BY value DESC');
  LList := TList<TNeighbour>.Create;
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LItem.MethodId := LQuery.FieldByName('method_id').AsInteger;
      LItem.FullName := LQuery.FieldByName('full_name').AsString;
      LItem.Value := LQuery.FieldByName('value').AsLargeInt;
      LItem.Calls := LQuery.FieldByName('calls').AsLargeInt;
      LList.Add(LItem);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LList.Free;
    LQuery.Free;
  end;
end;

function TSessionStore.Segments: TArray<TSegment>;
var
  LQuery: TFDQuery;
  LList: TList<TSegment>;
  LItem: TSegment;
begin
  // Sessions written before schema v2 have no history table: an older file is worth
  // opening read-only, it just has nothing to say about snapshots.
  if not TableExists('segment') then
    Exit(nil);
  LQuery := CreateQuery('SELECT id, taken_utc, kind, events FROM segment ORDER BY id');
  LList := TList<TSegment>.Create;
  try
    LQuery.Open;
    while not LQuery.Eof do
    begin
      LItem.Id := LQuery.FieldByName('id').AsInteger;
      LItem.TakenUtc := LQuery.FieldByName('taken_utc').AsString;
      LItem.Kind := LQuery.FieldByName('kind').AsString;
      LItem.Events := LQuery.FieldByName('events').AsLargeInt;
      LList.Add(LItem);
      LQuery.Next;
    end;
    Result := LList.ToArray;
  finally
    LList.Free;
    LQuery.Free;
  end;
end;

function TSessionStore.MethodName(AMethodId: Integer): string;
var
  LQuery: TFDQuery;
begin
  LQuery := CreateQuery('SELECT full_name FROM method WHERE id = :m');
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    if LQuery.Eof then
      Result := ''
    else
      Result := LQuery.Fields[0].AsString;
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.MethodInclusive(AMethodId: Integer): Int64;
var
  LQuery: TFDQuery;
begin
  Result := 0;
  if FMode = smInstrumenting then
    LQuery := CreateQuery('SELECT total_ns FROM timing_stat WHERE method_id = :m')
  else if FMode = smSampling then
    LQuery := CreateQuery('SELECT inclusive_cpu FROM sample_stat WHERE method_id = :m')
  else
    Exit;
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    if not LQuery.Eof then
      Result := LQuery.Fields[0].AsLargeInt;
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.MethodSource(AMethodId: Integer): TMethodSource;
var
  LQuery: TFDQuery;
begin
  Result := Default(TMethodSource);
  // Sessions recorded before schema v4 have no such columns, and a session run without
  // a symbols directory has them empty: both mean "no source to show".
  if FSchemaVersion < 4 then
    Exit;
  LQuery := CreateQuery('SELECT source_file, source_start_line, source_end_line FROM method WHERE id = :m');
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    if LQuery.Eof or LQuery.Fields[0].IsNull then
      Exit;
    Result.FileName := LQuery.Fields[0].AsString;
    Result.StartLine := LQuery.Fields[1].AsInteger;
    Result.EndLine := LQuery.Fields[2].AsInteger;
    Result.Found := Result.FileName <> '';
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.HasCallees(AMethodId: Integer): Boolean;
var
  LQuery: TFDQuery;
begin
  if FMode = smInstrumenting then
    LQuery := CreateQuery(
      'SELECT 1 FROM timing_tree n JOIN timing_tree c ON c.parent_id = n.id WHERE n.method_id = :m LIMIT 1')
  else
    LQuery := CreateQuery('SELECT 1 FROM sample_edge WHERE caller_method_id = :m LIMIT 1');
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    Result := not LQuery.Eof;
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.MethodStats(AMethodId: Integer): TMethodStats;
var
  LQuery: TFDQuery;
begin
  Result := Default(TMethodStats);
  if FMode = smInstrumenting then
    LQuery := CreateQuery('SELECT calls, self_ns AS self_value, total_ns AS total FROM timing_stat WHERE method_id = :m')
  else
    LQuery := CreateQuery('SELECT 0 AS calls, exclusive_cpu AS self_value, inclusive_cpu AS total FROM sample_stat WHERE method_id = :m');
  try
    LQuery.ParamByName('m').AsInteger := AMethodId;
    LQuery.Open;
    if LQuery.Eof then
      Exit;
    Result.Calls := LQuery.FieldByName('calls').AsLargeInt;
    Result.SelfValue := LQuery.FieldByName('self_value').AsLargeInt;
    Result.Total := LQuery.FieldByName('total').AsLargeInt;
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.HasAnySourceLocations: Boolean;
var
  LQuery: TFDQuery;
begin
  Result := False;
  if FSchemaVersion < 4 then
    Exit;
  LQuery := CreateQuery('SELECT 1 FROM method WHERE source_file IS NOT NULL AND source_file <> '''' LIMIT 1');
  try
    LQuery.Open;
    Result := not LQuery.Eof;
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.CountOf(const ATable: string): Int64;
var
  LQuery: TFDQuery;
begin
  if not TableExists(ATable) then
    Exit(0);
  LQuery := CreateQuery('SELECT COUNT(*) FROM ' + ATable);
  try
    LQuery.Open;
    Result := LQuery.Fields[0].AsLargeInt;
  finally
    LQuery.Free;
  end;
end;

end.
