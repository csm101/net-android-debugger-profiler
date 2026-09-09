unit uSessionSpec;

{
  Reading back what a session was started with.

  Every session writes its whole spec next to its results (session.json, and a copy in the
  database), which is what makes a recording repeatable: the way to run the same
  measurement again is not to remember what was typed into a dialog, it is to read what
  the session itself recorded. The shape it comes back in is TSessionRequest - the same
  record the setup dialog fills in and the control service is asked with - so "run this
  one again" is the dialog opened on a request that already exists.
}

interface

uses
  System.SysUtils, System.IOUtils, System.JSON, System.StrUtils, System.Character,
  uControlClient, uSessionStore;

/// The spec of a session, read from its directory (session.json, or the copy the result
/// database keeps when the file is gone). False when neither can be read.
function TryReadSessionSpec(const ASessionDirectory: string; out ARequest: TSessionRequest): Boolean;

/// Which entry of the setup dialog's list of profilers a request is: the dialog offers one
/// list rather than a mode and an engine to combine, so this is where the two are folded.
function ProfilerIndexOf(const ARequest: TSessionRequest): Integer;

/// A name for the next run of the same measurement: the trailing number moved on, or a
/// number added, and never one already in use in that folder.
function NextRunName(const AName, APackage, ASessionsFolder: string): string;

implementation

{ The spec records a mode and an engine as numbers (ProfilingMode 0 sampling,
  1 instrumenting, 2 heap; InstrumentingEngine 0 provider, 1 weaver, 2 weaver-tree,
  3 auto); the rest of the GUI speaks in the names the service takes. }
function ProfilerIndexOf(const ARequest: TSessionRequest): Integer;
begin
  if SameText(ARequest.Mode, 'heap') then
    Exit(5);
  if not SameText(ARequest.Mode, 'instrumenting') then
    Exit(0);
  if SameText(ARequest.Engine, 'provider') then
    Exit(3);
  if SameText(ARequest.Engine, 'weaver') then
    Exit(2);
  if SameText(ARequest.Engine, 'auto') then
    Exit(4);
  Result := 1;
end;

function ModeName(AMode: Integer): string;
begin
  case AMode of
    1: Result := 'instrumenting';
    2: Result := 'heap';
  else
    Result := 'sampling';
  end;
end;

function EngineName(AMode, AEngine: Integer): string;
begin
  Result := '';
  if AMode <> 1 then
    Exit;
  case AEngine of
    0: Result := 'provider';
    1: Result := 'weaver';
    3: Result := 'auto';
  else
    Result := 'weaver-tree';
  end;
end;

/// Durations are written as a TimeSpan ("00:01:30"); anything unreadable means "no limit",
/// which is what a session without a duration is.
function SecondsOf(const ATimeSpan: string): Integer;
var
  LParts: TArray<string>;
begin
  Result := 0;
  LParts := ATimeSpan.Split([':']);
  if Length(LParts) <> 3 then
    Exit;
  Result := StrToIntDef(LParts[0], 0) * 3600 + StrToIntDef(LParts[1], 0) * 60
    + Trunc(StrToFloatDef(LParts[2].Replace('.', FormatSettings.DecimalSeparator), 0));
end;

function StringsOf(AObject: TJSONObject; const AName: string): TArray<string>;
var
  LArray: TJSONArray;
  I: Integer;
begin
  Result := nil;
  if not (AObject.GetValue(AName) is TJSONArray) then
    Exit;
  LArray := TJSONArray(AObject.GetValue(AName));
  for I := 0 to LArray.Count - 1 do
    Result := Result + [LArray.Items[I].Value];
end;

function ParseSpec(const AJson: string; out ARequest: TSessionRequest): Boolean;
var
  LValue: TJSONValue;
  LObject: TJSONObject;
  LMode, LEngine: Integer;
