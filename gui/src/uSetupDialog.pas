unit uSetupDialog;

{
  What to profile, before a run: AQTime's Setup panel, reduced to the choices this
  profiler actually has.

  It starts from the sources. Point it at a solution, a project or a folder and the
  service reads the project files: which projects are Android applications, what package
  they install, where their build output is, and which assemblies are the application's
  own. Everything the session needs is filled in from that, including the namespaces and
  types the callspec can name - a profiler that is handed the sources should not ask the
  user to retype what the .csproj already says.

  The two things that used to send the user back to a command prompt are here as well:
  building and installing the app with the properties a session needs, and the
  prerequisite check on the APK that is actually installed.
}

interface

uses
  System.SysUtils, System.Classes,
  Vcl.Controls, Vcl.Forms, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.Dialogs, Vcl.FileCtrl,
  System.Generics.Collections,
  cxLabel, cxButtons, cxCheckBox, cxDropDownEdit, cxTextEdit, cxMaskEdit,
  uControlClient;

type
  /// What the dialog produces is exactly what the service is asked for: one shape, so a
  /// field added to a session does not have to be copied from a record into arguments.
  TSetupResult = TSessionRequest;

  TSetupDialog = class(TForm)
  private
    FClient: TControlClient;
    FSource: TcxTextEdit;
    FConfiguration: TcxComboBox;
    FProjects: TcxComboBox;
    FScanning: Boolean;
    FProjectHint: TcxLabel;
    FDevices: TcxComboBox;
    FPackage: TcxTextEdit;
    FProfiler: TcxComboBox;
    FProfilerInfo: TcxLabel;
    FNoFastDeployment: TcxCheckBox;
    FBuildWeaving: TcxCheckBox;
    FValidation: TcxLabel;
    FCallspec: TcxComboBox;
    FAssemblies: TcxTextEdit;
    FDuration: TcxTextEdit;
    FSymbols: TcxTextEdit;
    FName: TcxTextEdit;
    FStartPaused: TcxCheckBox;
    FFolder: TcxComboBox;
    FCheckLabel: TcxLabel;
    FBuild: TcxButton;
    FClearDeployed: TcxCheckBox;
    FOk: TcxButton;
    /// The projects the last scan found, in the order the combo lists them.
    FFound: TArray<TAppProject>;
    /// The devices, in the order the combo lists them: the list shows their names, the
    /// session needs their serials.
    FDevices_: TDeviceInfos;
    /// The callspecs the selected project offers, in the order the combo lists them.
    FCandidates: TArray<TCallspecCandidate>;
    /// The namespaces and types asked for per-line detail, kept until the engine collects it.
    FLineCallspec: string;
    /// Which project's assemblies FCandidates were read from: reading them costs real
    /// time on a product with thirty of them, so it happens once, and only when asked.
    FCandidatesOf: string;
    /// Fires once after the window is up: the first scan must not happen before it.
    FDeferred: TTimer;
    FDeferredSolution: string;
    FDeferredProject: string;
    /// Filled in from an existing session: the dialog then answers with these instead of
    /// with what the last session happened to leave in the settings.
    FPrefill: TSessionRequest;
    FPrefilled: Boolean;
    procedure Build_;
    procedure ProfilerChanged(Sender: TObject);
    procedure OptionChanged(Sender: TObject);
    procedure Validate;
    procedure CheckClick(Sender: TObject);
    procedure BrowseFileClick(Sender: TObject);
    procedure BrowseFolderClick(Sender: TObject);
    procedure BrowseSessionFolderClick(Sender: TObject);
    /// Where this session is to be kept; empty means the standard sessions folder.
    function ChosenSessionFolder: string;
    procedure ScanClick(Sender: TObject);
    procedure ProjectChanged(Sender: TObject);
    procedure CallspecChanged(Sender: TObject);
    procedure CallspecPopup(Sender: TObject);
    procedure ChooseCallspecClick(Sender: TObject);
    procedure DeferredScan(Sender: TObject);
    procedure BuildClick(Sender: TObject);
    procedure Scan(const APreferredProject: string);
    procedure ApplyPrefill;
    function SelectedProject(out AProject: TAppProject): Boolean;
    function SelectedSerial: string;
    function SelectedMode: string;
    function SelectedEngine: string;
    function WeaveMapPath: string;
    procedure EnsureAssembliesForCallspec(out AAdded: string);
    function ChosenAssemblies: TArray<string>;
    procedure LoadCandidates(const AProject: TAppProject);
  public
    constructor Create(AOwner: TComponent; AClient: TControlClient); reintroduce;
    function Execute(out AResult: TSetupResult): Boolean;
    /// Open on a session that already exists: every field as that session was started
    /// with. Running the same measurement again is the commonest thing anybody does with
    /// a profiler, and it must not cost filling this form a second time.
    procedure PrefillFrom(const ARequest: TSessionRequest);
  end;

implementation

uses
  System.UITypes, System.IOUtils, System.StrUtils,
  uJobDialog, uCallspecDialog, uSettings, uTheme, uSessionSpec;

const
  CLabelLeft = 16;
  CFieldLeft = 140;
  CFieldRight = 606;

constructor TSetupDialog.Create(AOwner: TComponent; AClient: TControlClient);
begin
  inherited CreateNew(AOwner);
  FClient := AClient;
  Build_;
end;

