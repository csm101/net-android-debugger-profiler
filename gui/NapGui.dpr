program NapGui;

{
  Delphi + DevExpress front end of the .NET for Android profiler (P4).

  It reads a session database directly - that file is the contract, see
  ARCHITECTURE.md - and drives sessions through the local control service
  (nap serve, docs/CONTROL_SERVICE.md). Build it with build-gui.cmd, which takes
  the DevExpress and SynEdit paths from the installed IDE.
}

uses
  Winapi.Windows,
  System.SysUtils,
  System.IOUtils,
  System.UITypes,
  Vcl.Forms,
  System.Classes,
  Vcl.Controls,
  Vcl.Graphics,
  Vcl.ExtCtrls,
  Vcl.StdCtrls,
  Vcl.Dialogs,
  // MPL-1.1, used unmodified: turns a crash report from an address into a unit and a line.
  JclDebug,
  // FireDAC needs its VCL wait-cursor component linked into a GUI application,
  // otherwise the first connection fails with a missing object factory.
  FireDAC.VCLUI.Wait,
  uMainForm in 'src\uMainForm.pas',
  uGuiControl in 'src\uGuiControl.pas',
  uGuiRender in 'src\uGuiRender.pas',
  uSessionStore in 'src\uSessionStore.pas',
  uControlClient in 'src\uControlClient.pas',
  uSetupDialog in 'src\uSetupDialog.pas',
  uCallspecDialog in 'src\uCallspecDialog.pas',
  uJobDialog in 'src\uJobDialog.pas',
  uCrashDialog in 'src\uCrashDialog.pas',
  uTheme in 'src\uTheme.pas',
  uGlyphs in 'src\uGlyphs.pas',
  uSettings in 'src\uSettings.pas',
  uSettingsDialog in 'src\uSettingsDialog.pas',
  uLayouts in 'src\uLayouts.pas',
  uLayoutDialog in 'src\uLayoutDialog.pas';

{$R *.res}

/// The call stack of the exception being handled, resolved to units and lines through the
/// map file next to the executable (JclDebug). Without it a crash report says what broke
/// and never where, which is an hour of guessing every time.
function CallStackOf: string;
var
  LLines: TStringList;
begin
  LLines := TStringList.Create;
  try
    try
      if JclLastExceptStackListToStrings(LLines, True, True, True, False) and (LLines.Count > 0) then
      begin
        // The first frames are the hook that captured the exception: they are the same in
        // every report and push the line that matters off the first screen.
        while (LLines.Count > 0) and (LLines[0].Contains('}Jcl') or LLines[0].Contains(' JclDebug.')
          or LLines[0].Contains(' JclHookExcept.') or LLines[0].Contains('System.@RaiseExcept')
          or LLines[0].Contains('System.@InternalRaiseAtExcept')) do
          LLines.Delete(0);
        Result := LLines.Text;
      end
      else
        Result := '  (no stack: build with the detailed map file next to NapGui.exe)'#13#10;
    except
      on E: Exception do
        Result := '  (the stack could not be read: ' + E.Message + ')'#13#10;
    end;
  finally
    LLines.Free;
  end;
end;

/// A GUI has nowhere to print a failure, and a bare "Application Error" is the worst thing
/// to hand a user: say what happened, where it happened, and leave all of it in a file.
procedure ReportFatal(E: Exception; const AWhere: string = 'starting up');
var
  LPath, LReport: string;
begin
  LPath := TPath.ChangeExtension(ParamStr(0), '.error.log');
  LReport := Format('%s  %s: %s'#13#10'  while %s'#13#10#13#10'Call stack:'#13#10'%s',
    [DateTimeToStr(Now), E.ClassName, E.Message, AWhere, CallStackOf]);
  try
    TFile.AppendAllText(LPath, LReport + #13#10);
  except
    // logging must never be the thing that fails
  end;
  // On screen as well as in the file: the stack is what someone reading this needs, and
  // sending them to find a log is asking them not to look.
  ShowCrashDialog(LReport, LPath);
end;

type
  /// Everything the VCL would otherwise show as a bare message box goes through here, so
  /// that a crash in a click handler leaves the same trace a startup crash does.
  TExceptionReporter = class
    procedure Handle(ASender: TObject; AException: Exception);
  end;

procedure TExceptionReporter.Handle(ASender: TObject; AException: Exception);
begin
  ReportFatal(AException, 'running');
end;

var
  GReporter: TExceptionReporter;

/// Building the main window - eight panels of DevExpress controls, the skins, the
/// docking layout - takes seconds on a cold start, and until it is up the double click on
/// the icon looks like it did nothing. A plain VCL form (no skin, nothing to load) shows
/// in milliseconds and says the application is coming.
function ShowSplash: TForm;
var
  LLabel: TLabel;
begin
  Result := TForm.CreateNew(nil);
  Result.BorderStyle := bsNone;
  Result.Position := poScreenCenter;
  Result.ClientWidth := 340;
  Result.ClientHeight := 90;
  Result.Color := $00201F1E;
  LLabel := TLabel.Create(Result);
  LLabel.Parent := Result;
  LLabel.Align := alClient;
  LLabel.Alignment := taCenter;
  LLabel.Layout := tlCenter;
  LLabel.Font.Color := clWhite;
  LLabel.Font.Size := 11;
  LLabel.Caption := '.NET for Android profiler'#13#10'starting...';
  Result.Show;
  Result.Update;
end;

/// --control: the window is driven over standard input and output (uGuiControl) and,
/// unless somebody asks for it, never shown. Nothing may be printed on stdout in that
/// mode except the channel's own answers, which is why the splash is skipped too.
function ControlMode: Boolean;
var
  LIndex: Integer;
begin
  for LIndex := 1 to ParamCount do
    if SameText(ParamStr(LIndex), '--control') then
      Exit(True);
  Result := False;
end;

/// --render=<panel>:<file>: the same window, used once for one picture. Like --control
/// it is a window nobody looks at, so it takes the same path: no splash, no saved
/// layout, and the arrangement this code builds rather than the one somebody left.
function RenderMode: Boolean;
var
  LIndex: Integer;
begin
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--render=', True) then
      Exit(True);
  Result := False;
end;

var
  GSplash: TForm;
begin
  // Assigned before anything can throw: the shutdown below frees it whatever happened.
  GReporter := nil;
  try
    GStartTicks := GetTickCount64;
    // Tracking has to start before anything else can throw.
    JclStartExceptionTracking;
    GReporter := TExceptionReporter.Create;
    Application.Initialize;
    Application.OnException := GReporter.Handle;
    Application.MainFormOnTaskbar := True;
    Application.Title := '.NET for Android profiler';
    if ControlMode or RenderMode then
    begin
      Application.ShowMainForm := False;
      GDrivenWindow := True;
      Application.CreateForm(TMainForm, MainForm);
      if ControlMode then
        StartControlChannel;
    end
    else
    begin
      GSplash := ShowSplash;
      try
        Application.CreateForm(TMainForm, MainForm);
      finally
        GSplash.Free;
      end;
    end;
    Application.Run;
  except
    on E: Exception do
      ReportFatal(E);
  end;
  try
    JclStopExceptionTracking;
    GReporter.Free;
  except
    // shutting down: nothing here is worth failing over
  end;
end.
