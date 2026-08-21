unit uLayouts;

{
  Named panel layouts, the way an IDE keeps them: save the arrangement you like under a
  name, load it back, rename or delete it, and mark one as the layout to open with.

  The docking controller writes a whole ini per layout (its own save/load takes a file,
  not a section), so a layout is simply a file in the layouts folder and the name is the
  file name.
}

interface

uses
  System.SysUtils, System.IOUtils, System.Classes, System.StrUtils,
  dxDockControl, dxBar,
  uSettings;

var
  /// The toolbars are part of an arrangement too: the form registers its manager here so
  /// a saved layout puts the bars back where they were, not only the panels.
  GBarManager: TdxBarManager = nil;

/// Names of the layouts saved so far, alphabetically.
function LayoutNames: TArray<string>;
function LayoutExists(const AName: string): Boolean;
procedure SaveLayoutAs(const AName: string);
procedure LoadNamedLayout(const AName: string);
procedure DeleteLayout(const AName: string);
procedure RenameLayout(const AOldName, ANewName: string);
/// The layout loaded at start-up; empty when the last used one should be kept.
procedure SetDefaultLayout(const AName: string);

/// The unnamed layout: what the window looked like when it was last closed.
function LastLayoutFile: string;

implementation

function LayoutFile(const AName: string): string;
begin
  Result := TPath.Combine(LayoutsDirectory, AName + '.ini');
end;

function BarsFile(const AName: string): string;
begin
  Result := TPath.Combine(LayoutsDirectory, AName + '.bars.ini');
end;

function LastLayoutFile: string;
begin
  Result := TPath.ChangeExtension(ParamStr(0), '.layout.ini');
end;

function LayoutNames: TArray<string>;
var
  LFiles: TArray<string>;
  LList: TStringList;
  I: Integer;
begin
  if not TDirectory.Exists(LayoutsDirectory) then
    Exit(nil);
  LFiles := TDirectory.GetFiles(LayoutsDirectory, '*.ini');
  LList := TStringList.Create;
  try
    for I := 0 to High(LFiles) do
      // The companion toolbar file is not a layout of its own.
      if not EndsText('.bars.ini', LFiles[I]) then
        LList.Add(TPath.GetFileNameWithoutExtension(LFiles[I]));
    LList.Sort;
    Result := LList.ToStringArray;
  finally
    LList.Free;
  end;
end;

function LayoutExists(const AName: string): Boolean;
begin
  Result := (AName <> '') and TFile.Exists(LayoutFile(AName));
end;

procedure SaveLayoutAs(const AName: string);
begin
  if AName = '' then
    raise Exception.Create('A layout needs a name.');
  TDirectory.CreateDirectory(LayoutsDirectory);
  dxDockingController.SaveLayoutToIniFile(LayoutFile(AName));
  if GBarManager <> nil then
    GBarManager.SaveToIniFile(BarsFile(AName));
end;

procedure LoadNamedLayout(const AName: string);
begin
  if not LayoutExists(AName) then
    raise Exception.CreateFmt('There is no layout called "%s".', [AName]);
  dxDockingController.LoadLayoutFromIniFile(LayoutFile(AName));
  if (GBarManager <> nil) and TFile.Exists(BarsFile(AName)) then
    GBarManager.LoadFromIniFile(BarsFile(AName));
end;

procedure DeleteLayout(const AName: string);
begin
  if LayoutExists(AName) then
    TFile.Delete(LayoutFile(AName));
  if TFile.Exists(BarsFile(AName)) then
    TFile.Delete(BarsFile(AName));
  // A deleted layout cannot stay the one we open with.
  if SameText(GSettings.DefaultLayout, AName) then
  begin
    GSettings.DefaultLayout := '';
    SaveSettings;
  end;
end;

procedure RenameLayout(const AOldName, ANewName: string);
begin
  if not LayoutExists(AOldName) then
    Exit;
  if ANewName = '' then
    raise Exception.Create('A layout needs a name.');
  if LayoutExists(ANewName) then
    raise Exception.CreateFmt('There is already a layout called "%s".', [ANewName]);
  TFile.Move(LayoutFile(AOldName), LayoutFile(ANewName));
  if TFile.Exists(BarsFile(AOldName)) then
    TFile.Move(BarsFile(AOldName), BarsFile(ANewName));
  if SameText(GSettings.DefaultLayout, AOldName) then
  begin
    GSettings.DefaultLayout := ANewName;
    SaveSettings;
  end;
end;

procedure SetDefaultLayout(const AName: string);
begin
  GSettings.DefaultLayout := AName;
  SaveSettings;
end;

end.
