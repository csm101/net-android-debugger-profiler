unit uSetupDialog;

{
  What to profile, before a run: AQTime's Setup panel, reduced to the choices this
  profiler actually has. The device list comes from the control service, and the
  prerequisite check ("can this app be profiled in this mode?") runs here rather than
  failing later inside the session.
}

interface

uses
  System.SysUtils, System.Classes,
  Vcl.Controls, Vcl.Forms, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.Dialogs,
  uControlClient;

type
  TSetupResult = record
    DeviceSerial: string;
    Package: string;
    Mode: string;
    Engine: string;
    Callspec: string;
    DurationSeconds: Integer;
    SymbolsDir: string;
  end;

  TSetupDialog = class(TForm)
  private
    FClient: TControlClient;
    FDevices: TComboBox;
    FPackage: TEdit;
    FMode: TComboBox;
    FEngine: TComboBox;
    FCallspec: TEdit;
    FDuration: TEdit;
    FSymbols: TEdit;
    FCheckLabel: TLabel;
    FOk: TButton;
    procedure Build;
    procedure ModeChanged(Sender: TObject);
    procedure CheckClick(Sender: TObject);
  public
    constructor Create(AOwner: TComponent; AClient: TControlClient); reintroduce;
    function Execute(out AResult: TSetupResult): Boolean;
  end;

implementation

uses
  System.UITypes;

constructor TSetupDialog.Create(AOwner: TComponent; AClient: TControlClient);
begin
  inherited CreateNew(AOwner);
  FClient := AClient;
  Build;
end;

procedure TSetupDialog.Build;

  function Label_(const AText: string; ATop: Integer): TLabel;
  begin
    Result := TLabel.Create(Self);
    Result.Parent := Self;
    Result.Left := 16;
    Result.Top := ATop + 4;
    Result.Caption := AText;
  end;

var
  LCheck, LCancel: TButton;
begin
  Caption := 'New profiling session';
  BorderStyle := bsDialog;
  Position := poOwnerFormCenter;
  ClientWidth := 560;
  ClientHeight := 330;

  Label_('Device', 16);
  FDevices := TComboBox.Create(Self);
  FDevices.Parent := Self;
  FDevices.SetBounds(140, 16, 400, 24);
  FDevices.Style := csDropDownList;

  Label_('Package', 48);
  FPackage := TEdit.Create(Self);
  FPackage.Parent := Self;
  FPackage.SetBounds(140, 48, 400, 24);
  FPackage.TextHint := 'com.example.app';

  Label_('Mode', 80);
  FMode := TComboBox.Create(Self);
  FMode.Parent := Self;
  FMode.SetBounds(140, 80, 200, 24);
  FMode.Style := csDropDownList;
  FMode.Items.Add('sampling');
  FMode.Items.Add('instrumenting');
  FMode.Items.Add('heap');
  FMode.ItemIndex := 0;
  FMode.OnChange := ModeChanged;

  Label_('Engine', 112);
  FEngine := TComboBox.Create(Self);
  FEngine.Parent := Self;
  FEngine.SetBounds(140, 112, 200, 24);
  FEngine.Style := csDropDownList;
  FEngine.Items.Add('weaver');      // the engine that supports live control
  FEngine.Items.Add('provider');
  FEngine.ItemIndex := 0;

  Label_('Callspec', 144);
  FCallspec := TEdit.Create(Self);
  FCallspec.Parent := Self;
  FCallspec.SetBounds(140, 144, 400, 24);
  FCallspec.TextHint := 'N:My.App.Namespace or T:My.App.Type';

  Label_('Duration (s)', 176);
  FDuration := TEdit.Create(Self);
  FDuration.Parent := Self;
  FDuration.SetBounds(140, 176, 80, 24);
  FDuration.Text := '0';
  with Label_('0 = until you stop it', 176) do
    Left := 232;

  Label_('Build output', 208);
  FSymbols := TEdit.Create(Self);
  FSymbols.Parent := Self;
  FSymbols.SetBounds(140, 208, 400, 24);
  FSymbols.TextHint := 'bin\Debug\net9.0-android35.0 - the pdbs, so results carry source locations';

  FCheckLabel := TLabel.Create(Self);
  FCheckLabel.Parent := Self;
  FCheckLabel.SetBounds(16, 240, 528, 32);
  FCheckLabel.WordWrap := True;
  FCheckLabel.Caption := '';

  LCheck := TButton.Create(Self);
  LCheck.Parent := Self;
  LCheck.SetBounds(16, 288, 120, 28);
  LCheck.Caption := 'Check app';
  LCheck.OnClick := CheckClick;

  FOk := TButton.Create(Self);
  FOk.Parent := Self;
  FOk.SetBounds(360, 288, 90, 28);
  FOk.Caption := 'Start';
  FOk.ModalResult := mrOk;
  FOk.Default := True;

  LCancel := TButton.Create(Self);
  LCancel.Parent := Self;
  LCancel.SetBounds(456, 288, 90, 28);
  LCancel.Caption := 'Cancel';
  LCancel.ModalResult := mrCancel;
  LCancel.Cancel := True;

  ModeChanged(nil);
end;

procedure TSetupDialog.ModeChanged(Sender: TObject);
var
  LInstrumenting: Boolean;
begin
  LInstrumenting := FMode.Text = 'instrumenting';
  // A callspec is meaningless outside instrumenting, and mandatory inside it: profiling
  // every method makes the app unusable, so the engine refuses an empty one.
  FCallspec.Enabled := LInstrumenting;
  FEngine.Enabled := LInstrumenting;
end;

procedure TSetupDialog.CheckClick(Sender: TObject);
var
  LProblems: TArray<string>;
begin
  if (FDevices.Text = '') or (FPackage.Text = '') then
  begin
    FCheckLabel.Caption := 'Pick a device and type a package name first.';
    Exit;
  end;
  try
    LProblems := FClient.CheckApp(FDevices.Text, Trim(FPackage.Text), FMode.Text);
    if Length(LProblems) = 0 then
      FCheckLabel.Caption := 'Ready: the installed APK supports this mode.'
    else
      FCheckLabel.Caption := string.Join(sLineBreak, LProblems);
  except
    on E: Exception do
      FCheckLabel.Caption := E.Message;
  end;
end;

function TSetupDialog.Execute(out AResult: TSetupResult): Boolean;
var
  LDevices: TDeviceInfos;
  I: Integer;
begin
  AResult := Default(TSetupResult);
  try
    LDevices := FClient.Devices;
  except
    on E: Exception do
    begin
      MessageDlg('Cannot list devices: ' + E.Message, mtError, [mbOK], 0);
      Exit(False);
    end;
  end;
  for I := 0 to High(LDevices) do
    FDevices.Items.Add(LDevices[I].Serial);
  if FDevices.Items.Count > 0 then
    FDevices.ItemIndex := 0;

  Result := ShowModal = mrOk;
  if not Result then
    Exit;
  AResult.DeviceSerial := FDevices.Text;
  AResult.Package := Trim(FPackage.Text);
  AResult.Mode := FMode.Text;
  if FMode.Text = 'instrumenting' then
  begin
    AResult.Engine := FEngine.Text;
    AResult.Callspec := Trim(FCallspec.Text);
  end;
  AResult.DurationSeconds := StrToIntDef(Trim(FDuration.Text), 0);
  AResult.SymbolsDir := Trim(FSymbols.Text);
end;

end.