procedure TSetupDialog.Build_;

  function Label_(const AText: string; ATop: Integer): TcxLabel;
  begin
    Result := TcxLabel.Create(Self);
    Result.Transparent := True;
    Result.Parent := Self;
    Result.Left := CLabelLeft;
    Result.Top := ATop + 4;
    Result.Caption := AText;
  end;

  function Button(const ACaption: string; ALeft, ATop, AWidth: Integer; AOnClick: TNotifyEvent): TcxButton;
  begin
    Result := TcxButton.Create(Self);
    Result.Parent := Self;
    Result.SetBounds(ALeft, ATop, AWidth, 24);
    Result.Caption := ACaption;
    Result.OnClick := AOnClick;
    // Anything sitting against the right edge stays there.
    if ALeft >= 400 then
      Result.Anchors := [akTop, akRight];
  end;

  function Edit(ATop, ALeft, AWidth: Integer; const AHint: string): TcxTextEdit;
  begin
    Result := TcxTextEdit.Create(Self);
    Result.Parent := Self;
    Result.SetBounds(ALeft, ATop, AWidth, 24);
    Result.TextHint := AHint;
    // The wide fields hold paths and callspecs: they take the extra width.
    if AWidth >= 300 then
      Result.Anchors := [akLeft, akTop, akRight];
  end;

  function Combo(ATop, AWidth: Integer; AFixed: Boolean): TcxComboBox;
  begin
    Result := TcxComboBox.Create(Self);
    Result.Parent := Self;
    Result.SetBounds(CFieldLeft, ATop, AWidth, 24);
    if AFixed then
      Result.Properties.DropDownListStyle := lsFixedList;
    if AWidth >= 300 then
      Result.Anchors := [akLeft, akTop, akRight];
  end;

var
  LCancel: TcxButton;
