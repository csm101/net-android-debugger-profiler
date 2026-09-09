unit uControlClient;

{
  Client of the local control service (nap serve, docs/CONTROL_SERVICE.md).

  The GUI owns the service process: it starts nap.exe, reads the port from the JSON
  line the service prints on stdout, and shuts it down on exit. Only control travels
  over HTTP - results are read straight from session.db by uSessionStore.
}

interface

uses
  System.SysUtils, System.Classes, System.JSON, System.Net.HttpClient, System.Net.URLClient,
  Winapi.Windows;

type
  EControlClient = class(Exception);

  TDeviceInfo = record
    Serial: string;
    Model: string;
    /// Name of the AVD, for emulators: what the tooling shows instead of a port number.
    AvdName: string;
    ApiLevel: Integer;
    Abi: string;
    IsEmulator: Boolean;
    /// How a person recognises it: the AVD or the model, with the serial to disambiguate.
    function Display: string;
  end;

  TDeviceInfos = TArray<TDeviceInfo>;

  /// An external tool the profiler needs, and what to do when it is missing.
  TToolStatus = record
    Name: string;
    Path: string;
    Purpose: string;
    /// The command that installs it, empty when there is none the GUI can run.
    InstallCommand: string;
    Fix: string;
    Required: Boolean;
    Found: Boolean;
  end;

  /// An msbuild property as the project file declares it: it may simply not say.
  TProjectFlag = (pfUnknown, pfFalse, pfTrue);

  /// An Android application project found in the sources.
  TAppProject = record
    ProjectPath: string;
    Name: string;
    ApplicationId: string;
    TargetFramework: string;
    AssemblyName: string;
    Configuration: string;
    /// bin\<Configuration>\<tfm>: the pdbs, and the assemblies the weaver reads.
    OutputDir: string;
    Assemblies: TArray<string>;
    EnableDiagnostics: TProjectFlag;
    EmbedAssembliesIntoApk: TProjectFlag;
    OutputExists: Boolean;
    InSolution: Boolean;
  end;

  /// Everything a new session is: what to profile, how, what to call it and where to
  /// keep it. A record rather than fifteen arguments - the setup dialog fills one in and
  /// hands it over, and adding a field does not renumber a call.
  TSessionRequest = record
    DeviceSerial: string;
    Package: string;
    Mode: string;
    Engine: string;
    Callspec: string;
    DurationSeconds: Integer;
    SymbolsDir: string;
    /// Assemblies to weave; empty lets the service infer them from the callspec.
    Assemblies: TArray<string>;
    /// Set when the app was woven during its build: the session reads that map and
    /// changes nothing on the device.
    WeaveMapPath: string;
    /// What to call this session. Empty leaves it named after the package.
    Name: string;
    /// Where to keep it; empty means the service's own sessions folder.
    SessionsRoot: string;
    /// Where the app came from, so sessions can be listed by product rather than by time.
    SolutionPath: string;
    ProjectPath: string;
    /// Set everything up but measure nothing until Resume: what needs profiling is rarely
    /// the startup, and everything recorded on the way to the screen that matters is noise.
    StartPaused: Boolean;
  end;

  /// A callspec the app's own assemblies offer, with how many methods it covers.
  TCallspecCandidate = record
    Callspec: string;
    Kind: string;
    /// The assemblies that hold it: what a weaving session has to rewrite, which is
    /// rarely every assembly of the app.
    Assembly: string;
    Methods: Integer;
  end;

  /// A background job of the service - a build, a tool install - and the tail of its log.
  TJobStatus = record
    Id: string;
    Kind: string;
    State: string;
    Error: string;
    ExitCode: Integer;
    HasExitCode: Boolean;
    /// Index of the first line in Log, and how many lines the job has produced overall.
    LogFrom: Integer;
    LogTotal: Integer;
    Log: TArray<string>;
    function Running: Boolean;
    function Succeeded: Boolean;
  end;

  /// What a running session reports once a second, for the Monitor panel.
  TSessionCounters = record
    State: string;
    ElapsedSeconds: Double;
    TraceBytes: Int64;
    EventBytes: Int64;
    Snapshots: Integer;
  end;

  /// What a session looks like from outside: state, warnings and the tail of its log.
  TSessionStatus = record
    Id: string;
    State: string;
    DatabasePath: string;
    Error: string;
    Warnings: TArray<string>;
    Log: TArray<string>;
  end;

  TControlClient = class
  private
    FHttp: THTTPClient;
    FBaseUrl: string;
    FProcess: TProcessInformation;
    FOwnsService: Boolean;
    function Get(const APath: string): TJSONValue;
    function Post(const APath, ABody: string): TJSONValue;
    function ReadStatus(AValue: TJSONValue): TSessionStatus;
    function ReadJob(AValue: TJSONValue): TJobStatus;
  public
    constructor Create;
    destructor Destroy; override;
    /// Start nap.exe serve and wait for it to report its port.
    procedure StartService(const AExePath: string; const ASessionsRoot: string = '');
    /// Use a service somebody else started.
    procedure UseService(APort: Integer);
    procedure StopService;
    function IsConnected: Boolean;

    function Devices: TDeviceInfos;
    /// Blocking problems for a package in a given mode; empty means "ready".
    function CheckApp(const ASerial, APackage, AMode: string): TArray<string>;

    /// The external tools this machine offers (adb, dsrouter, dotnet).
    function Prerequisites: TArray<TToolStatus>;
    /// Starts installing one of them; poll the answer with Job.
    function InstallTool(const AName: string): TJobStatus;
    /// The Android application projects a solution, project or folder holds.
    function Projects(const APath: string; const AConfiguration: string = 'Debug'): TArray<TAppProject>;
    /// Namespaces and types of the app's own assemblies, to offer instead of a typed callspec.
    function Candidates(const AOutputDir: string; const AAssemblies: TArray<string>): TArray<TCallspecCandidate>;
    /// Starts a build (and install) of an app project; poll the answer with Job.
    /// AClearDeployed removes the app's fast-deployment directory first, which an app
    /// that is changing deployment mode needs; APackage is what that needs to find it.
    function StartBuild(const AProjectPath, AConfiguration, ADeviceSerial: string;
      AEnableDiagnostics, AFastDeployment, AInstall: Boolean;
      AClearDeployed: Boolean = False; const APackage: string = '';
      AWeave: Boolean = False; const ACallspec: string = '';
      const AWeaveAssemblies: TArray<string> = nil): TJobStatus;
    /// State of a job, with the log lines from AFrom on.
    function Job(const AId: string; AFrom: Integer = 0): TJobStatus;
    function CancelJob(const AId: string): TJobStatus;
    function StartSession(const ARequest: TSessionRequest): TSessionStatus;
    /// Names a stored session, or takes its name away with an empty one. Works on a
    /// session this service never ran: it is a question about a directory.
    procedure RenameSession(const AId, AName: string);
    /// Deletes a stored session and everything it recorded. Not recoverable.
    procedure DeleteSession(const AId: string);
    function Status(const AId: string): TSessionStatus;
    function Counters(const AId: string): TSessionCounters;
    function Snapshot(const AId: string): Integer;
    /// Keeps the current results under a name and goes on profiling; answers where they
    /// were kept. The session refreshes them first, so an archive holds what the app has
    /// done up to this moment.
    function Archive(const AId, AName: string): string;
    function Pause(const AId: string): TSessionStatus;
    function Resume(const AId: string): TSessionStatus;
    function Clear(const AId: string): TSessionStatus;
    function Stop(const AId: string): TSessionStatus;

    property BaseUrl: string read FBaseUrl;
  end;

