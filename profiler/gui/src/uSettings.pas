unit uSettings;

{
  What the user chose, kept between runs: theme, the unit times are shown in, the font
  the code and the logs are read in, where sessions live, and which nap.exe drives them.

  Stored in an ini next to the executable rather than in the registry: a profiler is
  often carried around in a folder, and its preferences should travel with it.
}

interface

uses
  System.SysUtils, System.IOUtils, System.IniFiles,
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
    /// Explicit path to nap.exe; empty means "look next to this application".
    NapExePath: string;
    /// Panel layout loaded at start, from the named layouts. Empty means the last one used.
    DefaultLayout: string;
  end;

var
  GSettings: TAppSettings;

function SettingsFileName: string;
function LayoutsDirectory: string;
procedure LoadSettings;
procedure SaveSettings;

implementation

function SettingsFileName: string;
begin
  Result := TPath.ChangeExtension(ParamStr(0), '.settings.ini');
end;

function LayoutsDirectory: string;
begin
  Result := TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), 'layouts');
end;

procedure LoadSettings;
var
  LIni: TIniFile;
begin
  // Defaults first, so a missing or partial file still leaves a usable configuration.
  GSettings.Theme := atLight;
  GSettings.TimeUnit := tuAuto;
  GSettings.CodeFontName := 'Consolas';
  GSettings.CodeFontSize := 10;
  GSettings.SessionsRoot := '';
  GSettings.NapExePath := '';
  GSettings.DefaultLayout := '';
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
    GSettings.NapExePath := LIni.ReadString('App', 'NapExePath', '');
    GSettings.DefaultLayout := LIni.ReadString('App', 'DefaultLayout', '');
  finally
    LIni.Free;
  end;
  GTheme := GSettings.Theme;
  GTimeUnit := GSettings.TimeUnit;
end;

procedure SaveSettings;
var
  LIni: TIniFile;
begin
  GSettings.Theme := GTheme;
  GSettings.TimeUnit := GTimeUnit;
  try
    LIni := TIniFile.Create(SettingsFileName);
    try
      LIni.WriteString('App', 'Theme', ThemeName(GSettings.Theme));
      LIni.WriteInteger('App', 'TimeUnit', Ord(GSettings.TimeUnit));
      LIni.WriteString('App', 'CodeFontName', GSettings.CodeFontName);
      LIni.WriteInteger('App', 'CodeFontSize', GSettings.CodeFontSize);
      LIni.WriteString('App', 'SessionsRoot', GSettings.SessionsRoot);
      LIni.WriteString('App', 'NapExePath', GSettings.NapExePath);
      LIni.WriteString('App', 'DefaultLayout', GSettings.DefaultLayout);
    finally
      LIni.Free;
    end;
  except
    // preferences are a convenience: never fail the application over them
  end;
end;

end.
