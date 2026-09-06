unit uJobDialog;

{
  Watches a background job of the control service - a build and install, or the install of
  a missing tool - and shows its output while it runs.

  The service streams the job's log, so this polls for the lines it has not seen yet
  rather than waiting for the end: a build takes minutes and the interesting part is
  usually the first error, not the exit code.
}

interface

uses
  System.SysUtils, System.Classes,
  Vcl.Controls, Vcl.Forms, Vcl.ExtCtrls, Vcl.StdCtrls, Vcl.Graphics,
  cxTextEdit, cxMemo, cxButtons, cxLabel,
  uControlClient, uTheme;

type
  TJobDialog = class(TForm)
  private
    FClient: TControlClient;
    FJobId: string;
    FMemo: TcxMemo;
    FHeader: TcxLabel;
    FTimer: TTimer;
    FClose: TcxButton;
    FCancel: TcxButton;
    /// The next log line to ask the service for, so each poll carries only what is new.
    FNext: Integer;
    FSucceeded: Boolean;
    FFinished: Boolean;
    procedure Build(const ACaption, AHeader: string);
    procedure Poll(Sender: TObject);
    procedure CancelClick(Sender: TObject);
    procedure Apply(const AStatus: TJobStatus);
  public
    constructor Create(AOwner: TComponent; AClient: TControlClient;
      const AJob: TJobStatus; const ACaption, AHeader: string); reintroduce;
    /// Runs the dialog to the end of the job; True when the job succeeded.
    class function Run(AOwner: TComponent; AClient: TControlClient;
      const AJob: TJobStatus; const ACaption, AHeader: string): Boolean;
  end;

implementation

uses
  System.UITypes;

constructor TJobDialog.Create(AOwner: TComponent; AClient: TControlClient;
  const AJob: TJobStatus; const ACaption, AHeader: string);
begin
  inherited CreateNew(AOwner);
  FClient := AClient;
  FJobId := AJob.Id;
  Build(ACaption, AHeader);
  Apply(AJob);
end;

class function TJobDialog.Run(AOwner: TComponent; AClient: TControlClient;
  const AJob: TJobStatus; const ACaption, AHeader: string): Boolean;
var
  LDialog: TJobDialog;
begin
  LDialog := TJobDialog.Create(AOwner, AClient, AJob, ACaption, AHeader);
  try
    LDialog.ShowModal;
    Result := LDialog.FSucceeded;
  finally
    LDialog.Free;
  end;
end;

procedure TJobDialog.Build(const ACaption, AHeader: string);
var
  LColors: TThemeColors;
begin
  LColors := ThemeColors;
  Caption := ACaption;
  BorderStyle := bsSizeable;
  Position := poOwnerFormCenter;
  ClientWidth := 760;
  ClientHeight := 420;
  // msbuild's lines are long: widen it as much as the screen allows.
  Constraints.MinWidth := 480;
  Constraints.MinHeight := 260;
  Color := LColors.Window;

  FHeader := TcxLabel.Create(Self);
  FHeader.Transparent := True;
  FHeader.Parent := Self;
  FHeader.SetBounds(12, 12, 736, 20);
  FHeader.Anchors := [akLeft, akTop, akRight];
  FHeader.Properties.WordWrap := True;
  FHeader.Caption := AHeader;

  FMemo := TcxMemo.Create(Self);
  FMemo.Parent := Self;
  FMemo.SetBounds(12, 40, 736, 330);
  FMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  FMemo.Properties.ReadOnly := True;
  FMemo.Properties.ScrollBars := ssBoth;
  FMemo.Properties.WordWrap := False;
  FMemo.Style.Font.Name := 'Consolas';
  FMemo.Style.Font.Size := 9;

  FCancel := TcxButton.Create(Self);
  FCancel.Parent := Self;
  FCancel.SetBounds(12, 380, 110, 28);
  FCancel.Anchors := [akLeft, akBottom];
  FCancel.Caption := 'Cancel';
  FCancel.OnClick := CancelClick;

  FClose := TcxButton.Create(Self);
  FClose.Parent := Self;
  FClose.SetBounds(648, 380, 100, 28);
  FClose.Anchors := [akRight, akBottom];
  FClose.Caption := 'Close';
  FClose.ModalResult := mrOk;
  FClose.Default := True;
  // Closing the window before the job ends would leave it running unattended; the button
  // becomes usable when there is something to close.
  FClose.Enabled := False;

  FTimer := TTimer.Create(Self);
  FTimer.Interval := 500;
  FTimer.OnTimer := Poll;
  FTimer.Enabled := True;
end;

procedure TJobDialog.Apply(const AStatus: TJobStatus);
var
  I: Integer;
begin
  if AStatus.LogFrom > FNext then
    // Lines were dropped because the log outgrew its cap: say so instead of pretending
    // the output is continuous.
    FMemo.Lines.Add(Format('... %d lines dropped ...', [AStatus.LogFrom - FNext]));
  for I := 0 to High(AStatus.Log) do
    FMemo.Lines.Add(AStatus.Log[I]);
  FNext := AStatus.LogTotal;

  if AStatus.Running then
    Exit;

  FFinished := True;
  FTimer.Enabled := False;
  FSucceeded := AStatus.Succeeded;
  FCancel.Enabled := False;
  FClose.Enabled := True;
  FClose.SetFocus;
  if AStatus.Error <> '' then
    FHeader.Caption := AStatus.Error
  else if FSucceeded then
    FHeader.Caption := 'Done.'
  else
    FHeader.Caption := Format('%s (exit code %d)', [AStatus.State, AStatus.ExitCode]);
end;

procedure TJobDialog.Poll(Sender: TObject);
begin
  if FFinished then
    Exit;
  try
    Apply(FClient.Job(FJobId, FNext));
  except
    on E: Exception do
    begin
      FTimer.Enabled := False;
      FFinished := True;
      FMemo.Lines.Add(E.Message);
      FHeader.Caption := 'Lost contact with the control service.';
      FClose.Enabled := True;
      FCancel.Enabled := False;
    end;
  end;
end;

procedure TJobDialog.CancelClick(Sender: TObject);
begin
  try
    FClient.CancelJob(FJobId);
  except
    on E: Exception do
      FMemo.Lines.Add(E.Message);
  end;
end;

end.
