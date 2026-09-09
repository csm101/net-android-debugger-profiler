program StoreTests;

{
  Checks the GUI's data layer against a real session database, without a UI:
  the queries in uSessionStore are where a schema mismatch would show up first.

    StoreTests <path to session.db> [...]
}

{$APPTYPE CONSOLE}

uses
  System.SysUtils,
  System.IOUtils,
  FireDAC.Comp.Client,
  uSessionStore in '..\src\uSessionStore.pas',
  uControlClient in '..\src\uControlClient.pas',
  uSessionSpec in '..\src\uSessionSpec.pas';

var
  GFailures: Integer = 0;

procedure Check(ACondition: Boolean; const AWhat: string);
begin
  if ACondition then
    Writeln('  ok    ', AWhat)
  else
  begin
    Writeln('  FAIL  ', AWhat);
    Inc(GFailures);
  end;
end;

procedure TestSession(const APath: string);
var
  LStore: TSessionStore;
  LReport: TFDQuery;
  LRoots, LChildren: TTreeNodes;
  LMethodId, LRows: Integer;
  LNeighbours: TNeighbours;
  LTotals: TArray<THeapTotal>;
begin
  Writeln('session: ', APath);
  LStore := TSessionStore.Create;
  try
    LStore.Open(APath);
    Check(LStore.Mode <> smUnknown, 'mode is known: ' + ModeToString(LStore.Mode));
    Check(LStore.Package <> '', 'package: ' + LStore.Package);

    LReport := LStore.OpenReport;
    try
      LRows := 0;
      LMethodId := -1;
      while not LReport.Eof do
      begin
        if (LMethodId < 0) and (LReport.FindField('method_id') <> nil) then
          LMethodId := LReport.FieldByName('method_id').AsInteger;
        Inc(LRows);
        LReport.Next;
      end;
      Check(LRows > 0, Format('report has %d rows', [LRows]));
    finally
      LReport.Free;
    end;

    if LStore.Mode <> smHeapSnapshot then
    begin
      LRoots := LStore.TreeChildren(-1);
      Check(Length(LRoots) > 0, Format('tree has %d roots', [Length(LRoots)]));
      if Length(LRoots) > 0 then
      begin
        // A root is a thread: in an instrumenting session it carries no time of its
        // own, so the value has to show up one level down.
        if LRoots[0].HasChildren then
        begin
          LChildren := LStore.TreeChildren(LRoots[0].Id);
          Check(Length(LChildren) > 0, Format('the busiest root expands to %d children', [Length(LChildren)]));
          Check((LRoots[0].Inclusive > 0) or ((Length(LChildren) > 0) and (LChildren[0].Inclusive > 0)),
            'the tree carries values at or below the root');
        end;
      end;

      if LMethodId >= 0 then
      begin
        Check(LStore.MethodName(LMethodId) <> '', 'the focused method resolves to a name');
        LNeighbours := LStore.Parents(LMethodId);
        LNeighbours := LStore.Children(LMethodId);
        Check(True, Format('details queries ran (%d children of the top method)', [Length(LNeighbours)]));
      end;
    end;

    Check(LStore.CountOf('segment') >= 0, Format('segment history readable (%d rows)', [LStore.CountOf('segment')]));

    // The heap chart reads these directly: a session with snapshots must produce a
    // point per snapshot, and one without must produce none rather than fail.
    LTotals := LStore.HeapTotals;
    Check(Length(LTotals) = LStore.CountOf('heap_snapshot'),
      Format('heap chart has a point per snapshot (%d)', [Length(LTotals)]));
    if Length(LTotals) > 0 then
      Check((LTotals[0].Bytes > 0) and (LTotals[0].Objects > 0), 'the first snapshot carries totals');
  finally
    LStore.Free;
  end;
  Writeln;
end;

{ Running the same measurement again reads what the session recorded rather than what a
  dialog remembers, so the thing to check is that a session.json comes back as the request
  it was started with - and that the next run is offered a name of its own. }
procedure TestRunAgain;
var
  LFolder, LSession: string;
  LRequest: TSessionRequest;
begin
  Writeln('run again');
  LFolder := TPath.Combine(TPath.GetTempPath, 'nap-gui-tests-' + TGuid.NewGuid.ToString);
  LSession := TPath.Combine(LFolder, '20260909-003357-First run-instrumenting');
  TDirectory.CreateDirectory(LSession);
  try
    TFile.WriteAllText(TPath.Combine(LSession, 'session.json'),
      '{ "DeviceSerial": "a-device-serial", "Package": "App.Droid", "Mode": 1, "Engine": 2,' +
      '  "Callspec": "N:App,N:App.Core", "Duration": null, "StartPaused": true,' +
      '  "WeaveAssemblies": [ "App.Droid", "App.Core" ], "Name": "First run",' +
      '  "SymbolsDir": "C:\\Work\\ReferenceApp\\App.Droid\\bin\\Debug\\net9.0-android35.0",' +
      '  "ProjectPath": "C:\\Work\\ReferenceApp\\App.Droid\\App.Droid.csproj",' +
      '  "SolutionPath": "C:\\Work\\ReferenceApp\\ReferenceApp.sln" }');

    Check(TryReadSessionSpec(LSession, LRequest), 'the session says what it was started with');
    Check(LRequest.Package = 'App.Droid', 'package: ' + LRequest.Package);
    Check(LRequest.Mode = 'instrumenting', 'mode: ' + LRequest.Mode);
    Check(LRequest.Engine = 'weaver-tree', 'engine: ' + LRequest.Engine);
    Check(LRequest.Callspec = 'N:App,N:App.Core', 'callspec: ' + LRequest.Callspec);
    Check(Length(LRequest.Assemblies) = 2, 'the assemblies come back');
    Check(LRequest.StartPaused, 'started paused, and stays so');
    Check(LRequest.SolutionPath.EndsWith('ReferenceApp.sln'), 'the solution: ' + LRequest.SolutionPath);
    Check(SameText(LRequest.SessionsRoot, ExcludeTrailingPathDelimiter(LFolder)),
      'the next run goes where this one is kept');
    Check(ProfilerIndexOf(LRequest) = 1, 'the profiler list lands on the call-tree weaver');

    // The proposed name counts on from the one it repeats, and avoids what is already there.
    Check(NextRunName(LRequest.Name, LRequest.Package, LFolder) = 'First run 2', 'a name with no number gets one');
    Check(NextRunName('startup7', 'App.Droid', LFolder) = 'startup8', 'a trailing number counts on');
    Check(NextRunName('', 'App.Droid', LFolder) = 'App.Droid 2', 'no name at all: the package counts');

    Check(not TryReadSessionSpec(TPath.Combine(LFolder, 'nothing-here'), LRequest),
      'a session that recorded nothing says so instead of pretending');
  finally
    TDirectory.Delete(LFolder, True);
  end;
  Writeln;
end;

var
  I: Integer;
begin
  try
    if ParamCount = 0 then
    begin
      Writeln('usage: StoreTests <session.db> [...]');
      Halt(2);
    end;
    TestRunAgain;
    for I := 1 to ParamCount do
      TestSession(ParamStr(I));
    if GFailures = 0 then
      Writeln('all checks passed')
    else
      Writeln(GFailures, ' checks FAILED');
    Halt(Ord(GFailures <> 0));
  except
    on E: Exception do
    begin
      Writeln('EXCEPTION ', E.ClassName, ': ', E.Message);
      Halt(3);
    end;
  end;
end.
