program ControlTests;

{
  Drives real profiling sessions through the same client the GUI uses, without a window:
  start nap.exe serve, list devices, then

    - a sampling session: counters move, stop, and the database opens in the GUI's own
      data layer;
    - an instrumenting session on the weaver engine: snapshot, pause, resume and clear,
      the four live controls, which only that engine can serve.

  It also covers what the Setup dialog reads before any of that and what needs no device:
  the machine's prerequisites, and the projects a folder of sources holds.

  The split is the product's, not the test's: a runtime-provider trace only becomes
  readable when its session ends, which is why the toolbar greys those buttons out.

  Everything here is code the GUI runs when a user clicks the toolbar; the .NET suite
  covers the service, not this side of the wire.

    ControlTests <path to nap.exe> [device serial] [package]
}

{$APPTYPE CONSOLE}

uses
  System.SysUtils, System.IOUtils, System.Classes, System.DateUtils, System.StrUtils,
  FireDAC.Comp.Client,
  uControlClient in '..\src\uControlClient.pas',
  uSessionStore in '..\src\uSessionStore.pas';

const
  CDefaultPackage = 'com.mcasoftware.testtarget';
  CDurationSeconds = 12;
  /// Namespace of the test target's workloads: instrumenting needs a callspec, and one
  /// that covers hot leaves would drown the app in its own overhead.
  CCallspec = 'N:TestTarget.Workloads';

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

/// Waits for the session to reach a state that is not a starting one, or gives up.
function WaitForCollecting(AClient: TControlClient; const AId: string): TSessionStatus;
var
  LDeadline: TDateTime;
begin
  LDeadline := IncSecond(Now, 90);
  repeat
    Result := AClient.Status(AId);
    if (Result.State = 'Collecting') or (Result.State = 'Ready') or (Result.State = 'Failed') then
      Exit;
    Sleep(500);
  until Now > LDeadline;
end;

/// Stop only asks: collection has to wind down and the trace has to be analysed before
/// there is a database to open, which is why the window keeps polling after the click.
function StopAndWait(AClient: TControlClient; const AId: string): TSessionStatus;
var
  LDeadline: TDateTime;
begin
  Result := AClient.Stop(AId);
  LDeadline := IncSecond(Now, 240);
  // Ready is the terminal state: collection stops, the trace is analysed, and only then
  // is there a database to open. Failed ends the wait too.
  while (Result.State <> 'Ready') and (Result.State <> 'Failed') and (Now < LDeadline) do
  begin
    Sleep(1000);
    Result := AClient.Status(AId);
  end;
end;

/// The four buttons AQTime puts on its toolbar, against the engine that can serve them.
procedure RunLiveControls(AClient: TControlClient; const ASerial, APackage: string);
var
  LStatus: TSessionStatus;
  LSegment: Integer;
  LProblems: TArray<string>;
  LArchive: string;
  LArchives: TArchiveEntries;
  LStore: TSessionStore;
  I: Integer;
begin
  Writeln;
  Writeln('live controls (weaver engine):');
  LProblems := AClient.CheckApp(ASerial, APackage, 'instrumenting');
  for I := 0 to High(LProblems) do
    Writeln('        ', LProblems[I]);
  if Length(LProblems) > 0 then
  begin
    Check(False, 'the app is ready to instrument');
    Exit;
  end;

  // Naming the assembly matters: inference reads the callspec's first two segments, and
  // TestTarget.Workloads is a namespace inside TestTarget.dll, not an assembly.
  LStatus := AClient.StartSession(ASerial, APackage, 'instrumenting', 'weaver',
    CCallspec, 0, '', ['TestTarget']);
  Check(LStatus.Id <> '', 'instrumenting session started: ' + LStatus.Id);
  if LStatus.Id = '' then
    Exit;
  try
    LStatus := WaitForCollecting(AClient, LStatus.Id);
    Check(LStatus.State = 'Collecting', 'the session is collecting (state ' + LStatus.State + ')');
    if LStatus.State <> 'Collecting' then
      Exit;

    // Give the app a moment to run something worth collecting.
    Sleep(5000);
    LSegment := AClient.Snapshot(LStatus.Id);
    Check(LSegment >= 0, Format('snapshot taken (segment %d)', [LSegment]));
    // Keeping a Get Results: the archive must be a database that opens on its own, which
    // is the whole promise of the Explorer's tree of saved results.
    LArchive := AClient.Archive(LStatus.Id, 'the smoke test screen');
    Check(TFile.Exists(LArchive), 'the archive was written: ' + LArchive);
    if TFile.Exists(LArchive) then
    begin
      LStore := TSessionStore.Create;
      try
        try
          LStore.Open(LArchive);
          Check(LStore.Mode = smInstrumenting, 'the archive opens as an instrumenting session');
          Check(LStore.Package <> '', 'the archive knows its package: ' + LStore.Package);
        except
          on E: Exception do
            Check(False, 'the archive opens: ' + E.Message);
        end;
      finally
        LStore.Free;
      end;
      LArchives := ListArchives(TPath.GetDirectoryName(TPath.GetDirectoryName(LArchive)));
      Check(Length(LArchives) >= 1, Format('%d archive(s) listed from disk', [Length(LArchives)]));
      if Length(LArchives) > 0 then
        Check(LArchives[0].Name = 'the smoke test screen', 'the name survives: ' + LArchives[0].Name);
    end;

    Check(AClient.Pause(LStatus.Id).State <> '', 'pause answered');
    Check(AClient.Resume(LStatus.Id).State <> '', 'resume answered');
    Check(AClient.Clear(LStatus.Id).State <> '', 'clear answered');
  finally
    LStatus := StopAndWait(AClient, LStatus.Id);
    Check(LStatus.State = 'Ready',
      'the instrumenting session finished (state ' + LStatus.State + ')');
    if LStatus.Error <> '' then
      Writeln('        error: ', LStatus.Error);
  end;
