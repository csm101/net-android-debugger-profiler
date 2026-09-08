unit uGuiControl;

(*
  The control channel: one JSON object per line on standard input, one JSON object per
  line on standard output. It is how the MCP server drives this window - open a session,
  look at a panel, focus a method, hand back a picture of what is on it - with the window
  never shown unless somebody asks for it.

  Why stdio and not a socket: the profiler already speaks this way in the other direction
  (the GUI starts `nap serve` and reads its port from a pipe), it needs no port, no token
  and no firewall, and the process that owns the channel owns the GUI's lifetime for free.
  The commands themselves know nothing about the transport: ExecuteCommand takes a JSON
  object and answers one, so another transport can be put in front of it later.

  Every command runs on the main thread (VCL is not thread-safe and a render is a paint),
  which is what the reader thread's Synchronize is for. The answer is written from the
  same synchronized block, so answers keep the order of their requests.

  The protocol, in one example each:
    {"id":1,"command":"status"}
    {"id":2,"command":"open","path":"C:\\...\\session.db"}      a directory works too
    {"id":3,"command":"view","panel":"graph","method":"My.App.Search"}
    {"id":4,"command":"capture","panel":"graph","width":1400,"height":900}   trim by default
    {"id":5,"command":"show"}      {"id":6,"command":"hide"}    {"id":7,"command":"close"}
  Answers carry the id they belong to, "ok":true with what was done, or "ok":false with
  an "error" that says what to do instead.
*)

interface

uses
  System.SysUtils, System.Classes, System.JSON;

/// Run one command against the main window. Main thread only.
function ExecuteCommand(const ARequest: TJSONObject): TJSONObject;

/// Start reading commands from standard input. Answers, and the first "ready" line, go
/// to standard output; nothing else in the process may write there.
procedure StartControlChannel;

/// End this window when the process that owns it does. A driven window nobody can see is
/// a window nobody will close: when the server that started it is killed rather than shut
/// down, without this it stays for the rest of the day, holding its files.
/// Same guard the GUI puts on the control service it starts (--parent-pid).
procedure WatchParentProcess(APid: Cardinal);

/// True while the channel is running, so the window knows it is being driven.
function ControlChannelRunning: Boolean;

implementation

uses
  System.NetEncoding, System.IOUtils, Winapi.Windows, Winapi.Messages, Vcl.Forms, Vcl.Controls, Vcl.Graphics,
  uMainForm, uGuiRender, uSessionStore;

type
  /// Reads command lines and has them executed on the main thread.
  TChannelThread = class(TThread)
  private
    FInput: THandleStream;
    FReader: TStreamReader;
    procedure HandleLine(const ALine: string);
  protected
    procedure Execute; override;
  public
    constructor Create;
    destructor Destroy; override;
  end;

var
  GChannel: TChannelThread = nil;
  /// The process this window belongs to, reported in the ready line so that a client can
  /// see its own pid come back and know the guard is on.
  GParentPid: Cardinal = 0;
  GOutput: THandleStream = nil;
  GWriteLock: TObject = nil;

function ControlChannelRunning: Boolean;
begin
  Result := GChannel <> nil;
end;

/// One line of UTF-8 on standard output. Answers are written from the main thread, but
/// the lock keeps the ready line and any late answer from interleaving.
procedure WriteLine(const AText: string);
var
  LBytes: TBytes;
