unit uSettings;

{
  What the user chose, kept between runs: theme, the unit times are shown in, the font
  the code and the logs are read in, where sessions live, and which nap.exe drives them.

  Stored in an ini next to the executable rather than in the registry: a profiler is
  often carried around in a folder, and its preferences should travel with it.
}

interface

uses
  System.SysUtils, System.Classes, System.IOUtils, System.IniFiles,
  uTheme, uSessionStore;

type
  TAppSettings = record
    Theme: TAppTheme;
    TimeUnit: TTimeUnit;
    /// Font of the Source panel, the session log and the summary.
    CodeFontName: string;
    CodeFontSize: Integer;
    /// Where sessions are stored and listed; empty means the service's own default.
    SessionsRoot: string;
    /// Folders a session was deliberately saved in besides that root. A session kept
    /// beside the product it measures would otherwise disappear from the Explorer the
    /// moment it was created, which is the fastest way to make "save it where I want"
    /// useless.
    SessionFolders: TArray<string>;
    /// The folder the last new session was saved in, offered again by the setup dialog.
    LastSessionFolder: string;
    /// Explicit path to nap.exe; empty means "look next to this application".
    NapExePath: string;
    /// Panel layout loaded at start, from the named layouts. Empty means the last one used.
    DefaultLayout: string;
    /// Solution, project or folder the session setup last scanned for applications.
    LastSolution: string;
    /// Project of that solution the user last profiled, so it comes back selected.
    LastProject: string;
    /// The rest of the last session's setup, so that running the same app with a
    /// different profiler costs one dropdown and Start rather than filling a form again.
    LastMode: Integer;
    LastEngine: string;
    LastCallspec: string;
    LastAssemblies: string;
    LastDuration: Integer;
    LastDevice: string;
    /// The build options of the last session: they describe the app, not the run, so they
    /// are the ones that must not have to be set again tomorrow.
    LastNoFastDeployment: Boolean;
    LastBuildWeaving: Boolean;
    LastClearDeployed: Boolean;
  end;

var
  GSettings: TAppSettings;

function SettingsFileName: string;
function LayoutsDirectory: string;
procedure LoadSettings;
procedure SaveSettings;
/// Keep a folder in the list the Explorer browses. The standard sessions root is always
/// browsed and is never added; a folder already known is not added twice.
procedure RememberSessionFolder(const AFolder: string);

implementation

const
  CDefaultFontName = 'Consolas';
  CDefaultFontSize = 10;

function SettingsFileName: string;
begin
  Result := TPath.ChangeExtension(ParamStr(0), '.settings.ini');
end;

/// A font has to be nameable and have a size. An empty name or a size of zero reaches
/// the editor as an unusable font, so it is corrected here rather than defended against
/// at every place the font is applied.
procedure FixUpFont(var ASettings: TAppSettings);
begin
  if Trim(ASettings.CodeFontName) = '' then
    ASettings.CodeFontName := CDefaultFontName;
  if ASettings.CodeFontSize <= 0 then
    ASettings.CodeFontSize := CDefaultFontSize;
end;

function LayoutsDirectory: string;
begin
  Result := TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), 'layouts');
end;

{ The folders sessions were saved in live in a section of their own: a list has no
  natural key, and numbering them in the App section would make it unreadable. }
procedure ReadSessionFolders(AIni: TIniFile);
var
  LNames: TStringList;
  LFolder: string;
  I: Integer;
begin
  GSettings.SessionFolders := nil;
  LNames := TStringList.Create;
  try
    AIni.ReadSection('SessionFolders', LNames);
    for I := 0 to LNames.Count - 1 do
    begin
      LFolder := AIni.ReadString('SessionFolders', LNames[I], '');
      if LFolder <> '' then
        GSettings.SessionFolders := GSettings.SessionFolders + [LFolder];
    end;
  finally
    LNames.Free;
  end;
end;

procedure WriteSessionFolders(AIni: TIniFile);
var
  I: Integer;
begin
  AIni.EraseSection('SessionFolders');
  for I := 0 to High(GSettings.SessionFolders) do
    AIni.WriteString('SessionFolders', 'Folder' + IntToStr(I), GSettings.SessionFolders[I]);
end;

procedure RememberSessionFolder(const AFolder: string);
var
  LFolder: string;
begin
  LFolder := Trim(AFolder);
  if LFolder = '' then
    Exit;
  LFolder := ExcludeTrailingPathDelimiter(LFolder);
  if SameText(LFolder, ExcludeTrailingPathDelimiter(GSettings.SessionsRoot)) then
    Exit;
  for var LKnown in GSettings.SessionFolders do
    if SameText(LKnown, LFolder) then
      Exit;
  GSettings.SessionFolders := GSettings.SessionFolders + [LFolder];
  SaveSettings;
end;

procedure LoadSettings;
var
  LIni: TIniFile;