/// The few fields a session needs when nobody is filling a dialog: what a check, a
/// script or a one-off run says. Everything else keeps its default.
function SessionRequest(const ASerial, APackage, AMode, AEngine, ACallspec: string;
  ADurationSeconds: Integer = 0; const AAssemblies: TArray<string> = nil): TSessionRequest;

implementation

uses
  System.IOUtils, System.NetEncoding, System.Net.HttpClientComponent;

function SessionRequest(const ASerial, APackage, AMode, AEngine, ACallspec: string;
  ADurationSeconds: Integer; const AAssemblies: TArray<string>): TSessionRequest;
begin
  Result := Default(TSessionRequest);
  Result.DeviceSerial := ASerial;
  Result.Package := APackage;
  Result.Mode := AMode;
  Result.Engine := AEngine;
  Result.Callspec := ACallspec;
  Result.DurationSeconds := ADurationSeconds;
  Result.Assemblies := AAssemblies;
end;

function TDeviceInfo.Display: string;
var
  LName: string;
begin
  LName := AvdName;
  if LName = '' then
    LName := Model;
  if LName = '' then
    LName := Serial;
  Result := Format('%s  (%s)', [LName, Serial]);
  if ApiLevel > 0 then
    Result := Result + Format('  api %d', [ApiLevel]);
  if Abi <> '' then
    Result := Result + '  ' + Abi;