begin
  Caption := 'New profiling session';
  // Resizable, like every window here that holds more than two lines: paths and callspecs
  // are long, and a fixed width means reading them through a keyhole. The rows keep their
  // distance from the top, the answer and the buttons from the bottom, and everything wide
  // grows with the form.
  BorderStyle := bsSizeable;
  Position := poOwnerFormCenter;
  ClientWidth := 620;
  ClientHeight := 712;
  Constraints.MinWidth := 636;
  Constraints.MinHeight := 704;

  // ---- the sources
  Label_('Solution', 16);
  FSource := Edit(16, CFieldLeft, 330, 'a .sln, a .csproj, or a folder to search');
  Button('File...', 478, 16, 60, BrowseFileClick);
  Button('Folder...', 546, 16, 60, BrowseFolderClick);

  Label_('Configuration', 48);
  FConfiguration := Combo(48, 120, False);
  FConfiguration.Properties.Items.Add('Debug');
  FConfiguration.Properties.Items.Add('Release');
  FConfiguration.Properties.Items.Add('Profiling');
  FConfiguration.Text := 'Debug';
  // The configuration decides which build output the session reads, so changing it means
  // reading the projects again: that is what Scan is for.
  Button('Scan', 270, 48, 80, ScanClick);

  Label_('Project', 80);
  FProjects := Combo(80, 330, True);
  FProjects.Properties.OnChange := ProjectChanged;
  FBuild := Button('Build && install', 478, 80, 128, BuildClick);
  FBuild.Enabled := False;

  // An app that is changing deployment mode keeps its old assemblies in
  // files/.__override__, and the runtime prefers whatever is there: stale ones can stop
  // it from starting. The build redeploys them, so clearing costs only that.
  FClearDeployed := TcxCheckBox.Create(Self);
  FClearDeployed.Parent := Self;
  FClearDeployed.SetBounds(478, 108, 128, 20);
  FClearDeployed.Anchors := [akTop, akRight];
  FClearDeployed.Transparent := True;
  FClearDeployed.Properties.MultiLine := True;
  FClearDeployed.Caption := 'Clear deployed assemblies first';
  FClearDeployed.Height := 34;

  // AutoSize is on by default, and a wrapped label that sizes itself grows straight over
  // the fields below it: the hint has a fixed area, and the whole text lives in its tooltip.
  FProjectHint := TcxLabel.Create(Self);
  FProjectHint.Transparent := True;
  FProjectHint.Parent := Self;
  FProjectHint.AutoSize := False;
  FProjectHint.SetBounds(CFieldLeft, 110, 330, 48);
  FProjectHint.Properties.WordWrap := True;
  // The hint is text, not a caption: an & in it names a property, not an accelerator.
  FProjectHint.Properties.ShowAccelChar := False;
  FProjectHint.ShowHint := True;
  FProjectHint.Caption := '';

  // ---- what the build must produce
  // Two properties of the *build*, next to the button that runs it. They are choices and
  // not assumptions: an app can have a reason to keep its assemblies inside the APK, and
  // then the only way to instrument it is to weave it while it is built.
  FNoFastDeployment := TcxCheckBox.Create(Self);
  FNoFastDeployment.Parent := Self;
  FNoFastDeployment.SetBounds(CLabelLeft, 164, 330, 20);
  FNoFastDeployment.Transparent := True;
  FNoFastDeployment.Caption := 'Keep assemblies inside the APK (no fast deployment)';
  FNoFastDeployment.Properties.OnChange := OptionChanged;

  FBuildWeaving := TcxCheckBox.Create(Self);
  FBuildWeaving.Parent := Self;
  FBuildWeaving.SetBounds(360, 164, 246, 20);
  FBuildWeaving.Anchors := [akTop, akRight];
  FBuildWeaving.Transparent := True;
  FBuildWeaving.Caption := 'Instrument during the build';
  FBuildWeaving.Properties.OnChange := OptionChanged;

  // ---- the session
  Label_('Device', 192);
  FDevices := Combo(192, CFieldRight - CFieldLeft, True);
  FDevices.Properties.OnChange := OptionChanged;

  Label_('Package', 224);
  FPackage := Edit(224, CFieldLeft, CFieldRight - CFieldLeft, 'com.example.app');
  FPackage.Properties.OnChange := OptionChanged;

  // One list of the profilers that exist, rather than a mode and an engine to combine:
  // the two together were a puzzle ("where is sampling?"), and what changes between them
  // deserves a sentence rather than a word - which is what the text underneath is for.
  Label_('Profiler', 256);
  FProfiler := Combo(256, CFieldRight - CFieldLeft, True);
  FProfiler.Properties.Items.Add('CPU sampling');
  FProfiler.Properties.Items.Add('Instrumenting - call tree kept in the app');
  FProfiler.Properties.Items.Add('Instrumenting - one event per call');
  FProfiler.Properties.Items.Add('Instrumenting - runtime provider, no rewriting');
  FProfiler.Properties.Items.Add('Instrumenting - let the profiler choose');
  FProfiler.Properties.Items.Add('Memory - heap snapshot');
  FProfiler.ItemIndex := 0;
  FProfiler.Properties.OnChange := ProfilerChanged;

  FProfilerInfo := TcxLabel.Create(Self);
  FProfilerInfo.Transparent := True;
  FProfilerInfo.Parent := Self;
  FProfilerInfo.AutoSize := False;
  FProfilerInfo.SetBounds(CFieldLeft, 284, CFieldRight - CFieldLeft, 76);
  FProfilerInfo.Anchors := [akLeft, akTop, akRight];
  FProfilerInfo.Properties.WordWrap := True;
  FProfilerInfo.Properties.ShowAccelChar := False;
  FProfilerInfo.ShowHint := True;

  Label_('Callspec', 364);
  FCallspec := Combo(364, CFieldRight - CFieldLeft - 90, False);
  Button('Choose...', CFieldRight - 84, 364, 84, ChooseCallspecClick);
  FCallspec.Properties.DropDownRows := 20;
  FCallspec.Properties.OnChange := CallspecChanged;
  FCallspec.Properties.OnInitPopup := CallspecPopup;
  FCallspec.TextHint := 'N:My.App.Namespace or T:My.App.Type - the list holds what the app declares';

  Label_('Assemblies', 396);
  FAssemblies := Edit(396, CFieldLeft, CFieldRight - CFieldLeft,
    'leave empty to infer from the callspec; otherwise MyApp, MyApp.Core');

  Label_('Duration (s)', 428);
  FDuration := Edit(428, CFieldLeft, 80, '');
  FDuration.Text := '0';
  with Label_('0 = until you stop it', 428) do
    Left := 232;

  // What needs measuring is rarely the startup: it is what happens when somebody presses
  // a certain button. Started this way the app runs unmeasured until Record is pressed,
  // and the results hold that and nothing else.
  FStartPaused := TcxCheckBox.Create(Self);
  FStartPaused.Parent := Self;
  FStartPaused.SetBounds(360, 428, 246, 20);
  FStartPaused.Anchors := [akTop, akRight];
  FStartPaused.Transparent := True;
  FStartPaused.Caption := 'Start recording only when I say';
  FStartPaused.Hint := 'The app starts and runs normally; nothing is measured until you press Record. '
    + 'Drive it to what you want to look at first.';
  FStartPaused.ShowHint := True;

  Label_('Build output', 460);
  FSymbols := Edit(460, CFieldLeft, CFieldRight - CFieldLeft,
    'bin\Debug\net9.0-android35.0 - the pdbs, so results carry source locations');
  FSymbols.Properties.OnChange := OptionChanged;

  // ---- the session as something somebody keeps
  // A recording only ever called 20260908-174233-com.acme.app-sampling is a recording
  // nobody finds again: a name, and a folder for the sessions that belong beside the
  // product they measure rather than in the profiler's own pile.
  Label_('Name', 492);
  FName := Edit(492, CFieldLeft, CFieldRight - CFieldLeft,
    'what this run is about - "startup after the cache change"; optional');

  Label_('Keep it in', 524);
  FFolder := Combo(524, CFieldRight - CFieldLeft - 90, False);
  FFolder.TextHint := 'the profiler''s own sessions folder';
  for var LFolder in GSettings.SessionFolders do
    FFolder.Properties.Items.Add(LFolder);
  FFolder.Text := GSettings.LastSessionFolder;
  Button('Folder...', CFieldRight - 84, 524, 84, BrowseSessionFolderClick);

  // Why Start is off, where the eye goes when it is: red, and never a surprise at the
  // moment of clicking.
  FValidation := TcxLabel.Create(Self);
  FValidation.Transparent := True;
  FValidation.Parent := Self;
  FValidation.AutoSize := False;
  FValidation.SetBounds(CLabelLeft, 556, CFieldRight - CLabelLeft, 60);
  FValidation.Anchors := [akLeft, akRight, akBottom];
  FValidation.Properties.WordWrap := True;
  FValidation.Properties.ShowAccelChar := False;
  FValidation.Style.TextColor := ThemeColors.Warning;
  FValidation.Visible := False;

  FCheckLabel := TcxLabel.Create(Self);
  FCheckLabel.Transparent := True;
  FCheckLabel.Parent := Self;
  FCheckLabel.SetBounds(CLabelLeft, 620, CFieldRight - CLabelLeft, 44);
  FCheckLabel.Anchors := [akLeft, akRight, akBottom];
  FCheckLabel.AutoSize := False;
  FCheckLabel.Properties.WordWrap := True;
  FCheckLabel.Properties.ShowAccelChar := False;
  FCheckLabel.ShowHint := True;
  FCheckLabel.Caption := '';

  with Button('Check app', CLabelLeft, 670, 120, CheckClick) do
  begin
    Height := 28;
    Anchors := [akLeft, akBottom];
  end;

  FOk := TcxButton.Create(Self);
  FOk.Parent := Self;
  FOk.SetBounds(416, 670, 90, 28);
  FOk.Anchors := [akRight, akBottom];
  FOk.Caption := 'Start';
  FOk.ModalResult := mrOk;
  FOk.Default := True;

  LCancel := TcxButton.Create(Self);
  LCancel.Parent := Self;
  LCancel.SetBounds(516, 670, 90, 28);
  LCancel.Anchors := [akRight, akBottom];
  LCancel.Caption := 'Cancel';
  LCancel.ModalResult := mrCancel;
  LCancel.Cancel := True;

  ProfilerChanged(nil);
