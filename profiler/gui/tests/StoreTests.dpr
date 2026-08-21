program StoreTests;

{
  Checks the GUI's data layer against a real session database, without a UI:
  the queries in uSessionStore are where a schema mismatch would show up first.

    StoreTests <path to session.db> [...]
}

{$APPTYPE CONSOLE}

uses
  System.SysUtils,
  FireDAC.Comp.Client,
  uSessionStore in '..\src\uSessionStore.pas';

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
  finally
    LStore.Free;
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