end;

/// What the Setup dialog fills itself in from, none of which needs a device: the tools
/// this machine offers, and the application projects in a folder of sources.
procedure RunSetupInputs(AClient: TControlClient; const ARepoRoot: string);
var
  LTools: TArray<TToolStatus>;
  LProjects: TArray<TAppProject>;
  LCandidates: TArray<TCallspecCandidate>;
  LDsRouter: TToolStatus;
  LTestTarget: TAppProject;
  I: Integer;
begin
  Writeln;
  Writeln('setup inputs:');
  LTools := AClient.Prerequisites;
  Check(Length(LTools) > 0, Format('%d prerequisites reported', [Length(LTools)]));
  LDsRouter := Default(TToolStatus);
  for I := 0 to High(LTools) do
  begin
    Writeln(Format('        %-16s %s', [LTools[I].Name,
      IfThen(LTools[I].Found, LTools[I].Path, 'NOT FOUND - ' + LTools[I].Fix)]));
    if LTools[I].Name = 'dotnet-dsrouter' then
      LDsRouter := LTools[I];
  end;
  // The command is what lets the GUI offer to install it instead of printing a note.
  Check(LDsRouter.InstallCommand <> '', 'dsrouter carries the command that installs it');

  if ARepoRoot = '' then
  begin
    Writeln('        (no repository root: skipping the project scan)');
    Exit;
  end;
  LProjects := AClient.Projects(TPath.Combine(ARepoRoot, 'TestTarget'), 'Debug');
  Check(Length(LProjects) = 1, Format('%d application project(s) under TestTarget', [Length(LProjects)]));
  if Length(LProjects) = 0 then
    Exit;
  LTestTarget := LProjects[0];
  Writeln(Format('        %s  %s  %s', [LTestTarget.Name, LTestTarget.ApplicationId, LTestTarget.OutputDir]));
  Check(LTestTarget.ApplicationId = CDefaultPackage, 'the package comes from the project file');
  Check(Length(LTestTarget.Assemblies) >= 2, Format('%d assemblies to weave, references included',
    [Length(LTestTarget.Assemblies)]));

  if not LTestTarget.OutputExists then
  begin
    Writeln('        (TestTarget is not built: skipping the callspec candidates)');
    Exit;
  end;
  LCandidates := AClient.Candidates(LTestTarget.OutputDir, LTestTarget.Assemblies);
  Check(Length(LCandidates) > 0, Format('%d callspec candidates offered', [Length(LCandidates)]));
  for I := 0 to High(LCandidates) do
    if LCandidates[I].Callspec = CCallspec then
    begin
      Check(True, 'the callspec the sessions use is one of them: ' + CCallspec);
      Exit;
    end;
  Check(False, 'the callspec the sessions use is one of them: ' + CCallspec);
end;

procedure Run(const ANapPath, ASerial, APackage, ARepoRoot: string);
var
  LClient: TControlClient;
  LDevices: TDeviceInfos;
  LSerial: string;
  LProblems: TArray<string>;
  LStatus: TSessionStatus;
  LCounters: TSessionCounters;
  LStore: TSessionStore;
  LSegment: Integer;
  I: Integer;