end;

// ------------------------------------------------------------------ the sources

procedure TSetupDialog.BrowseFileClick(Sender: TObject);
var
  LDialog: TOpenDialog;
begin
  LDialog := TOpenDialog.Create(Self);
  try
    LDialog.Filter := 'Solutions and projects|*.sln;*.slnx;*.csproj|All files|*.*';
    LDialog.Options := LDialog.Options + [ofFileMustExist];
    if FSource.Text <> '' then
      LDialog.InitialDir := TPath.GetDirectoryName(FSource.Text);
    if not LDialog.Execute then
      Exit;
    FSource.Text := LDialog.FileName;
    Scan('');
  finally
    LDialog.Free;
  end;
end;

procedure TSetupDialog.BrowseFolderClick(Sender: TObject);
var
  LFolder: string;
begin
  LFolder := FSource.Text;
  if not SelectDirectory('Folder to search for Android applications', '', LFolder) then
    Exit;
  FSource.Text := LFolder;
  Scan('');
end;

procedure TSetupDialog.BrowseSessionFolderClick(Sender: TObject);
var
  LFolder: string;
begin
  LFolder := ChosenSessionFolder;
  if not SelectDirectory('Folder to keep this session in', '', LFolder) then
    Exit;
  FFolder.Text := LFolder;
end;

function TSetupDialog.ChosenSessionFolder: string;
begin
  Result := Trim(FFolder.Text);
end;

procedure TSetupDialog.ScanClick(Sender: TObject);
begin
  Scan('');
end;

procedure TSetupDialog.Scan(const APreferredProject: string);
var
  I, LIndex: Integer;
  LCaption: string;
begin
  FScanning := True;
  try
    FProjects.Properties.Items.Clear;
  finally
    FScanning := False;
  end;
  FFound := nil;
  FBuild.Enabled := False;
  FProjectHint.Caption := '';
  if Trim(FSource.Text) = '' then
    Exit;

  Screen.Cursor := crHourGlass;
  try
    try
      FFound := FClient.Projects(Trim(FSource.Text), FConfiguration.Text);
    except
      on E: Exception do
      begin
        FProjectHint.Caption := E.Message;
        Exit;
      end;
    end;
  finally
    Screen.Cursor := crDefault;
  end;

  LIndex := -1;
  for I := 0 to High(FFound) do
  begin
    LCaption := FFound[I].Name;
    if FFound[I].ApplicationId <> '' then
      LCaption := LCaption + '  -  ' + FFound[I].ApplicationId;
    FProjects.Properties.Items.Add(LCaption);
    if SameText(FFound[I].ProjectPath, APreferredProject) then
      LIndex := I;
  end;

  if Length(FFound) = 0 then
  begin
    FProjectHint.Caption := 'No .NET for Android application found there. A library that targets Android is not one: '
      + 'an application declares an ApplicationId, or is an Exe.';
    Exit;
  end;
  if LIndex < 0 then
    LIndex := 0;
  FProjects.ItemIndex := LIndex;
  ProjectChanged(nil);
  if GSettings.LastCallspec <> '' then
    FCallspec.Text := GSettings.LastCallspec;
  if GSettings.LastAssemblies <> '' then
    FAssemblies.Text := GSettings.LastAssemblies;
  OptionChanged(nil);
end;

/// What the session calls the profiler the user picked. The list shows names a person
/// recognises; the engine takes these three words.
function TSetupDialog.SelectedMode: string;
begin
  case FProfiler.ItemIndex of
    1, 2, 3, 4: Result := 'instrumenting';
    5: Result := 'heap';
  else
    Result := 'sampling';
  end;
end;

/// Which of the three instrumenting engines the chosen profiler is. Empty outside
/// instrumenting, where the engine means nothing.
function TSetupDialog.SelectedEngine: string;
begin
  case FProfiler.ItemIndex of
    1: Result := 'weaver-tree';
    2: Result := 'weaver';
    3: Result := 'provider';
    4: Result := 'auto';
  else
    Result := '';
  end;
end;

/// Where the build-time weave leaves its map, which is what the session reads instead of
/// touching the device. The targets file writes it next to the build output.
function TSetupDialog.WeaveMapPath: string;
begin
  Result := '';
  if FBuildWeaving.Checked and (Trim(FSymbols.Text) <> '') then
    Result := TPath.Combine(Trim(FSymbols.Text), 'nap-weave.map');
end;

/// The assemblies to rewrite, as the user named them. Both engines take this list: the
/// on-device weaver rewrites them where they are deployed, and the build weaves them in
/// the output folder.
function TSetupDialog.ChosenAssemblies: TArray<string>;
var
  I: Integer;
begin
  Result := Trim(FAssemblies.Text).Split([','], TStringSplitOptions.ExcludeEmpty);
  for I := 0 to High(Result) do
    Result[I] := Trim(Result[I]);
end;

/// Whether the chosen callspec names something that lives outside the application's own
/// assembly. Build-time weaving rewrites only that one, so a callspec pointing at a
/// referenced library would produce a session that collects almost nothing and says
/// nothing about why - which is exactly what happened on the reference application.
/// Whether a callspec entry named ANamespaceOrType covers the candidate ACallspec: the
/// same prefix rule the engine's filter uses, so the dialog and the weaver agree on what
/// a choice reaches.
function CallspecCovers(const ANamespaceOrType, ACandidate: string): Boolean;
var
  LName: string;
begin
  LName := ACandidate;
  if (Length(LName) > 2) and (LName[2] = ':') then
    LName := Copy(LName, 3, MaxInt);
  Result := SameText(LName, ANamespaceOrType)
    or LName.StartsWith(ANamespaceOrType + '.', True)
    or LName.StartsWith(ANamespaceOrType + '+', True);
end;

