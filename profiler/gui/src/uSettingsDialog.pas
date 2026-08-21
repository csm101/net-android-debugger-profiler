unit uSettingsDialog;

{
  The settings window: theme, the unit times are read in, the font used for code and
  logs, where sessions are kept, and which nap.exe drives them. Everything here is a
  preference - nothing changes what the profiler measures.
}

interface

uses
  System.SysUtils, System.Classes, System.UITypes, System.Math,
  Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.Graphics,
  cxLabel, cxButtons, cxDropDownEdit, cxTextEdit, cxMaskEdit,
  uTheme, uSessionStore, uSettings;

type
  TSettingsDialog = class(TForm)
  private
    FTheme: TcxComboBox;
    FUnits: TcxComboBox;
    FFontPreview: TcxLabel;
    FSessionsRoot: TcxTextEdit;
    FNapPath: TcxTextEdit;
    FFontName: string;
    FFontSize: Integer;
    procedure Build;
    procedure PickFontClick(Sender: TObject);
    procedure BrowseRootClick(Sender: TObject);
    procedure BrowseNapClick(Sender: TObject);
    procedure UpdateFontPreview;
  public
    constructor Create(AOwner: TComponent); reintroduce;
    /// Shows the dialog; when it returns True the settings have been written.
    function Execute: Boolean;
  end;

implementation

uses
  System.IOUtils, Vcl.FileCtrl;

constructor TSettingsDialog.Create(AOwner: TComponent);
begin
  inherited CreateNew(AOwner);
  Build;
end;

procedure TSettingsDialog.Build;

  function Caption_(const AText: string; ATop: Integer): TcxLabel;
  begin
    Result := TcxLabel.Create(Self);
    Result.Transparent := True;
    Result.Parent := Self;
    Result.Left := 16;
    Result.Top := ATop + 4;
    Result.Caption := AText;
  end;

var
  LFontButton, LRootButton, LNapButton, LOk, LCancel: TcxButton;
begin
  Caption := 'Settings';
  BorderStyle := bsDialog;
  Position := poOwnerFormCenter;
  ClientWidth := 620;
  ClientHeight := 260;

  Caption_('Theme', 16);
  FTheme := TcxComboBox.Create(Self);
  FTheme.Parent := Self;
  FTheme.SetBounds(160, 16, 180, 24);
  FTheme.Properties.DropDownListStyle := lsFixedList;
  FTheme.Properties.Items.Add(ThemeName(atLight));
  FTheme.Properties.Items.Add(ThemeName(atDark));

  Caption_('Show times in', 48);
  FUnits := TcxComboBox.Create(Self);
  FUnits.Parent := Self;
  FUnits.SetBounds(160, 48, 180, 24);
  FUnits.Properties.DropDownListStyle := lsFixedList;
  FUnits.Properties.Items.Add(TimeUnitName(tuAuto));
  FUnits.Properties.Items.Add(TimeUnitName(tuSeconds));
  FUnits.Properties.Items.Add(TimeUnitName(tuMilliseconds));
  FUnits.Properties.Items.Add(TimeUnitName(tuMicroseconds));
  FUnits.Properties.Items.Add(TimeUnitName(tuNanoseconds));

  Caption_('Code font', 80);
  FFontPreview := TcxLabel.Create(Self);
  FFontPreview.Transparent := True;
  FFontPreview.Parent := Self;
  FFontPreview.SetBounds(160, 84, 320, 20);
  LFontButton := TcxButton.Create(Self);
  LFontButton.Parent := Self;
  LFontButton.SetBounds(496, 80, 100, 26);
  LFontButton.Caption := 'Choose...';
  LFontButton.OnClick := PickFontClick;

  Caption_('Sessions folder', 116);
  FSessionsRoot := TcxTextEdit.Create(Self);
  FSessionsRoot.Parent := Self;
  FSessionsRoot.SetBounds(160, 116, 330, 24);
  FSessionsRoot.TextHint := 'default: %LOCALAPPDATA%\net-android-profiler\sessions';
  LRootButton := TcxButton.Create(Self);
  LRootButton.Parent := Self;
  LRootButton.SetBounds(496, 115, 100, 26);
  LRootButton.Caption := 'Browse...';
  LRootButton.OnClick := BrowseRootClick;

  Caption_('nap.exe', 148);
  FNapPath := TcxTextEdit.Create(Self);
  FNapPath.Parent := Self;
  FNapPath.SetBounds(160, 148, 330, 24);
  FNapPath.TextHint := 'default: next to this application';
  LNapButton := TcxButton.Create(Self);
  LNapButton.Parent := Self;
  LNapButton.SetBounds(496, 147, 100, 26);
  LNapButton.Caption := 'Browse...';
  LNapButton.OnClick := BrowseNapClick;

  with Caption_('The control service the GUI starts to run sessions.', 180) do
  begin
    Left := 160;
    Style.Font.Color := clGrayText;
  end;

  LOk := TcxButton.Create(Self);
  LOk.Parent := Self;
  LOk.SetBounds(420, 216, 90, 28);
  LOk.Caption := 'OK';
  LOk.ModalResult := mrOk;
  LOk.Default := True;

  LCancel := TcxButton.Create(Self);
  LCancel.Parent := Self;
  LCancel.SetBounds(516, 216, 90, 28);
  LCancel.Caption := 'Cancel';
  LCancel.ModalResult := mrCancel;
  LCancel.Cancel := True;