begin
  LClient := TControlClient.Create;
  try
    LClient.StartService(ANapPath);
    Check(LClient.IsConnected, 'the service answered with a port: ' + LClient.BaseUrl);

    RunSetupInputs(LClient, ARepoRoot);

    LDevices := LClient.Devices;
    Check(Length(LDevices) > 0, Format('%d device(s) visible', [Length(LDevices)]));
    if Length(LDevices) = 0 then
      Exit;
    LSerial := ASerial;
    if LSerial = '' then
      LSerial := LDevices[0].Serial;
    for I := 0 to High(LDevices) do
      Writeln(Format('        %s  %s  api %d  %s', [LDevices[I].Serial, LDevices[I].Model,
        LDevices[I].ApiLevel, LDevices[I].Abi]));

    LProblems := LClient.CheckApp(LSerial, APackage, 'sampling');
    for I := 0 to High(LProblems) do
      Writeln('        ', LProblems[I]);
    Check(Length(LProblems) = 0, 'the app is ready to profile on ' + LSerial);
    if Length(LProblems) > 0 then
      Exit;

    LStatus := LClient.StartSession(LSerial, APackage, 'sampling', '', '', CDurationSeconds);
    Check(LStatus.Id <> '', 'sampling session started: ' + LStatus.Id);
    if LStatus.Id = '' then
      Exit;

    LStatus := WaitForCollecting(LClient, LStatus.Id);
    Check(LStatus.State = 'Collecting', 'the session is collecting (state ' + LStatus.State + ')');

    LCounters := LClient.Counters(LStatus.Id);
    Check(LCounters.ElapsedSeconds > 0, Format('counters move: %.1f s elapsed, %d trace bytes',
      [LCounters.ElapsedSeconds, LCounters.TraceBytes]));

    // A provider trace cannot be read while it is being written, and the service says so
    // instead of handing back half a file. The toolbar greys the button out for exactly
    // this reason, so the refusal is part of the contract, not a failure.
    try
      LClient.Snapshot(LStatus.Id);
      Check(False, 'snapshot is refused on the provider engine');
    except
      on E: EControlClient do
        Check(Pos('weaver', E.Message) > 0, 'snapshot is refused on the provider engine');
    end;

    LStatus := StopAndWait(LClient, LStatus.Id);
    Check(LStatus.State = 'Ready', 'the session finished (state ' + LStatus.State + ')');
    if LStatus.Error <> '' then
      Writeln('        error: ', LStatus.Error);
    for I := 0 to High(LStatus.Warnings) do
      Writeln('        warning: ', LStatus.Warnings[I]);

    Check(TFile.Exists(LStatus.DatabasePath), 'a database was written: ' + LStatus.DatabasePath);
    if not TFile.Exists(LStatus.DatabasePath) then
      Exit;

    // The GUI opens exactly this file: if the two sides disagree, it shows here.
    LStore := TSessionStore.Create;
    try
      LStore.Open(LStatus.DatabasePath);
      Check(LStore.Mode = smSampling, 'the store reads it as a sampling session');
      Check(LStore.Package = APackage, 'the store reads the package back: ' + LStore.Package);
      Check(LStore.CountOf('segment') >= 1,
        Format('the session recorded its segments (%d rows)', [LStore.CountOf('segment')]));
    finally
      LStore.Free;
    end;

    RunLiveControls(LClient, LSerial, APackage);
  finally
    LClient.Free;
  end;
end;

/// The repository this test runs from, found by walking up until TestTarget is in sight.
function FindRepoRoot: string;
var
  LFolder: string;
  I: Integer;
begin
  LFolder := TPath.GetDirectoryName(ParamStr(0));
  for I := 0 to 5 do
  begin
    if TDirectory.Exists(TPath.Combine(LFolder, 'TestTarget')) then
      Exit(LFolder);
    LFolder := TPath.GetDirectoryName(LFolder);
    if LFolder = '' then
      Break;
  end;
  Result := '';
end;

var
  LNap, LSerial, LPackage, LRepo: string;
begin
  try
    if ParamCount = 0 then
    begin
      Writeln('usage: ControlTests <nap.exe> [device serial] [package] [repository root]');
      Halt(2);
    end;
    LNap := ParamStr(1);
    LSerial := ParamStr(2);
    LPackage := ParamStr(3);
    LRepo := ParamStr(4);
    if LPackage = '' then
      LPackage := CDefaultPackage;
    if LRepo = '' then
      LRepo := FindRepoRoot;
    Writeln('nap:     ', LNap);
    Writeln('package: ', LPackage);
    Writeln('repo:    ', LRepo);
    Run(LNap, LSerial, LPackage, LRepo);
    Writeln;
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