begin
  Result := False;
  ARequest := Default(TSessionRequest);
  if Trim(AJson) = '' then
    Exit;
  try
    LValue := TJSONObject.ParseJSONValue(AJson);
  except
    Exit;
  end;
  try
    if not (LValue is TJSONObject) then
      Exit;
    LObject := TJSONObject(LValue);
    LMode := StrToIntDef(LObject.GetValue<string>('Mode', '0'), 0);
    LEngine := StrToIntDef(LObject.GetValue<string>('Engine', '3'), 3);
    ARequest.DeviceSerial := LObject.GetValue<string>('DeviceSerial', '');
    ARequest.Package := LObject.GetValue<string>('Package', '');
    ARequest.Mode := ModeName(LMode);
    ARequest.Engine := EngineName(LMode, LEngine);
    ARequest.Callspec := LObject.GetValue<string>('Callspec', '');
    ARequest.DurationSeconds := SecondsOf(LObject.GetValue<string>('Duration', ''));
    ARequest.SymbolsDir := LObject.GetValue<string>('SymbolsDir', '');
    ARequest.Assemblies := StringsOf(LObject, 'WeaveAssemblies');
    ARequest.WeaveMapPath := LObject.GetValue<string>('WeaveMapPath', '');
    ARequest.Name := LObject.GetValue<string>('Name', '');
    ARequest.SolutionPath := LObject.GetValue<string>('SolutionPath', '');
    ARequest.ProjectPath := LObject.GetValue<string>('ProjectPath', '');
    ARequest.StartPaused := SameText(LObject.GetValue<string>('StartPaused', 'false'), 'true');
    Result := ARequest.Package <> '';
  finally
    LValue.Free;
  end;
end;

function TryReadSessionSpec(const ASessionDirectory: string; out ARequest: TSessionRequest): Boolean;
var
  LFile: string;
  LStore: TSessionStore;
begin
  ARequest := Default(TSessionRequest);
  if ASessionDirectory = '' then
    Exit(False);
  LFile := TPath.Combine(ASessionDirectory, 'session.json');
  if TFile.Exists(LFile) then
    try
      if ParseSpec(TFile.ReadAllText(LFile), ARequest) then
      begin
        // A session is kept where it is: running it again puts the new one beside it.
        ARequest.SessionsRoot := TDirectory.GetParent(ASessionDirectory);
        Exit(True);
      end;
    except
      // an unreadable file is one source among two, not a failure
    end;
  // The database carries a copy of the spec, which is what an archive - or a session whose
  // json somebody deleted - still has.
  LFile := TPath.Combine(ASessionDirectory, 'session.db');
  if not TFile.Exists(LFile) then
    Exit(False);
  LStore := TSessionStore.Create;
  try
    try
      LStore.Open(LFile);
      Result := ParseSpec(LStore.SpecJson, ARequest);
      if Result then
        ARequest.SessionsRoot := TDirectory.GetParent(ASessionDirectory);
    except
      Result := False;
    end;
  finally
    LStore.Free;
  end;
end;

/// The names already used by the sessions of a folder, so a proposed one can avoid them.
function NamesIn(const AFolder: string): TArray<string>;
var
  LSessions: TSessionEntries;
  I: Integer;
begin
  Result := nil;
  LSessions := ListSessions(AFolder);
  for I := 0 to High(LSessions) do
    if LSessions[I].Name <> '' then
      Result := Result + [LSessions[I].Name];
end;

function IsTaken(const AName: string; const ATaken: TArray<string>): Boolean;
begin
  for var LName in ATaken do
    if SameText(LName, AName) then
      Exit(True);
  Result := False;
end;

function NextRunName(const AName, APackage, ASessionsFolder: string): string;
var
  LTaken: TArray<string>;
  LBase: string;
  LNumber, LDigits: Integer;
begin
  LBase := Trim(AName);
  if LBase = '' then
    LBase := APackage;
  if LBase = '' then
    LBase := 'session';
  // "Prova1" becomes "Prova2" rather than "Prova1 (2)": a run is the next one, and the
  // number people put at the end of a name is what they meant it to be counted by.
  LDigits := 0;
  while (LDigits < Length(LBase)) and LBase[Length(LBase) - LDigits].IsDigit do
    Inc(LDigits);
  if LDigits > 0 then
  begin
    LNumber := StrToIntDef(Copy(LBase, Length(LBase) - LDigits + 1, LDigits), 1);
    LBase := Copy(LBase, 1, Length(LBase) - LDigits);
  end
  else
  begin
    LNumber := 1;
    LBase := LBase + ' ';
  end;
  LTaken := NamesIn(ASessionsFolder);
  repeat
    Inc(LNumber);
    Result := LBase + IntToStr(LNumber);
  until not IsTaken(Result, LTaken);
end;

end.