end;

function TJobStatus.Running: Boolean;
begin
  Result := SameText(State, 'Running');
end;

function TJobStatus.Succeeded: Boolean;
begin
  Result := SameText(State, 'Succeeded');
end;

constructor TControlClient.Create;
begin
  inherited Create;
  FHttp := THTTPClient.Create;
  FHttp.ConnectionTimeout := 5000;
  FHttp.ResponseTimeout := 120000;   // a start can wait for the app to come up
end;

destructor TControlClient.Destroy;
begin
  StopService;
  FHttp.Free;
  inherited Destroy;
end;

function TControlClient.IsConnected: Boolean;
begin
  Result := FBaseUrl <> '';
end;

procedure TControlClient.UseService(APort: Integer);
begin
  FBaseUrl := Format('http://127.0.0.1:%d/', [APort]);
  FOwnsService := False;
end;

procedure TControlClient.StartService(const AExePath, ASessionsRoot: string);
var
  LRead, LWrite: THandle;
  LSecurity: TSecurityAttributes;
  LStartup: TStartupInfo;
  LCommand: string;
  LBuffer: array[0..1023] of AnsiChar;
  LRawText: AnsiString;
  LDone: Cardinal;
  LJson: TJSONValue;
  LDeadline: TDateTime;
begin
  if not TFile.Exists(AExePath) then
    raise EControlClient.CreateFmt('nap.exe not found at %s', [AExePath]);

  // The service prints {"port":...} on stdout as soon as it listens, so the pipe is
  // both the handshake and the place its log goes.
  LSecurity := Default(TSecurityAttributes);
  LSecurity.nLength := SizeOf(LSecurity);
  LSecurity.bInheritHandle := True;
  if not CreatePipe(LRead, LWrite, @LSecurity, 0) then
    raise EControlClient.Create('Cannot create a pipe for the control service.');

  LStartup := Default(TStartupInfo);
  LStartup.cb := SizeOf(LStartup);
  LStartup.dwFlags := STARTF_USESTDHANDLES or STARTF_USESHOWWINDOW;
  LStartup.wShowWindow := SW_HIDE;
  LStartup.hStdOutput := LWrite;
  LStartup.hStdError := LWrite;

  // --parent-pid: a GUI that is killed rather than closed never runs StopService, and
  // the service would go on listening. Told whose child it is, it ends by itself.
  LCommand := Format('"%s" serve --parent-pid %d', [AExePath, GetCurrentProcessId]);
  if ASessionsRoot <> '' then
    LCommand := LCommand + Format(' --sessions-root "%s"', [ASessionsRoot]);

  if not CreateProcess(nil, PChar(LCommand), nil, nil, True, CREATE_NO_WINDOW, nil, nil, LStartup, FProcess) then
  begin
    CloseHandle(LRead);
    CloseHandle(LWrite);
    raise EControlClient.CreateFmt('Cannot start the control service: %s', [SysErrorMessage(GetLastError)]);
  end;
  CloseHandle(LWrite);          // the child owns the writing end now
  FOwnsService := True;

  LRawText := '';
  LDeadline := Now + 15 / SecsPerDay;
  try
    while Now < LDeadline do
    begin
      if not ReadFile(LRead, LBuffer, SizeOf(LBuffer) - 1, LDone, nil) or (LDone = 0) then
        Break;
      SetString(LRawText, LBuffer, LDone);
      LJson := TJSONObject.ParseJSONValue(string(LRawText));
      try
        if (LJson is TJSONObject) and (TJSONObject(LJson).GetValue('port') <> nil) then
        begin
          UseService(TJSONObject(LJson).GetValue<Integer>('port'));
          FOwnsService := True;
          Exit;
        end;
      finally
        LJson.Free;
      end;
    end;
    raise EControlClient.Create('The control service did not report a port.');
  finally
    CloseHandle(LRead);
  end;