end;

procedure TSettingsDialog.UpdateFontPreview;
begin
  FFontPreview.Style.Font.Name := FFontName;
  FFontPreview.Style.Font.Size := FFontSize;
  FFontPreview.Caption := Format('%s %d  -  if (x < 10) { return 0; }', [FFontName, FFontSize]);
end;

procedure TSettingsDialog.PickFontClick(Sender: TObject);
var
  LDialog: TFontDialog;
begin
  LDialog := TFontDialog.Create(Self);
  try
    LDialog.Font.Name := FFontName;
    LDialog.Font.Size := FFontSize;
    // Code is read in columns: offer the fonts that keep them.
    LDialog.Options := LDialog.Options + [fdFixedPitchOnly];
    if not LDialog.Execute then
      Exit;
    FFontName := LDialog.Font.Name;
    FFontSize := LDialog.Font.Size;
    UpdateFontPreview;
  finally
    LDialog.Free;
  end;
end;

procedure TSettingsDialog.BrowseRootClick(Sender: TObject);
var
  LDirectory: string;
begin
  LDirectory := FSessionsRoot.Text;
  if SelectDirectory('Where profiling sessions are kept', '', LDirectory) then
    FSessionsRoot.Text := LDirectory;
end;

procedure TSettingsDialog.BrowseNapClick(Sender: TObject);
var
  LDialog: TOpenDialog;
begin
  LDialog := TOpenDialog.Create(Self);
  try
    LDialog.Title := 'Where is nap.exe?';
    LDialog.Filter := 'nap.exe|nap.exe|Applications (*.exe)|*.exe';
    if LDialog.Execute then
      FNapPath.Text := LDialog.FileName;
  finally
    LDialog.Free;
  end;
end;

function TSettingsDialog.Execute: Boolean;
begin
  FTheme.ItemIndex := Ord(GSettings.Theme);
  FUnits.ItemIndex := Ord(GSettings.TimeUnit);
  FFontName := GSettings.CodeFontName;
  FFontSize := GSettings.CodeFontSize;
  UpdateFontPreview;
  FSessionsRoot.Text := GSettings.SessionsRoot;
  FNapPath.Text := GSettings.NapExePath;

  Result := ShowModal = mrOk;
  if not Result then
    Exit;

  GSettings.Theme := TAppTheme(Max(0, FTheme.ItemIndex));
  GSettings.TimeUnit := TTimeUnit(Max(0, FUnits.ItemIndex));
  GSettings.CodeFontName := FFontName;
  GSettings.CodeFontSize := FFontSize;
  GSettings.SessionsRoot := Trim(FSessionsRoot.Text);
  GSettings.NapExePath := Trim(FNapPath.Text);
  GTheme := GSettings.Theme;
  GTimeUnit := GSettings.TimeUnit;
  SaveSettings;
end;

end.