/// The assemblies a callspec reaches, put into the field instead of demanded from whoever
/// is filling the dialog. The build weaves the application's own assembly and the ones
/// named in Assemblies; which candidate came out of which assembly is something this
/// dialog already knows, so asking for it back was asking somebody to guess what was on
/// the screen. Names are added, never removed: one that is not reached weaves nothing.
procedure TSetupDialog.EnsureAssembliesForCallspec(out AAdded: string);
var
  LProject: TAppProject;
  LPart, LOwn, LName, LPrefix, LNamed, LAssembly: string;
  LChosen: TArray<string>;
  LMissing: TStringList;
  I: Integer;
  LKnown: Boolean;
begin
  AAdded := '';
  if not SelectedProject(LProject) or (Length(FCandidates) = 0) then
    Exit;
  LOwn := LProject.AssemblyName;
  LChosen := ChosenAssemblies;
  LMissing := TStringList.Create;
  try
    LMissing.Sorted := True;
    LMissing.Duplicates := dupIgnore;
    for LPart in Trim(FCallspec.Text).Split([','], TStringSplitOptions.ExcludeEmpty) do
    begin
      LPrefix := Trim(LPart);
      if LPrefix.StartsWith('-') then
        Continue;
      LName := LPrefix;
      if (Length(LName) > 2) and (LName[2] = ':') then
        LName := Copy(LName, 3, MaxInt);
      for I := 0 to High(FCandidates) do
      begin
        if (FCandidates[I].Assembly = '') or not CallspecCovers(LName, FCandidates[I].Callspec) then
          Continue;
        // A candidate can live in more than one assembly, and says so comma separated.
        for LAssembly in FCandidates[I].Assembly.Split([','], TStringSplitOptions.ExcludeEmpty) do
        begin
          if SameText(Trim(LAssembly), LOwn) then
            Continue;
          LKnown := False;
          for LNamed in LChosen do
            if SameText(LNamed, Trim(LAssembly)) then
            begin
              LKnown := True;
              Break;
            end;
          if not LKnown then
            LMissing.Add(Trim(LAssembly));
        end;
      end;
    end;
    if LMissing.Count = 0 then
      Exit;
    if Trim(FAssemblies.Text) = '' then
      FAssemblies.Text := string.Join(', ', LMissing.ToStringArray)
    else
      FAssemblies.Text := Trim(FAssemblies.Text) + ', ' + string.Join(', ', LMissing.ToStringArray);
    AAdded := string.Join(', ', LMissing.ToStringArray);
  finally
    LMissing.Free;
  end;
end;
procedure TSetupDialog.DeferredScan(Sender: TObject);
var
  I: Integer;
begin
  FDeferred.Enabled := False;
  if FDeferredSolution <> '' then
  begin
    FSource.Text := FDeferredSolution;
    FDeferredSolution := '';
    Scan(FDeferredProject);
  end;
  // --dialog=callspec opens the picker straight away, the same way the other dialogs are
  // exercised without a hand on the mouse.
  for I := 1 to ParamCount do
    if SameText(ParamStr(I), '--dialog=callspec') then
    begin
      ChooseCallspecClick(nil);
      Break;
    end;
end;

function TSetupDialog.SelectedSerial: string;
begin
  if (FDevices.ItemIndex >= 0) and (FDevices.ItemIndex <= High(FDevices_)) then
    Result := FDevices_[FDevices.ItemIndex].Serial
  else
    Result := '';
end;

function TSetupDialog.SelectedProject(out AProject: TAppProject): Boolean;
begin
  Result := (FProjects.ItemIndex >= 0) and (FProjects.ItemIndex <= High(FFound));
  if Result then
    AProject := FFound[FProjects.ItemIndex]
  else
    AProject := Default(TAppProject);
end;

procedure TSetupDialog.ProjectChanged(Sender: TObject);
var
  LProject: TAppProject;
  LHints: TArray<string>;
begin
  if FScanning or not SelectedProject(LProject) then
    Exit;

  FBuild.Enabled := True;
  if LProject.ApplicationId <> '' then
    FPackage.Text := LProject.ApplicationId;
  FSymbols.Text := LProject.OutputDir;
  // The app's own assembly as a starting point. Picking a callspec narrows it to the
  // assemblies that actually hold it: weaving a whole product - the reference application has thirty-odd
  // assemblies - would cost a pull, a rewrite and a push for each of them.
  FAssemblies.Text := LProject.AssemblyName;

  // Say what this build is missing for profiling, and what Build & install would add.
  // The APK that is actually installed is a different question: that is Check app.
  SetLength(LHints, 0);
  if not LProject.OutputExists then
    LHints := LHints + [Format('Not built in %s yet.', [LProject.Configuration])];
  if LProject.EnableDiagnostics <> pfTrue then
    LHints := LHints + ['EnableDiagnostics not in the project: Build & install adds it.'];
  if LProject.EmbedAssembliesIntoApk = pfTrue then
  begin
    LHints := LHints + ['Assemblies are embedded in the APK: Build & install switches this build to fast deployment, '
      + 'which the weaver engines need.'];
    // Changing deployment mode is exactly the case the checkbox exists for, so it is on
    // by default - but never against a choice the user has already made and saved.
    if not GSettings.LastClearDeployed and not FNoFastDeployment.Checked then
      FClearDeployed.Checked := True;
  end;
  FProjectHint.Caption := string.Join(' ', LHints);
  FProjectHint.Hint := FProjectHint.Caption;

  // Not read here: see LoadCandidates. Dropping the list down is what asks for them.
  FCallspec.Properties.Items.Clear;
  FCandidates := nil;
  FCandidatesOf := '';
end;

/// What the app declares, read from its assemblies with Cecil. On a product with thirty
/// of them that is seconds of work, so it happens when the list is dropped down and once
/// per project - never while the dialog is being opened.
procedure TSetupDialog.LoadCandidates(const AProject: TAppProject);
var
  I: Integer;
