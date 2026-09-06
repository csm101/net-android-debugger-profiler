unit uCrashDialog;

{
  What the user sees when something goes wrong: the exception, what the application was
  doing, and the call stack - on screen, not only in a file. A message box saying "details
  in NapGui.error.log" asks someone to go and find a file; the stack is the thing worth
  reading, so it is right there, selectable and copyable.

  Deliberately plain VCL. This dialog runs after something has already failed, and the
  failure may well be inside the skinning or the docking library: a crash reporter that
  needs the framework that just crashed is no reporter at all.
}

interface

uses
  System.SysUtils, System.Classes,
  Vcl.Controls, Vcl.Forms, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.Graphics;

/// Shows the report and waits. Safe to call from an exception handler; a second call while
/// the dialog is up is ignored, so a crash inside the reporter cannot loop.
procedure ShowCrashDialog(const AReport, ALogPath: string);

implementation

uses
  Winapi.Windows, Winapi.ShellAPI, System.UITypes, Vcl.Clipbrd;

type
  TCrashDialog = class(TForm)
  private
    FReport: TMemo;
    FLogPath: string;
    procedure CopyClick(Sender: TObject);
    procedure OpenLogClick(Sender: TObject);
    procedure Build(const AReport, ALogPath: string);
  end;

var
  GShowing: Boolean = False;

procedure TCrashDialog.Build(const AReport, ALogPath: string);

  function Button(const ACaption: string; ALeft: Integer; AOnClick: TNotifyEvent): TButton;
  begin
    Result := TButton.Create(Self);
    Result.Parent := Self;
    Result.SetBounds(ALeft, 396, 120, 28);
    Result.Anchors := [akLeft, akBottom];
    Result.Caption := ACaption;
    Result.OnClick := AOnClick;
  end;

var
  LTitle: TLabel;
  LClose: TButton;
begin
  FLogPath := ALogPath;
  Caption := 'Unexpected error';
  BorderStyle := bsSizeable;
  Position := poScreenCenter;
  ClientWidth := 760;
  ClientHeight := 436;
  Constraints.MinWidth := 520;
  Constraints.MinHeight := 300;

  LTitle := TLabel.Create(Self);
  LTitle.Parent := Self;
  // AutoSize is on by default and wins over the width set below: a wrapped label then
  // squeezes itself into a column a few words wide.
  LTitle.AutoSize := False;
  LTitle.SetBounds(12, 12, 736, 32);
  LTitle.Anchors := [akLeft, akTop, akRight];
  LTitle.WordWrap := True;
  LTitle.Caption := 'The profiler ran into something it did not expect. What follows is the whole of '
    + 'what it knows; the same text is in the log.';

  FReport := TMemo.Create(Self);
  FReport.Parent := Self;
  FReport.SetBounds(12, 52, 736, 332);
  FReport.Anchors := [akLeft, akTop, akRight, akBottom];
  FReport.ReadOnly := True;
  FReport.ScrollBars := ssBoth;
  FReport.WordWrap := False;
  FReport.Font.Name := 'Consolas';
  FReport.Font.Size := 9;
  FReport.Text := AReport;
  // The first line of the stack is the one that matters: start there, not at the end.
  FReport.SelStart := 0;

  Button('Copy', 12, CopyClick);
  Button('Open the log', 140, OpenLogClick);

  LClose := TButton.Create(Self);
  LClose.Parent := Self;
  LClose.SetBounds(628, 396, 120, 28);
  LClose.Anchors := [akRight, akBottom];
  LClose.Caption := 'Close';
  LClose.ModalResult := mrOk;
  LClose.Default := True;
  LClose.Cancel := True;
end;

procedure TCrashDialog.CopyClick(Sender: TObject);
begin
  try
    Clipboard.AsText := FReport.Text;
  except
    // a clipboard another application is holding is not worth a second failure here
  end;
end;

procedure TCrashDialog.OpenLogClick(Sender: TObject);
begin
  if FLogPath = '' then
    Exit;
  ShellExecute(0, 'open', PChar(FLogPath), nil, nil, SW_SHOWNORMAL);
end;

procedure ShowCrashDialog(const AReport, ALogPath: string);
var
  LDialog: TCrashDialog;
begin
  // A failure raised while reporting a failure would otherwise stack dialogs for ever.
  if GShowing then
    Exit;
  GShowing := True;
  try
    LDialog := TCrashDialog.CreateNew(nil);
    try
      LDialog.Build(AReport, ALogPath);
      LDialog.ShowModal;
    finally
      LDialog.Free;
    end;
  except
    on E: Exception do
      // Last resort: the plainest thing Windows can show.
      MessageBox(0, PChar(AReport), 'Unexpected error', MB_OK or MB_ICONERROR);
  end;
  GShowing := False;
end;

end.