end;

procedure TControlClient.StopService;
begin
  if FOwnsService and (FProcess.hProcess <> 0) then
  begin
    try
      Post('shutdown', '');
    except
      // if it is already gone, killing it is the fallback below
    end;
    if WaitForSingleObject(FProcess.hProcess, 3000) <> WAIT_OBJECT_0 then
      TerminateProcess(FProcess.hProcess, 0);
    CloseHandle(FProcess.hProcess);
    CloseHandle(FProcess.hThread);
    FProcess := Default(TProcessInformation);
  end;
  FBaseUrl := '';
  FOwnsService := False;
end;

function TControlClient.Get(const APath: string): TJSONValue;
var
  LResponse: IHTTPResponse;
  LText: string;
begin
  if not IsConnected then
    raise EControlClient.Create('Not connected to a control service.');
  LResponse := FHttp.Get(FBaseUrl + APath);
  LText := LResponse.ContentAsString;
  Result := TJSONObject.ParseJSONValue(LText);
  if LResponse.StatusCode >= 400 then
  begin
    try
      if (Result is TJSONObject) and (TJSONObject(Result).GetValue('error') <> nil) then
        raise EControlClient.Create(TJSONObject(Result).GetValue<string>('error'));
      raise EControlClient.CreateFmt('%d from %s', [LResponse.StatusCode, APath]);
    finally
      Result.Free;
    end;
  end;
end;

function TControlClient.Post(const APath, ABody: string): TJSONValue;
var
  LResponse: IHTTPResponse;
  LStream: TStringStream;
begin
  if not IsConnected then
    raise EControlClient.Create('Not connected to a control service.');
  // Always send a body, even an empty one: Windows' HTTP stack answers 411 to a POST
  // without Content-Length before the service ever sees it.
  LStream := TStringStream.Create(ABody, TEncoding.UTF8);
  try
    LResponse := FHttp.Post(FBaseUrl + APath, LStream, nil,
      [TNameValuePair.Create('Content-Type', 'application/json')]);
  finally
    LStream.Free;
  end;
  Result := TJSONObject.ParseJSONValue(LResponse.ContentAsString);
  if LResponse.StatusCode >= 400 then
  begin
    try
      if (Result is TJSONObject) and (TJSONObject(Result).GetValue('error') <> nil) then
        raise EControlClient.Create(TJSONObject(Result).GetValue<string>('error'));
      raise EControlClient.CreateFmt('%d from %s', [LResponse.StatusCode, APath]);
    finally
      Result.Free;
    end;
  end;
end;

function TControlClient.Devices: TDeviceInfos;
var
  LValue: TJSONValue;
  LArray: TJSONArray;
  LItem: TJSONObject;
  I: Integer;
begin
  LValue := Get('devices');
  try
    if not (LValue is TJSONArray) then
      Exit(nil);
    LArray := TJSONArray(LValue);
    SetLength(Result, LArray.Count);
    for I := 0 to LArray.Count - 1 do
    begin
      LItem := LArray.Items[I] as TJSONObject;
      Result[I].Serial := LItem.GetValue<string>('serial', '');
      Result[I].Model := LItem.GetValue<string>('model', '');
      Result[I].AvdName := LItem.GetValue<string>('avdName', '');
      Result[I].ApiLevel := LItem.GetValue<Integer>('apiLevel', 0);
      Result[I].Abi := LItem.GetValue<string>('abi', '');
      Result[I].IsEmulator := LItem.GetValue<Boolean>('isEmulator', False);
    end;
  finally
    LValue.Free;
  end;
end;

function TControlClient.CheckApp(const ASerial, APackage, AMode: string): TArray<string>;
var
  LValue: TJSONValue;
  LProblems: TJSONArray;
  I: Integer;