begin
  if (FCandidatesOf = AProject.ProjectPath) or not AProject.OutputExists then
    Exit;
  Screen.Cursor := crHourGlass;
  try
    try
      FCandidates := FClient.Candidates(AProject.OutputDir, AProject.Assemblies);
    except
      on E: Exception do
        // Offering no candidates is a lesser problem than breaking the dialog: the
        // callspec can still be typed.
        Exit;
    end;
  finally
    Screen.Cursor := crDefault;
  end;
  FCandidatesOf := AProject.ProjectPath;
  FCallspec.Properties.Items.BeginUpdate;
  try
    FCallspec.Properties.Items.Clear;
    for I := 0 to High(FCandidates) do
      FCallspec.Properties.Items.Add(FCandidates[I].Callspec);
  finally
    FCallspec.Properties.Items.EndUpdate;
  end;
end;

/// The picker: a tree of what the app declares, with a box per node. A callspec is a set
/// - several namespaces, minus a type, plus one from elsewhere - and a combo cannot say
/// that. Reading the assemblies happens here too, since this is the moment it is asked for.
procedure TSetupDialog.ChooseCallspecClick(Sender: TObject);
var
  LProject: TAppProject;
  LSelection: TCallspecSelection;
begin
  if SelectedProject(LProject) then
    LoadCandidates(LProject);
  if Length(FCandidates) = 0 then
  begin
    FCheckLabel.Caption := 'Nothing to choose from yet: the app has to be built in this '
      + 'configuration before the profiler can read what it declares.';
    Exit;
  end;
  LSelection.Callspec := Trim(FCallspec.Text);
  LSelection.LineCallspec := FLineCallspec;
  if not TCallspecDialog.Execute(Self, FCandidates, LSelection) then
    Exit;
  FCallspec.Text := LSelection.Callspec;
  FLineCallspec := LSelection.LineCallspec;
  CallspecChanged(nil);
  Validate;
end;

/// Dropping the callspec list down is the moment the app's assemblies are worth reading.
procedure TSetupDialog.CallspecPopup(Sender: TObject);
var
  LProject: TAppProject;
begin
  if SelectedProject(LProject) then
    LoadCandidates(LProject);
  Validate;
end;

/// Choosing a callspec settles which assemblies have to be woven, because the candidates
/// came out of them. Validate adds what is reached and says what it added, for a callspec
/// picked from the list and for one typed by hand alike.
procedure TSetupDialog.CallspecChanged(Sender: TObject);
begin
  Validate;
end;

procedure TSetupDialog.BuildClick(Sender: TObject);
var
  LProject: TAppProject;
  LJob: TJobStatus;
  LSerial: string;
begin
  if not SelectedProject(LProject) then
    Exit;
  LSerial := SelectedSerial;
  try
    LJob := FClient.StartBuild(LProject.ProjectPath, FConfiguration.Text, LSerial, True,
      not FNoFastDeployment.Checked, True, FClearDeployed.Checked, Trim(FPackage.Text),
      FBuildWeaving.Checked, Trim(FCallspec.Text), ChosenAssemblies);
  except
    on E: Exception do
    begin
      FCheckLabel.Caption := E.Message;
      Exit;
    end;
  end;

  if not TJobDialog.Run(Self, FClient, LJob, 'Build and install',
    Format('%s  ->  %s', [LProject.Name, LSerial])) then
    Exit;

  // A successful build changes what the project offers (its output now exists) and what
  // the device holds, so both are read again.
  Scan(LProject.ProjectPath);
  CheckClick(nil);
  Validate;
end;

// ------------------------------------------------------------------ the session

/// What the chosen profiler does, in the terms that matter when choosing it: what it
/// modifies, whether the app restarts, what it costs, and what it cannot tell you.
procedure TSetupDialog.ProfilerChanged(Sender: TObject);
const
  CInfo: array[0..5] of string = (
    'The runtime interrupts the app about a thousand times a second and records the stack. '
    + 'Nothing is modified, and it can attach to an app that is already running. Counts are '
    + 'samples of about a millisecond, and methods too short to be caught never appear.',

    'The profiler rewrites the IL of the methods the callspec names, and the app keeps a call '
    + 'tree in memory instead of writing an event per call: the cheapest way to instrument '
    + '(55-66 ns per call, measured). The app is restarted. The order of calls and the duration '
    + 'of each single call are not kept - only counts, totals, min and max.',

    'The same rewriting, but every enter and leave is written as a record: the order of the '
    + 'calls and every single duration survive, at about twice the cost per call (123-131 ns) '
    + 'and far larger files. The app is restarted.',

    'No rewriting: the Mono runtime instruments while it compiles, reading the callspec, and '
    + 'reports allocations with their exact sizes. It needs .NET 10 - on a .NET 9 app it '
    + 'crashes the runtime - and its results only resolve when the session ends, so Get Results, '
    + 'Pause and Clear stay unavailable.',

    'Looks at the app and decides: the in-app call tree when its assemblies can be rewritten '
    + 'where they are deployed, the runtime provider when they cannot. It writes which one it '
    + 'chose, and why, in the session log.',

    'Photographs the live heap by type - how many objects of each type are alive and how many '
    + 'bytes they hold. Nothing is instrumented; ask for two snapshots and you also get what '
    + 'grew between them.');
var
  LInstrumenting: Boolean;
begin
  if (FProfiler.ItemIndex >= 0) and (FProfiler.ItemIndex <= High(CInfo)) then
  begin
    FProfilerInfo.Caption := CInfo[FProfiler.ItemIndex];
    FProfilerInfo.Hint := FProfilerInfo.Caption;
  end;
  // A callspec is meaningless outside instrumenting, and mandatory inside it: profiling
  // every method makes the app unusable, so the engine refuses an empty one.
  LInstrumenting := SelectedMode = 'instrumenting';
  FCallspec.Enabled := LInstrumenting;
  FAssemblies.Enabled := LInstrumenting;
  Validate;