begin
  if GOutput = nil then
    Exit;
  LBytes := TEncoding.UTF8.GetBytes(AText + #10);
  System.TMonitor.Enter(GWriteLock);
  try
    GOutput.WriteBuffer(LBytes, Length(LBytes));
  finally
    System.TMonitor.Exit(GWriteLock);
  end;
end;

function StringOf(const ARequest: TJSONObject; const AName: string; const ADefault: string = ''): string;
var
  LValue: TJSONValue;
begin
  LValue := ARequest.GetValue(AName);
  if (LValue = nil) or (LValue is TJSONNull) then
    Exit(ADefault);
  Result := LValue.Value;
end;

function IntegerOf(const ARequest: TJSONObject; const AName: string; ADefault: Integer): Integer;
var
  LValue: TJSONValue;
begin
  LValue := ARequest.GetValue(AName);
  if (LValue = nil) or (LValue is TJSONNull) then
    Exit(ADefault);
  if not TryStrToInt(LValue.Value, Result) then
    Result := ADefault;
end;

function BooleanOf(const ARequest: TJSONObject; const AName: string; ADefault: Boolean): Boolean;
var
  LValue: TJSONValue;
begin
  LValue := ARequest.GetValue(AName);
  if (LValue = nil) or (LValue is TJSONNull) then
    Exit(ADefault);
  Result := SameText(LValue.Value, 'true') or (LValue.Value = '1');
end;

procedure AddStatus(const AAnswer: TJSONObject);
begin
  AAnswer.AddPair('session', MainForm.CurrentSessionPath);
  AAnswer.AddPair('panel', MainForm.ActivePanelName);
  AAnswer.AddPair('method', MainForm.CurrentMethodName);
  AAnswer.AddPair('visible', TJSONBool.Create(MainForm.Visible));
end;

procedure DoOpen(const ARequest, AAnswer: TJSONObject);
var
  LPath: string;
begin
  LPath := StringOf(ARequest, 'path');
  if LPath = '' then
    raise EArgumentException.Create('open needs a path: the session database, or the directory holding it.');
  MainForm.OpenSessionPath(LPath);
  if StringOf(ARequest, 'panel') <> '' then
    MainForm.ActivatePanel(StringOf(ARequest, 'panel'));
  AddStatus(AAnswer);
end;

procedure DoView(const ARequest, AAnswer: TJSONObject);
var
  LPanel, LMethod, LFilter, LSort: string;
begin
  LPanel := StringOf(ARequest, 'panel');
  LMethod := StringOf(ARequest, 'method');
  LFilter := StringOf(ARequest, 'filter');
  LSort := StringOf(ARequest, 'sortBy');
  if LFilter <> '' then
    MainForm.FilterReport(LFilter);
  if LSort <> '' then
    MainForm.SortReportBy(LSort, not BooleanOf(ARequest, 'ascending', False));
  if LMethod <> '' then
    if not MainForm.FocusMethodByName(LMethod) then
      raise ESessionStore.CreateFmt('No method of this session matches "%s". The report panel lists the names it knows.', [LMethod]);
  if LPanel <> '' then
    if not MainForm.ActivatePanel(LPanel) then
      raise EArgumentException.CreateFmt('Unknown panel "%s". One of: %s.', [LPanel, MainForm.PanelNames]);
  AddStatus(AAnswer);
end;

procedure DoCapture(const ARequest, AAnswer: TJSONObject);
var
  LPanel, LTarget, LPath: string;
  LWidth, LHeight: Integer;
  LControl: TControl;
  LBitmap, LTrimmed: TBitmap;
  LPng: TBytes;
begin
  LPanel := StringOf(ARequest, 'panel');
  LTarget := StringOf(ARequest, 'target', 'panel');
  LWidth := IntegerOf(ARequest, 'width', 0);
  LHeight := IntegerOf(ARequest, 'height', 0);
  if (LWidth > 0) and (LHeight > 0) then
    MainForm.ResizeClient(LWidth, LHeight);
  if LPanel <> '' then
    if not MainForm.ActivatePanel(LPanel) then
      raise EArgumentException.CreateFmt('Unknown panel "%s". One of: %s.', [LPanel, MainForm.PanelNames]);

  if SameText(LTarget, 'window') then
    LControl := MainForm
  else
    LControl := MainForm.PanelControl(LPanel);
  if LControl = nil then
    raise EArgumentException.Create('There is nothing to capture: open a session first.');

  LBitmap := ControlToBitmap(LControl, MainForm.Color);
  try
    // Trimmed unless asked otherwise: a panel that draws on a canvas leaves most of it
    // blank, and the blank is what makes a picture unreadable in a report.
    if BooleanOf(ARequest, 'trim', True) then
    begin
      LTrimmed := TrimToContent(LBitmap);
      if LTrimmed <> nil then
      begin
        LBitmap.Free;
        LBitmap := LTrimmed;
        AAnswer.AddPair('trimmed', TJSONBool.Create(True));
      end;
    end;
    LPng := BitmapToPng(LBitmap);
    AAnswer.AddPair('width', TJSONNumber.Create(LBitmap.Width));
    AAnswer.AddPair('height', TJSONNumber.Create(LBitmap.Height));
    AAnswer.AddPair('blank', TJSONBool.Create(not HasContent(LBitmap)));
  finally
    LBitmap.Free;
  end;

  LPath := StringOf(ARequest, 'path');
  if LPath <> '' then
  begin
    TDirectory.CreateDirectory(TPath.GetDirectoryName(TPath.GetFullPath(LPath)));
    TFile.WriteAllBytes(LPath, LPng);
    AAnswer.AddPair('path', TPath.GetFullPath(LPath));
  end;
  AAnswer.AddPair('bytes', TJSONNumber.Create(Length(LPng)));
  if BooleanOf(ARequest, 'inline', True) then
    AAnswer.AddPair('png', TNetEncoding.Base64.EncodeBytesToString(LPng));
  AAnswer.AddPair('panel', MainForm.ActivePanelName);
  AAnswer.AddPair('target', LTarget);
end;

function ExecuteCommand(const ARequest: TJSONObject): TJSONObject;
var
  LCommand: string;
begin
  Result := TJSONObject.Create;
  try
    if ARequest.GetValue('id') <> nil then
      Result.AddPair('id', TJSONNumber.Create(IntegerOf(ARequest, 'id', 0)));
    LCommand := LowerCase(StringOf(ARequest, 'command'));
    if LCommand = 'status' then
      AddStatus(Result)
    else if LCommand = 'open' then
      DoOpen(ARequest, Result)
    else if LCommand = 'view' then
      DoView(ARequest, Result)
    else if LCommand = 'capture' then
      DoCapture(ARequest, Result)
    else if LCommand = 'show' then
    begin
      MainForm.SetWindowVisible(True);
      AddStatus(Result);
    end
    else if LCommand = 'hide' then
    begin
      MainForm.SetWindowVisible(False);
      AddStatus(Result);
    end
    else if LCommand = 'close' then
    begin
      Result.AddPair('closing', TJSONBool.Create(True));
      PostMessage(MainForm.Handle, WM_CLOSE, 0, 0);
    end
    else
      raise EArgumentException.CreateFmt(
        'Unknown command "%s". One of: status, open, view, capture, show, hide, close.', [LCommand]);
    Result.AddPair('ok', TJSONBool.Create(True));
  except
    on E: Exception do
    begin
      Result.Free;
      Result := TJSONObject.Create;
      if ARequest.GetValue('id') <> nil then
        Result.AddPair('id', TJSONNumber.Create(IntegerOf(ARequest, 'id', 0)));
      Result.AddPair('ok', TJSONBool.Create(False));
      Result.AddPair('error', E.Message);
    end;
  end;
end;

{ TChannelThread }

constructor TChannelThread.Create;
begin
  inherited Create(False);
  FreeOnTerminate := False;
  FInput := THandleStream.Create(GetStdHandle(STD_INPUT_HANDLE));
  FReader := TStreamReader.Create(FInput, TEncoding.UTF8);
end;

destructor TChannelThread.Destroy;
begin
  FReader.Free;
  FInput.Free;
  inherited;
end;

procedure TChannelThread.HandleLine(const ALine: string);
var
  LRequest: TJSONObject;
  LAnswer: TJSONObject;
  LText: string;
begin
  LRequest := TJSONObject.ParseJSONValue(ALine) as TJSONObject;
  if LRequest = nil then
  begin
    WriteLine('{"ok":false,"error":"That line is not a JSON object."}');
    Exit;
  end;
  try
    // The command touches the window, so it belongs to the main thread; writing the
    // answer from the same block keeps answers in the order of their requests.
    TThread.Synchronize(nil,
      procedure
      begin
        LAnswer := ExecuteCommand(LRequest);
        try
          LText := LAnswer.ToJSON;
        finally
          LAnswer.Free;
        end;
        WriteLine(LText);
      end);
  finally
    LRequest.Free;
  end;
end;

procedure TChannelThread.Execute;
var
  LLine: string;
begin
  NameThreadForDebugging('nap-gui-control');
  while not Terminated do
  begin
    LLine := FReader.ReadLine;
    if LLine = '' then
    begin
      // End of input: the process that owns this window has gone, so the window goes too.
      if FReader.EndOfStream then
      begin
        if MainForm <> nil then
          PostMessage(MainForm.Handle, WM_CLOSE, 0, 0);
        Break;
      end;
      Continue;
    end;
    HandleLine(LLine);
  end;
end;

procedure EndBecauseTheOwnerIsGone; forward;

type
  /// Waits on the owner's handle and closes the window when it is signalled.
  TParentWatchThread = class(TThread)
  private
    FParent: THandle;
  protected
    procedure Execute; override;
  public
    constructor Create(AParent: THandle);
  end;

constructor TParentWatchThread.Create(AParent: THandle);
begin
  FParent := AParent;
  FreeOnTerminate := True;
  inherited Create(False);
end;

procedure TParentWatchThread.Execute;
begin
  NameThreadForDebugging('nap-gui-parent-watch');
  WaitForSingleObject(FParent, INFINITE);
  CloseHandle(FParent);
  EndBecauseTheOwnerIsGone;
end;

/// The owner is gone, so this window has to go. It is asked politely first; measured on
/// this window, a WM_CLOSE that closes it perfectly well when it came from the channel's
/// end of input does not always take here, and a window nobody can see is a window nobody
/// will ever close by hand - so the process ends itself if the polite way has not worked
/// within a few seconds. Nothing is lost: a driven window writes neither layout nor
/// settings, and its session database belongs to the profiler, not to it.
procedure EndBecauseTheOwnerIsGone;
begin
  if MainForm <> nil then
    PostMessage(MainForm.Handle, WM_CLOSE, 0, 0);
  Sleep(3000);
  ExitProcess(0);
end;

procedure WatchParentProcess(APid: Cardinal);
var
  LParent: THandle;
begin
  GParentPid := APid;
  if APid = 0 then
    Exit;
  LParent := OpenProcess(SYNCHRONIZE, False, APid);
  // A parent that is already gone means this window has nobody: end the same way, but not
  // on this thread - the caller is still setting the window up.
  if LParent = 0 then
  begin
    TThread.CreateAnonymousThread(EndBecauseTheOwnerIsGone).Start;
    Exit;
  end;
  TParentWatchThread.Create(LParent);
end;

procedure StartControlChannel;
begin
  if GChannel <> nil then
    Exit;
  GWriteLock := TObject.Create;
  GOutput := THandleStream.Create(GetStdHandle(STD_OUTPUT_HANDLE));
  GChannel := TChannelThread.Create;
  // The readiness line, the way dsrouter announces itself: the client waits for it
  // instead of sleeping and hoping.
  WriteLine(Format('{"ready":true,"pid":%d,"parent":%d,"gui":%s}', [GetCurrentProcessId, GParentPid,
    TJSONString.Create(ParamStr(0)).ToJSON]));
end;

initialization

finalization
  if GChannel <> nil then
  begin
    GChannel.Terminate;
    GChannel.Free;
  end;
  GOutput.Free;
  GWriteLock.Free;

end.
