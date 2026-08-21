program NapGui;

{
  Delphi + DevExpress front end of the .NET for Android profiler (P4).

  It reads a session database directly - that file is the contract, see
  ARCHITECTURE.md - and drives sessions through the local control service
  (nap serve, docs/CONTROL_SERVICE.md). Build it with build-gui.cmd, which takes
  the DevExpress and SynEdit paths from the installed IDE.
}

uses
  System.SysUtils,
  System.IOUtils,
  System.UITypes,
  Vcl.Forms,
  Vcl.Dialogs,
  // FireDAC needs its VCL wait-cursor component linked into a GUI application,
  // otherwise the first connection fails with a missing object factory.
  FireDAC.VCLUI.Wait,
  uMainForm in 'src\uMainForm.pas',
  uSessionStore in 'src\uSessionStore.pas',
  uControlClient in 'src\uControlClient.pas',
  uSetupDialog in 'src\uSetupDialog.pas';

{$R *.res}

/// A GUI has nowhere to print a startup failure, and a bare "Application Error" is the
/// worst thing to hand a user: say what happened and leave it in a file as well.
procedure ReportFatal(E: Exception);
var
  LPath: string;
begin
  LPath := TPath.ChangeExtension(ParamStr(0), '.error.log');
  try
    TFile.AppendAllText(LPath, Format('%s  %s: %s'#13#10, [DateTimeToStr(Now), E.ClassName, E.Message]));
  except
    // logging must never be the thing that fails
  end;
  MessageDlg(Format('%s'#13#10#13#10'%s'#13#10#13#10'Details in %s', [E.ClassName, E.Message, LPath]),
    mtError, [mbOK], 0);
end;

begin
  try
    Application.Initialize;
    Application.MainFormOnTaskbar := True;
    Application.Title := '.NET for Android profiler';
    Application.CreateForm(TMainForm, MainForm);
    Application.Run;
  except
    on E: Exception do
      ReportFatal(E);
  end;
end.
