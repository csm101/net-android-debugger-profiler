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
  cxLabel, cxButtons, cxDropDownEdit, cxTextEdit, cxMaskEdit,
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
    /// Assemblies to weave; empty lets the service infer them from the callspec.
    Assemblies: TArray<string>;
  end;

  TSetupDialog = class(TForm)
  private
    FClient: TControlClient;
    FDevices: TcxComboBox;
    FPackage: TcxTextEdit;
    FMode: TcxComboBox;
    FEngine: TcxComboBox;
    FCallspec: TcxTextEdit;
    FAssemblies: TcxTextEdit;
    FDuration: TcxTextEdit;
    FSymbols: TcxTextEdit;
    FCheckLabel: TcxLabel;
    FOk: TcxButton;
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

  function Label_(const AText: string; ATop: Integer): TcxLabel;
  begin
    Result := TcxLabel.Create(Self);
    Result.Transparent := True;
    Result.Parent := Self;
    Result.Left := 16;
    Result.Top := ATop + 4;
    Result.Caption := AText;
  end;

var
  LCheck, LCancel: TcxButton;
begin
  Caption := 'New profiling session';
  BorderStyle := bsDialog;
  Position := poOwnerFormCenter;
  ClientWidth := 560;
  ClientHeight := 362;

  Label_('Device', 16);
  FDevices := TcxComboBox.Create(Self);
  FDevices.Parent := Self;
  FDevices.SetBounds(140, 16, 400, 24);
  FDevices.Properties.DropDownListStyle := lsFixedList;

  Label_('Package', 48);
  FPackage := TcxTextEdit.Create(Self);
  FPackage.Parent := Self;
  FPackage.SetBounds(140, 48, 400, 24);
  FPackage.TextHint := 'com.example.app';

  Label_('Mode', 80);
  FMode := TcxComboBox.Create(Self);
  FMode.Parent := Self;
  FMode.SetBounds(140, 80, 200, 24);
  FMode.Properties.DropDownListStyle := lsFixedList;
  FMode.Properties.Items.Add('sampling');
  FMode.Properties.Items.Add('instrumenting');
  FMode.Properties.Items.Add('heap');
  FMode.ItemIndex := 0;
  FMode.Properties.OnChange := ModeChanged;

  Label_('Engine', 112);
  FEngine := TcxComboBox.Create(Self);
  FEngine.Parent := Self;
  FEngine.SetBounds(140, 112, 200, 24);
  FEngine.Properties.DropDownListStyle := lsFixedList;
  FEngine.Properties.Items.Add('weaver');      // the engine that supports live control
  FEngine.Properties.Items.Add('provider');
  FEngine.ItemIndex := 0;

  Label_('Callspec', 144);
  FCallspec := TcxTextEdit.Create(Self);
  FCallspec.Parent := Self;
  FCallspec.SetBounds(140, 144, 400, 24);
  FCallspec.TextHint := 'N:My.App.Namespace or T:My.App.Type';

  Label_('Assemblies', 176);
  FAssemblies := TcxTextEdit.Create(Self);
  FAssemblies.Parent := Self;
  FAssemblies.SetBounds(140, 176, 400, 24);
  FAssemblies.TextHint := 'leave empty to infer from the callspec; otherwise MyApp, MyApp.Core';

  Label_('Duration (s)', 208);
  FDuration := TcxTextEdit.Create(Self);
  FDuration.Parent := Self;
  FDuration.SetBounds(140, 208, 80, 24);
  FDuration.Text := '0';
  with Label_('0 = until you stop it', 208) do
    Left := 232;

  Label_('Build output', 240);
  FSymbols := TcxTextEdit.Create(Self);
  FSymbols.Parent := Self;
  FSymbols.SetBounds(140, 240, 400, 24);
  FSymbols.TextHint := 'bin\Debug\net9.0-android35.0 - the pdbs, so results carry source locations';

  FCheckLabel := TcxLabel.Create(Self);
  FCheckLabel.Transparent := True;
  FCheckLabel.Parent := Self;
  FCheckLabel.SetBounds(16, 272, 528, 32);
  FCheckLabel.Properties.WordWrap := True;
  FCheckLabel.Caption := '';

  LCheck := TcxButton.Create(Self);
  LCheck.Parent := Self;
  LCheck.SetBounds(16, 320, 120, 28);
  LCheck.Caption := 'Check app';
  LCheck.OnClick := CheckClick;

  FOk := TcxButton.Create(Self);
  FOk.Parent := Self;
  FOk.SetBounds(360, 320, 90, 28);
  FOk.Caption := 'Start';
  FOk.ModalResult := mrOk;
  FOk.Default := True;

  LCancel := TcxButton.Create(Self);
  LCancel.Parent := Self;
  LCancel.SetBounds(456, 320, 90, 28);
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
  FAssemblies.Enabled := LInstrumenting;
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
    FDevices.Properties.Items.Add(LDevices[I].Serial);
  if FDevices.Properties.Items.Count > 0 then
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
    // The service infers the assembly from the callspec's first two dotted segments,
    // which is wrong whenever the namespace is deeper than the assembly name
    // (N:TestTarget.Workloads lives in TestTarget.dll). Naming them settles it.
    AResult.Assemblies := Trim(FAssemblies.Text).Split([','], TStringSplitOptions.ExcludeEmpty);
    for I := 0 to High(AResult.Assemblies) do
      AResult.Assemblies[I] := Trim(AResult.Assemblies[I]);
    AResult.Callspec := Trim(FCallspec.Text);
  end;
  AResult.DurationSeconds := StrToIntDef(Trim(FDuration.Text), 0);
  AResult.SymbolsDir := Trim(FSymbols.Text);
end;

end.