end;

procedure TSetupDialog.OptionChanged(Sender: TObject);
var
  LProject: TAppProject;
begin
  // Whether the callspec lives outside the app can only be answered against what the app
  // declares, and that is read on demand. Ticking the build-time weave is the moment it
  // becomes worth reading: otherwise the rule would quietly never fire, which is how the
  // the reference application session came to collect nothing.
  if FBuildWeaving.Checked and (Length(FCandidates) = 0) and SelectedProject(LProject) then
    LoadCandidates(LProject);
  Validate;
end;

/// The combinations that cannot work, said before Start rather than after it. Each rule
/// is a real constraint of the engine, not a matter of taste.
procedure TSetupDialog.Validate;
var
  LProject: TAppProject;
  LProblem, LAdded: string;
  LWeaves: Boolean;
begin
  LWeaves := (SelectedEngine = 'weaver') or (SelectedEngine = 'weaver-tree');
  LProblem := '';
  LAdded := '';
  // Whatever the callspec reaches has to be woven, and the dialog knows where it lives:
  // it fills the field in and says so, rather than refusing until somebody types it.
  if SelectedMode = 'instrumenting' then
    EnsureAssembliesForCallspec(LAdded);

  if SelectedSerial = '' then
    LProblem := 'Pick a device.'
  else if Trim(FPackage.Text) = '' then
    LProblem := 'The package is missing: pick a project, or type the application id.'
  else if (SelectedMode = 'instrumenting') and (Trim(FCallspec.Text) = '') then
    LProblem := 'Instrumenting needs a callspec: pick one from the list, or type N:My.Namespace '
      + 'or T:My.Type. Instrumenting everything makes the app unusable.'
  else if FBuildWeaving.Checked and (SelectedMode <> 'instrumenting') then
    LProblem := 'Instrumenting during the build only produces something an instrumenting session '
      + 'can read. Turn it off, or choose one of the instrumenting profilers.'
  else if FBuildWeaving.Checked and (SelectedEngine = 'provider') then
    LProblem := 'The runtime provider does not read a build-time weave: it instruments while the '
      + 'app runs. Choose one of the two rewriting profilers, or turn the build-time weaving off.'
  else if FBuildWeaving.Checked and (SelectedEngine = 'auto') then
    LProblem := 'Build-time weaving needs to know which profiler will read it: choose the call '
      + 'tree or the event-per-call profiler instead of letting it decide.'
  else if LWeaves and FNoFastDeployment.Checked and not FBuildWeaving.Checked then
    LProblem := 'A rewriting profiler changes the app assemblies where they are deployed, and '
      + 'with them inside the APK it cannot reach them. Either leave fast deployment on, or tick '
      + '"Instrument during the build".'
  else if FBuildWeaving.Checked and (WeaveMapPath <> '') and not TFile.Exists(WeaveMapPath) then
    LProblem := Format('The build has not woven this app yet: %s is missing. Press Build & install '
      + 'with "Instrument during the build" ticked - that map is what the session reads instead of '
      + 'touching the device.', [WeaveMapPath])
  else if FBuildWeaving.Checked and (WeaveMapPath = '') then
    LProblem := 'Instrumenting during the build writes its map next to the build output, so that '
      + 'field has to say where it is.'
  else if (SelectedEngine = 'provider') and SelectedProject(LProject)
    and LProject.TargetFramework.Contains('net9') then
    LProblem := Format('%s targets %s, and the runtime provider crashes a .NET 9 runtime. '
      + 'Choose one of the rewriting profilers.', [LProject.Name, LProject.TargetFramework]);

  FValidation.Caption := LProblem;
  // Red says "this cannot run"; a note is only news, and reads in the ordinary colour.
  if LProblem <> '' then
    FValidation.Style.TextColor := ThemeColors.Warning
  else
    FValidation.Style.TextColor := ThemeColors.Subtle;
  // A note is not a refusal: what was added is worth seeing, and Start stays enabled.
  if (LProblem = '') and (LAdded <> '') then
    FValidation.Caption := Format('Assemblies: added %s, which the callspec reaches. The build '
      + 'weaves the application''s assembly and the ones named there.', [LAdded]);
  FValidation.Visible := FValidation.Caption <> '';
  FOk.Enabled := LProblem = '';
end;

procedure TSetupDialog.CheckClick(Sender: TObject);
var
  LProblems: TArray<string>;
begin
  if (SelectedSerial = '') or (FPackage.Text = '') then
  begin
    FCheckLabel.Caption := 'Pick a device and a package first.';
    Exit;
  end;
  try
    LProblems := FClient.CheckApp(SelectedSerial, Trim(FPackage.Text), SelectedMode);
    if Length(LProblems) = 0 then
      FCheckLabel.Caption := 'Ready: the installed APK supports this mode.'
    else
      FCheckLabel.Caption := string.Join(sLineBreak, LProblems);
    FCheckLabel.Hint := FCheckLabel.Caption;
  except
    on E: Exception do
      FCheckLabel.Caption := E.Message;
  end;
end;

{ The dialog opened on a session that already exists. Everything the session recorded is
  put back where it was typed; what a session does not record - the build options, which
  describe the app and not the run - keeps coming from the settings. The configuration is
  read out of the build output path, because that is where it is visible: a session that
  read its symbols from bin\Release\... was a Release session. }
procedure TSetupDialog.ApplyPrefill;

  function ConfigurationOf(const ASymbolsDir: string): string;
  var
    LParent: string;
  begin
    // bin\<Configuration>\<tfm> - the configuration is the folder above the framework one.
    Result := '';
    if ASymbolsDir = '' then
      Exit;
    LParent := TPath.GetDirectoryName(ExcludeTrailingPathDelimiter(ASymbolsDir));
    if SameText(TPath.GetFileName(TPath.GetDirectoryName(LParent)), 'bin') then
      Result := TPath.GetFileName(LParent);
  end;