begin
  LValue := Get(Format('apps/%s/check?device=%s&mode=%s', [APackage, ASerial, AMode]));
  try
    Result := nil;
    if not (LValue is TJSONObject) then
      Exit;
    if not (TJSONObject(LValue).GetValue('problems') is TJSONArray) then
      Exit;
    LProblems := TJSONObject(LValue).GetValue('problems') as TJSONArray;
    SetLength(Result, LProblems.Count);
    for I := 0 to LProblems.Count - 1 do
      Result[I] := (LProblems.Items[I] as TJSONObject).GetValue<string>('message', '');
  finally
    LValue.Free;
  end;
end;

// The service leaves null properties out entirely, so every reader here has to cope with
// a missing pair rather than with a null one.

/// The name with the other capitalisation of its first letter. The control service sends
/// camelCase and the sidecar the engine writes beside an archive is PascalCase: reading
/// both means neither side can break this one by choosing a policy.
function OtherCase(const AName: string): string;
begin
  Result := AName;
  if Result = '' then
    Exit;
  if Result[1] = UpCase(Result[1]) then
    Result[1] := LowerCase(Result[1])[1]
  else
    Result[1] := UpCase(Result[1]);
end;

function JsonValueOf(AObject: TJSONObject; const AName: string): TJSONValue;
begin
  Result := AObject.GetValue(AName);
  if Result = nil then
    Result := AObject.GetValue(OtherCase(AName));
  if Result is TJSONNull then
    Result := nil;
end;

function JsonStr(AObject: TJSONObject; const AName: string): string;
var
  LValue: TJSONValue;
begin
  LValue := JsonValueOf(AObject, AName);
  if LValue = nil then
    Result := ''
  else
    Result := LValue.Value;
end;

function JsonBool(AObject: TJSONObject; const AName: string): Boolean;
begin
  Result := JsonValueOf(AObject, AName) is TJSONTrue;
end;

function JsonInt(AObject: TJSONObject; const AName: string): Integer;
var
  LValue: TJSONValue;
begin
  LValue := JsonValueOf(AObject, AName);
  if LValue = nil then
    Result := 0
  else
    Result := StrToIntDef(LValue.Value, 0);
end;

function JsonFlag(AObject: TJSONObject; const AName: string): TProjectFlag;
var
  LValue: TJSONValue;
begin
  LValue := JsonValueOf(AObject, AName);
  if LValue = nil then
    Result := pfUnknown
  else if LValue is TJSONTrue then
    Result := pfTrue
  else
    Result := pfFalse;
end;

function JsonStrings(AObject: TJSONObject; const AName: string): TArray<string>;
var
  LArray: TJSONArray;
  I: Integer;
begin
  Result := nil;
  if not (JsonValueOf(AObject, AName) is TJSONArray) then
    Exit;
  LArray := JsonValueOf(AObject, AName) as TJSONArray;
  SetLength(Result, LArray.Count);
  for I := 0 to LArray.Count - 1 do
    Result[I] := LArray.Items[I].Value;
end;

function TControlClient.Prerequisites: TArray<TToolStatus>;
var
  LValue: TJSONValue;
  LArray: TJSONArray;
  LItem: TJSONObject;
  I: Integer;
begin
  Result := nil;
  LValue := Get('prereqs');
  try
    if not (LValue is TJSONObject) then
      Exit;
    if not (TJSONObject(LValue).GetValue('tools') is TJSONArray) then
      Exit;
    LArray := TJSONObject(LValue).GetValue('tools') as TJSONArray;
    SetLength(Result, LArray.Count);
    for I := 0 to LArray.Count - 1 do
    begin
      LItem := LArray.Items[I] as TJSONObject;
      Result[I].Name := JsonStr(LItem, 'name');
      Result[I].Path := JsonStr(LItem, 'path');
      Result[I].Purpose := JsonStr(LItem, 'purpose');
      Result[I].InstallCommand := JsonStr(LItem, 'installCommand');
      Result[I].Fix := JsonStr(LItem, 'fix');
      Result[I].Required := JsonBool(LItem, 'required');
      Result[I].Found := JsonBool(LItem, 'found');
    end;
  finally
    LValue.Free;
  end;
end;

function TControlClient.ReadJob(AValue: TJSONValue): TJobStatus;
var
  LObject: TJSONObject;