begin
  // Defaults first, so a missing or partial file still leaves a usable configuration.
  GSettings.Theme := atLight;
  GSettings.TimeUnit := tuAuto;
  GSettings.CodeFontName := CDefaultFontName;
  GSettings.CodeFontSize := CDefaultFontSize;
  GSettings.SessionsRoot := '';
  GSettings.SessionFolders := nil;
  GSettings.LastSessionFolder := '';
  GSettings.NapExePath := '';
  GSettings.DefaultLayout := '';
  GSettings.LastSolution := '';
  GSettings.LastProject := '';
  GSettings.LastMode := 0;
  GSettings.LastEngine := '';
  GSettings.LastCallspec := '';
  GSettings.LastAssemblies := '';
  GSettings.LastDuration := 0;
  GSettings.LastDevice := '';
  GSettings.LastNoFastDeployment := False;
  GSettings.LastBuildWeaving := False;
  GSettings.LastClearDeployed := False;
  if not TFile.Exists(SettingsFileName) then
    Exit;
  LIni := TIniFile.Create(SettingsFileName);
  try
    if SameText(LIni.ReadString('App', 'Theme', 'Light'), 'Dark') then
      GSettings.Theme := atDark;
    GSettings.TimeUnit := TTimeUnit(LIni.ReadInteger('App', 'TimeUnit', Ord(tuAuto)));
    GSettings.CodeFontName := LIni.ReadString('App', 'CodeFontName', GSettings.CodeFontName);
    GSettings.CodeFontSize := LIni.ReadInteger('App', 'CodeFontSize', GSettings.CodeFontSize);
    GSettings.SessionsRoot := LIni.ReadString('App', 'SessionsRoot', '');
    GSettings.LastSessionFolder := LIni.ReadString('App', 'LastSessionFolder', '');
    ReadSessionFolders(LIni);
    GSettings.NapExePath := LIni.ReadString('App', 'NapExePath', '');
    GSettings.DefaultLayout := LIni.ReadString('App', 'DefaultLayout', '');
    GSettings.LastSolution := LIni.ReadString('App', 'LastSolution', '');
    GSettings.LastProject := LIni.ReadString('App', 'LastProject', '');
    GSettings.LastMode := LIni.ReadInteger('App', 'LastMode', 0);
    GSettings.LastEngine := LIni.ReadString('App', 'LastEngine', '');
    GSettings.LastCallspec := LIni.ReadString('App', 'LastCallspec', '');
    GSettings.LastAssemblies := LIni.ReadString('App', 'LastAssemblies', '');
    GSettings.LastDuration := LIni.ReadInteger('App', 'LastDuration', 0);
    GSettings.LastDevice := LIni.ReadString('App', 'LastDevice', '');
    GSettings.LastNoFastDeployment := LIni.ReadBool('App', 'LastNoFastDeployment', False);
    GSettings.LastBuildWeaving := LIni.ReadBool('App', 'LastBuildWeaving', False);
    GSettings.LastClearDeployed := LIni.ReadBool('App', 'LastClearDeployed', False);
  finally
    LIni.Free;
  end;
  // A file written by a run that died before it read its own settings holds empty
  // values, and reading them back would kill the next run the same way.
  FixUpFont(GSettings);
  GTheme := GSettings.Theme;
  GTimeUnit := GSettings.TimeUnit;
end;

procedure SaveSettings;
var
  LIni: TIniFile;
begin
  GSettings.Theme := GTheme;
  GSettings.TimeUnit := GTimeUnit;
  FixUpFont(GSettings);
  try
    LIni := TIniFile.Create(SettingsFileName);
    try
      LIni.WriteString('App', 'Theme', ThemeName(GSettings.Theme));
      LIni.WriteInteger('App', 'TimeUnit', Ord(GSettings.TimeUnit));
      LIni.WriteString('App', 'CodeFontName', GSettings.CodeFontName);
      LIni.WriteInteger('App', 'CodeFontSize', GSettings.CodeFontSize);
      LIni.WriteString('App', 'SessionsRoot', GSettings.SessionsRoot);
      LIni.WriteString('App', 'LastSessionFolder', GSettings.LastSessionFolder);
      WriteSessionFolders(LIni);
      LIni.WriteString('App', 'NapExePath', GSettings.NapExePath);
      LIni.WriteString('App', 'DefaultLayout', GSettings.DefaultLayout);
      LIni.WriteString('App', 'LastSolution', GSettings.LastSolution);
      LIni.WriteString('App', 'LastProject', GSettings.LastProject);
      LIni.WriteInteger('App', 'LastMode', GSettings.LastMode);
      LIni.WriteString('App', 'LastEngine', GSettings.LastEngine);
      LIni.WriteString('App', 'LastCallspec', GSettings.LastCallspec);
      LIni.WriteString('App', 'LastAssemblies', GSettings.LastAssemblies);
      LIni.WriteInteger('App', 'LastDuration', GSettings.LastDuration);
      LIni.WriteString('App', 'LastDevice', GSettings.LastDevice);
      LIni.WriteBool('App', 'LastNoFastDeployment', GSettings.LastNoFastDeployment);
      LIni.WriteBool('App', 'LastBuildWeaving', GSettings.LastBuildWeaving);
      LIni.WriteBool('App', 'LastClearDeployed', GSettings.LastClearDeployed);
    finally
      LIni.Free;
    end;
  except
    // preferences are a convenience: never fail the application over them
  end;
end;

end.