var
  LConfiguration: string;
  I: Integer;
begin
  FDeferredSolution := FPrefill.SolutionPath;
  FDeferredProject := FPrefill.ProjectPath;
  LConfiguration := ConfigurationOf(FPrefill.SymbolsDir);
  if LConfiguration <> '' then
    FConfiguration.Text := LConfiguration;
  for I := 0 to High(FDevices_) do
    if SameText(FDevices_[I].Serial, FPrefill.DeviceSerial) then
      FDevices.ItemIndex := I;
  FPackage.Text := FPrefill.Package;
  FProfiler.ItemIndex := ProfilerIndexOf(FPrefill);
  FCallspec.Text := FPrefill.Callspec;
  FAssemblies.Text := string.Join(', ', FPrefill.Assemblies);
  FDuration.Text := IntToStr(FPrefill.DurationSeconds);
  FSymbols.Text := FPrefill.SymbolsDir;
  FStartPaused.Checked := FPrefill.StartPaused;
  FFolder.Text := FPrefill.SessionsRoot;
  // A run of the same measurement is the next one, not the same one: the name moves on so
  // that two runs never answer to the same thing.
  FName.Text := NextRunName(FPrefill.Name, FPrefill.Package, FPrefill.SessionsRoot);
  // A map from a build-time weave belongs to the build that produced it, so it comes back
  // only when that build is still there; otherwise this run weaves on the device.
  FBuildWeaving.Checked := (FPrefill.WeaveMapPath <> '') and TFile.Exists(FPrefill.WeaveMapPath);
  Caption := 'Run again: ' + IfThen(FPrefill.Name <> '', FPrefill.Name, FPrefill.Package);
  FOk.Caption := 'Start';
end;

procedure TSetupDialog.PrefillFrom(const ARequest: TSessionRequest);
begin
  FPrefill := ARequest;
  FPrefilled := True;
end;

function TSetupDialog.Execute(out AResult: TSetupResult): Boolean;
var
  LDevices: TDeviceInfos;
  LProject: TAppProject;
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
  FDevices_ := LDevices;
  for I := 0 to High(LDevices) do
  begin
    FDevices.Properties.Items.Add(LDevices[I].Display);
    // The device the last session ran on, when it is still attached.
    if SameText(LDevices[I].Serial, GSettings.LastDevice) then
      FDevices.ItemIndex := I;
  end;
  if (FDevices.ItemIndex < 0) and (FDevices.Properties.Items.Count > 0) then
    FDevices.ItemIndex := 0;

  // Running the same app with a different profiler should cost one dropdown and Start,
  // not filling the form again: the last session's choices come back - or, when the dialog
  // was opened on an existing session, that session's.
  if FPrefilled then
    ApplyPrefill
  else if (GSettings.LastMode >= 0) and (GSettings.LastMode < FProfiler.Properties.Items.Count) then
    FProfiler.ItemIndex := GSettings.LastMode;
  FNoFastDeployment.Checked := GSettings.LastNoFastDeployment;
  FBuildWeaving.Checked := GSettings.LastBuildWeaving;
  FClearDeployed.Checked := GSettings.LastClearDeployed;
  if GSettings.LastDuration > 0 then
    FDuration.Text := IntToStr(GSettings.LastDuration);
  ProfilerChanged(nil);

  // Come back where the last session started from: the same solution, the same project.
  // Reading a real solution takes seconds, so it happens after the window is on screen -
  // otherwise clicking New session looks like nothing happening at all.
  if FDeferredSolution = '' then
  begin
    FDeferredSolution := GSettings.LastSolution;
    FDeferredProject := GSettings.LastProject;
  end;
  if FDeferredSolution <> '' then
  begin
    FSource.Text := FDeferredSolution;
    FProjectHint.Caption := 'Reading ' + FDeferredSolution + ' ...';
    FDeferred := TTimer.Create(Self);
    FDeferred.Interval := 1;
    FDeferred.OnTimer := DeferredScan;
    FDeferred.Enabled := True;
  end;

  Result := ShowModal = mrOk;
  if not Result then
    Exit;

  AResult.DeviceSerial := SelectedSerial;
  AResult.Package := Trim(FPackage.Text);
  AResult.Mode := SelectedMode;
  if SelectedMode = 'instrumenting' then
  begin
    AResult.Engine := SelectedEngine;
    AResult.WeaveMapPath := WeaveMapPath;
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
  AResult.Name := Trim(FName.Text);
  AResult.StartPaused := FStartPaused.Checked;
  AResult.SessionsRoot := ChosenSessionFolder;
  // Where the app came from travels with the session, so tomorrow's list reads as "my
  // runs of this product" instead of a wall of timestamps.
  AResult.SolutionPath := Trim(FSource.Text);
  if SelectedProject(LProject) then
    AResult.ProjectPath := LProject.ProjectPath;

  GSettings.LastSolution := Trim(FSource.Text);
  GSettings.LastSessionFolder := AResult.SessionsRoot;
  if SelectedProject(LProject) then
    GSettings.LastProject := LProject.ProjectPath;
  GSettings.LastMode := FProfiler.ItemIndex;
  GSettings.LastCallspec := Trim(FCallspec.Text);
  GSettings.LastAssemblies := Trim(FAssemblies.Text);
  GSettings.LastDuration := AResult.DurationSeconds;
  GSettings.LastDevice := AResult.DeviceSerial;
  GSettings.LastNoFastDeployment := FNoFastDeployment.Checked;
  GSettings.LastBuildWeaving := FBuildWeaving.Checked;
  GSettings.LastClearDeployed := FClearDeployed.Checked;
  SaveSettings;
end;

end.