begin
  Result := Default(TJobStatus);
  if not (AValue is TJSONObject) then
    Exit;
  LObject := TJSONObject(AValue);
  Result.Id := JsonStr(LObject, 'id');
  Result.Kind := JsonStr(LObject, 'kind');
  Result.State := JsonStr(LObject, 'state');
  Result.Error := JsonStr(LObject, 'error');
  Result.HasExitCode := LObject.GetValue('exitCode') <> nil;
  Result.ExitCode := JsonInt(LObject, 'exitCode');
  Result.LogFrom := JsonInt(LObject, 'logFrom');
  Result.LogTotal := JsonInt(LObject, 'logTotal');
  Result.Log := JsonStrings(LObject, 'log');
end;

function TControlClient.InstallTool(const AName: string): TJobStatus;
var
  LBody: TJSONObject;
  LValue: TJSONValue;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('tool', AName);
    LValue := Post('prereqs/install', LBody.ToJSON);
  finally
    LBody.Free;
  end;
  try
    Result := ReadJob(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Projects(const APath, AConfiguration: string): TArray<TAppProject>;
var
  LValue: TJSONValue;
  LArray: TJSONArray;
  LItem: TJSONObject;
  I: Integer;
begin
  Result := nil;
  LValue := Get(Format('projects?path=%s&configuration=%s',
    [TNetEncoding.URL.Encode(APath), TNetEncoding.URL.Encode(AConfiguration)]));
  try
    if not (LValue is TJSONArray) then
      Exit;
    LArray := TJSONArray(LValue);
    SetLength(Result, LArray.Count);
    for I := 0 to LArray.Count - 1 do
    begin
      LItem := LArray.Items[I] as TJSONObject;
      Result[I].ProjectPath := JsonStr(LItem, 'projectPath');
      Result[I].Name := JsonStr(LItem, 'name');
      Result[I].ApplicationId := JsonStr(LItem, 'applicationId');
      Result[I].TargetFramework := JsonStr(LItem, 'targetFramework');
      Result[I].AssemblyName := JsonStr(LItem, 'assemblyName');
      Result[I].Configuration := JsonStr(LItem, 'configuration');
      Result[I].OutputDir := JsonStr(LItem, 'outputDir');
      Result[I].Assemblies := JsonStrings(LItem, 'assemblies');
      Result[I].EnableDiagnostics := JsonFlag(LItem, 'enableDiagnostics');
      Result[I].EmbedAssembliesIntoApk := JsonFlag(LItem, 'embedAssembliesIntoApk');
      Result[I].OutputExists := JsonBool(LItem, 'outputExists');
      Result[I].InSolution := JsonBool(LItem, 'inSolution');
    end;
  finally
    LValue.Free;
  end;
end;

function TControlClient.Candidates(const AOutputDir: string; const AAssemblies: TArray<string>): TArray<TCallspecCandidate>;
var
  LValue: TJSONValue;
  LArray: TJSONArray;
  LItem: TJSONObject;
  I: Integer;
begin
  Result := nil;
  if (AOutputDir = '') or (Length(AAssemblies) = 0) then
    Exit;
  LValue := Get(Format('projects/candidates?outputDir=%s&assemblies=%s',
    [TNetEncoding.URL.Encode(AOutputDir), TNetEncoding.URL.Encode(string.Join(',', AAssemblies))]));
  try
    if not (LValue is TJSONArray) then
      Exit;
    LArray := TJSONArray(LValue);
    SetLength(Result, LArray.Count);
    for I := 0 to LArray.Count - 1 do
    begin
      LItem := LArray.Items[I] as TJSONObject;
      Result[I].Callspec := JsonStr(LItem, 'callspec');
      Result[I].Kind := JsonStr(LItem, 'kind');
      Result[I].Assembly := JsonStr(LItem, 'assembly');
      Result[I].Methods := JsonInt(LItem, 'methods');
    end;
  finally
    LValue.Free;
  end;
end;

function TControlClient.StartBuild(const AProjectPath, AConfiguration, ADeviceSerial: string;
  AEnableDiagnostics, AFastDeployment, AInstall: Boolean;
  AClearDeployed: Boolean; const APackage: string;
  AWeave: Boolean; const ACallspec: string;
  const AWeaveAssemblies: TArray<string>): TJobStatus;
var
  LBody: TJSONObject;
  LValue: TJSONValue;
  LArray: TJSONArray;
  I: Integer;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('projectPath', AProjectPath);
    if AConfiguration <> '' then
      LBody.AddPair('configuration', AConfiguration);
    if ADeviceSerial <> '' then
      LBody.AddPair('deviceSerial', ADeviceSerial);
    LBody.AddPair('enableDiagnostics', TJSONBool.Create(AEnableDiagnostics));
    LBody.AddPair('fastDeployment', TJSONBool.Create(AFastDeployment));
    LBody.AddPair('install', TJSONBool.Create(AInstall));
    if AClearDeployed then
    begin
      LBody.AddPair('clearDeployedAssemblies', TJSONBool.Create(True));
      LBody.AddPair('packageName', APackage);
    end;
    // Weaving during the build bakes the callspec into the app, so it travels with the
    // build request rather than with the session that follows.
    if AWeave then
    begin
      LBody.AddPair('weave', TJSONBool.Create(True));
      LBody.AddPair('callspec', ACallspec);
      // The libraries the callspec reaches: without them the build instruments only the
      // application project, and the business logic stays invisible.
      if Length(AWeaveAssemblies) > 0 then
      begin
        LArray := TJSONArray.Create;
        for I := 0 to High(AWeaveAssemblies) do
          LArray.Add(AWeaveAssemblies[I]);
        LBody.AddPair('weaveAssemblies', LArray);
      end;
    end;
    LValue := Post('builds', LBody.ToJSON);
  finally
    LBody.Free;
  end;
  try
    Result := ReadJob(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Job(const AId: string; AFrom: Integer): TJobStatus;
var
  LValue: TJSONValue;
begin
  LValue := Get(Format('jobs/%s?from=%d', [AId, AFrom]));
  try
    Result := ReadJob(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.CancelJob(const AId: string): TJobStatus;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('jobs/%s/cancel', [AId]), '');
  try
    Result := ReadJob(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.ReadStatus(AValue: TJSONValue): TSessionStatus;
var
  LObject: TJSONObject;
  LArray: TJSONArray;
  I: Integer;
begin
  Result := Default(TSessionStatus);
  if not (AValue is TJSONObject) then
    Exit;
  LObject := TJSONObject(AValue);
  // A snapshot answers { segment, databasePath, session: {...} }.
  if LObject.GetValue('session') is TJSONObject then
    LObject := LObject.GetValue('session') as TJSONObject;
  Result.Id := LObject.GetValue<string>('id', '');
  Result.State := LObject.GetValue<string>('state', '');
  Result.DatabasePath := LObject.GetValue<string>('databasePath', '');
  Result.Error := LObject.GetValue<string>('error', '');
  if LObject.GetValue('warnings') is TJSONArray then
  begin
    LArray := LObject.GetValue('warnings') as TJSONArray;
    SetLength(Result.Warnings, LArray.Count);
    for I := 0 to LArray.Count - 1 do
      Result.Warnings[I] := LArray.Items[I].Value;
  end;
  if LObject.GetValue('log') is TJSONArray then
  begin
    LArray := LObject.GetValue('log') as TJSONArray;
    SetLength(Result.Log, LArray.Count);
    for I := 0 to LArray.Count - 1 do
      Result.Log[I] := LArray.Items[I].Value;
  end;
end;

function TControlClient.StartSession(const ARequest: TSessionRequest): TSessionStatus;

  procedure AddIfAny(ABody: TJSONObject; const AName, AValue: string);
  begin
    if AValue <> '' then
      ABody.AddPair(AName, AValue);
  end;

var
  LBody: TJSONObject;
  LValue: TJSONValue;
  LArray: TJSONArray;
  I: Integer;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('deviceSerial', ARequest.DeviceSerial);
    LBody.AddPair('packageName', ARequest.Package);
    LBody.AddPair('mode', ARequest.Mode);
    AddIfAny(LBody, 'engine', ARequest.Engine);
    AddIfAny(LBody, 'callspec', ARequest.Callspec);
    if ARequest.DurationSeconds > 0 then
      LBody.AddPair('durationSeconds', TJSONNumber.Create(ARequest.DurationSeconds));
    AddIfAny(LBody, 'symbolsDir', ARequest.SymbolsDir);
    AddIfAny(LBody, 'name', ARequest.Name);
    AddIfAny(LBody, 'sessionsRoot', ARequest.SessionsRoot);
    AddIfAny(LBody, 'solutionPath', ARequest.SolutionPath);
    AddIfAny(LBody, 'projectPath', ARequest.ProjectPath);
    // With a map from a build-time weave the session weaves nothing: the installed app
    // already carries the instrumentation.
    AddIfAny(LBody, 'weaveMapPath', ARequest.WeaveMapPath);
    if ARequest.StartPaused then
      LBody.AddPair('startPaused', TJSONBool.Create(True));
    // Which assemblies to weave. Left out, the service infers them from the callspec,
    // which is right when the namespace and the assembly share a name and wrong when
    // they do not (N:TestTarget.Workloads lives in TestTarget.dll).
    if Length(ARequest.Assemblies) > 0 then
    begin
      LArray := TJSONArray.Create;
      for I := 0 to High(ARequest.Assemblies) do
        LArray.Add(ARequest.Assemblies[I]);
      LBody.AddPair('weaveAssemblies', LArray);
    end;
    LValue := Post('sessions', LBody.ToJSON);
  finally
    LBody.Free;
  end;
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

{ Naming and deleting travel in the body rather than in the path: a session can be a
  directory somewhere else, and a Windows path is not a URL segment. }
procedure TControlClient.RenameSession(const AId, AName: string);
var
  LBody: TJSONObject;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('id', AId);
    LBody.AddPair('name', AName);
    Post('sessions/rename', LBody.ToJSON).Free;
  finally
    LBody.Free;
  end;
end;

procedure TControlClient.DeleteSession(const AId: string);
var
  LBody: TJSONObject;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('id', AId);
    Post('sessions/delete', LBody.ToJSON).Free;
  finally
    LBody.Free;
  end;
end;

function TControlClient.Status(const AId: string): TSessionStatus;
var
  LValue: TJSONValue;
begin
  LValue := Get('sessions/' + AId);
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Counters(const AId: string): TSessionCounters;
var
  LValue: TJSONValue;
  LObject: TJSONObject;
begin
  Result := Default(TSessionCounters);
  LValue := Get('sessions/' + AId + '/counters');
  try
    if not (LValue is TJSONObject) then
      Exit;
    LObject := TJSONObject(LValue);
    Result.State := LObject.GetValue<string>('state', '');
    Result.ElapsedSeconds := LObject.GetValue<Double>('elapsedSeconds', 0);
    Result.TraceBytes := LObject.GetValue<Int64>('traceBytes', 0);
    Result.EventBytes := LObject.GetValue<Int64>('eventBytes', 0);
    Result.Snapshots := LObject.GetValue<Integer>('snapshots', 0);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Snapshot(const AId: string): Integer;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('sessions/%s/snapshot', [AId]), '');
  try
    if (LValue is TJSONObject) and (TJSONObject(LValue).GetValue('segment') <> nil) then
      Result := TJSONObject(LValue).GetValue<Integer>('segment')
    else
      Result := 0;
  finally
    LValue.Free;
  end;
end;

function TControlClient.Archive(const AId, AName: string): string;
var
  LBody: TJSONObject;
  LValue: TJSONValue;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('name', AName);
    LValue := Post(Format('sessions/%s/archive', [AId]), LBody.ToJSON);
  finally
    LBody.Free;
  end;
  try
    Result := '';
    if LValue is TJSONObject then
      Result := JsonStr(TJSONObject(LValue), 'Path');
  finally
    LValue.Free;
  end;
end;

function TControlClient.Pause(const AId: string): TSessionStatus;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('sessions/%s/pause', [AId]), '');
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Resume(const AId: string): TSessionStatus;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('sessions/%s/resume', [AId]), '');
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Clear(const AId: string): TSessionStatus;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('sessions/%s/clear', [AId]), '');
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

function TControlClient.Stop(const AId: string): TSessionStatus;
var
  LValue: TJSONValue;
begin
  LValue := Post(Format('sessions/%s/stop', [AId]), '');
  try
    Result := ReadStatus(LValue);
  finally
    LValue.Free;
  end;
end;

end.
