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
    ApiLevel: Integer;
    Abi: string;
    IsEmulator: Boolean;
  end;

  TDeviceInfos = TArray<TDeviceInfo>;

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
    function StartSession(const ASerial, APackage, AMode, AEngine, ACallspec: string;
      ADurationSeconds: Integer; const ASymbolsDir: string = ''): TSessionStatus;
    function Status(const AId: string): TSessionStatus;
    function Counters(const AId: string): TSessionCounters;
    function Snapshot(const AId: string): Integer;
    function Pause(const AId: string): TSessionStatus;
    function Resume(const AId: string): TSessionStatus;
    function Clear(const AId: string): TSessionStatus;
    function Stop(const AId: string): TSessionStatus;

    property BaseUrl: string read FBaseUrl;
  end;

implementation

uses
  System.IOUtils, System.Net.HttpClientComponent;

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

  LCommand := Format('"%s" serve', [AExePath]);
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

function TControlClient.StartSession(const ASerial, APackage, AMode, AEngine, ACallspec: string;
  ADurationSeconds: Integer; const ASymbolsDir: string): TSessionStatus;
var
  LBody: TJSONObject;
  LValue: TJSONValue;
begin
  LBody := TJSONObject.Create;
  try
    LBody.AddPair('deviceSerial', ASerial);
    LBody.AddPair('packageName', APackage);
    LBody.AddPair('mode', AMode);
    if AEngine <> '' then
      LBody.AddPair('engine', AEngine);
    if ACallspec <> '' then
      LBody.AddPair('callspec', ACallspec);
    if ADurationSeconds > 0 then
      LBody.AddPair('durationSeconds', TJSONNumber.Create(ADurationSeconds));
    if ASymbolsDir <> '' then
      LBody.AddPair('symbolsDir', ASymbolsDir);
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
