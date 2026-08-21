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
  Data.DB;

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
  end;

  TNeighbours = TArray<TNeighbour>;

  /// Where a method lives, as resolved from the build's portable pdbs.
  TMethodSource = record
    FileName: string;
    StartLine: Integer;
    EndLine: Integer;
    Found: Boolean;
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
    function CreateQuery(const ASql: string): TFDQuery;
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
    /// Children of a call-tree node; pass -1 for the roots.
    function TreeChildren(AParentId: Integer): TTreeNodes;
    /// Immediate callers and callees of a method (Details panel).
    function Parents(AMethodId: Integer): TNeighbours;
    function Children(AMethodId: Integer): TNeighbours;
    function Segments: TArray<TSegment>;
    function MethodName(AMethodId: Integer): string;
    /// Source location of a method, when the session was told where the build output is.
    function MethodSource(AMethodId: Integer): TMethodSource;
    function CountOf(const ATable: string): Int64;

    property Path: string read FPath;
    property Mode: TSessionMode read FMode;
    property Package: string read FPackage;
    property Device: string read FDevice;
    property State: string read FState;
    property StartedUtc: string read FStartedUtc;
    property TotalSamples: Int64 read FTotalSamples;
    /// Schema version of the open database; older sessions lack the newer tables.
    property SchemaVersion: Integer read FSchemaVersion;
  end;

function ModeToString(AMode: TSessionMode): string;
/// Nanoseconds as a human-readable duration; the grids show this instead of raw ns.
function FormatNs(ANs: Int64): string;

implementation

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

function FormatNs(ANs: Int64): string;
begin
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
  LQuery := CreateQuery('SELECT mode, state, package, device_serial, started_utc, total_samples FROM session LIMIT 1');
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
  finally
    LQuery.Free;
  end;
end;

function TSessionStore.OpenReport: TFDQuery;
const
  SSampling =
    'SELECT m.id AS method_id, m.full_name, m.module, ' +
    '       s.exclusive_cpu AS self_samples, s.inclusive_cpu AS total_samples, ' +
    '       s.exclusive AS self_wall, s.inclusive AS total_wall ' +
    'FROM sample_stat s JOIN method m ON m.id = s.method_id ' +
    'ORDER BY s.exclusive_cpu DESC, s.inclusive_cpu DESC';
  SInstrumenting =
    'SELECT m.id AS method_id, m.full_name, m.module, t.calls, ' +
    '       t.self_ns, t.total_ns, t.min_ns, t.max_ns, ' +
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
    LSql :=
      'SELECT n.id, n.method_id, COALESCE(m.full_name, ''(thread)'') AS full_name, ' +
      '       n.inclusive, n.exclusive, 0 AS calls, ' +
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
      'SELECT p.method_id, m.full_name, SUM(n.total_ns) AS value ' +
      'FROM timing_tree n JOIN timing_tree p ON p.id = n.parent_id JOIN method m ON m.id = p.method_id ' +
      'WHERE n.method_id = :m GROUP BY p.method_id, m.full_name ORDER BY value DESC')
  else
    LQuery := CreateQuery(
      'SELECT e.caller_method_id AS method_id, m.full_name, e.samples AS value ' +
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
      'SELECT c.method_id, m.full_name, SUM(c.total_ns) AS value ' +
      'FROM timing_tree n JOIN timing_tree c ON c.parent_id = n.id JOIN method m ON m.id = c.method_id ' +
      'WHERE n.method_id = :m GROUP BY c.method_id, m.full_name ORDER BY value DESC')
  else
    LQuery := CreateQuery(
      'SELECT e.callee_method_id AS method_id, m.full_name, e.samples AS value ' +
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
