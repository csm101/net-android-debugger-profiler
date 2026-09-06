unit uTheme;

{
  Light and dark theme for the profiler GUI.

  Three layers have to agree, or the window looks half-painted:
    - the DevExpress controls, which follow a skin;
    - SynEdit and the plain VCL bits (log, summary), which take explicit colours;
    - the panels we draw ourselves (pies, call graph, monitor), which read this palette.

  The palette follows the one used by CVSTreeGraph (C:\Athens\DelphiTools\CVSTreeGraph),
  so the tools on this desk look like they belong together.
}

interface

uses
  System.SysUtils, Vcl.Graphics, Vcl.Forms,
  SynEdit, SynEditHighlighter;

type
  TAppTheme = (atLight, atDark);

  /// Every colour the hand-drawn panels and the plain controls need.
  TThemeColors = record
    Window: TColor;          // panel and canvas background
    Text: TColor;
    Subtle: TColor;          // secondary text, axis labels
    Line: TColor;            // arrows, axes, separators
    BoxFill: TColor;         // call-graph box
    BoxCentreFill: TColor;   // the focused method's box
    Accent: TColor;          // chart line, highlights
    EditorBack: TColor;
    EditorText: TColor;
    GutterBack: TColor;
    GutterText: TColor;
    RangeWash: TColor;       // the profiled method's line range in the Source panel
    Slices: array[0..5] of TColor;
  end;

var
  /// The theme in force. Changing it means calling ApplyTheme again.
  GTheme: TAppTheme = atLight;

function ThemeColors: TThemeColors;
function SkinNameFor(ATheme: TAppTheme): string;
/// Colour a SynEdit and its highlighter for the current theme.
procedure ApplyThemeToEditor(AEditor: TSynEdit);
procedure ApplyThemeToHighlighter(AHighlighter: TSynCustomHighlighter);
function ThemeName(ATheme: TAppTheme): string;

implementation

uses
  SynEditMiscClasses;

function ThemeName(ATheme: TAppTheme): string;
begin
  if ATheme = atDark then
    Result := 'Dark'
  else
    Result := 'Light';
end;

function SkinNameFor(ATheme: TAppTheme): string;
begin
  // Two skins from the same family, so only the brightness changes.
  if ATheme = atDark then
    Result := 'Office2019Black'
  else
    Result := 'Office2019Colorful';
end;

function ThemeColors: TThemeColors;
begin
  if GTheme = atDark then
  begin
    Result.Window := $001E1E1E;
    Result.Text := $00E6E6E6;
    Result.Subtle := $008A8A8A;
    Result.Line := $004F4F4F;
    Result.BoxFill := $002A2A2A;
    Result.BoxCentreFill := $00454545;
    Result.Accent := $00D7BA7D;
    Result.EditorBack := $001E1E1E;
    Result.EditorText := $00E6E6E6;
    Result.GutterBack := $002C2C2C;
    Result.GutterText := $008A8A8A;
    Result.RangeWash := $003A3A2A;
    Result.Slices[0] := $005C5CFF;
    Result.Slices[1] := $0060C060;
    Result.Slices[2] := $0030C0F0;
    Result.Slices[3] := $00E09050;
    Result.Slices[4] := $00C070C0;
    Result.Slices[5] := $00909090;
  end
  else
  begin
    Result.Window := clWindow;
    Result.Text := clWindowText;
    Result.Subtle := $00909090;
    Result.Line := $00A0A0A0;
    Result.BoxFill := $00F8F8F8;
    Result.BoxCentreFill := $00F0E0C0;
    Result.Accent := $00C08040;
    Result.EditorBack := $00FAFAFE;
    Result.EditorText := clWindowText;
    Result.GutterBack := $00F2F2F2;
    Result.GutterText := $00909090;
    Result.RangeWash := $00E8F4FF;
    Result.Slices[0] := $004040FF;
    Result.Slices[1] := $0040C040;
    Result.Slices[2] := $0000D0FF;
    Result.Slices[3] := $00FF8040;
    Result.Slices[4] := $00C040C0;
    Result.Slices[5] := $00909090;
  end;
end;

procedure ApplyThemeToHighlighter(AHighlighter: TSynCustomHighlighter);
var
  LKeyword, LString, LComment: TColor;
  LAttribute: TSynHighlighterAttributes;
  I: Integer;
  LName: string;
begin
  if AHighlighter = nil then
    Exit;
  if GTheme = atDark then
  begin
    LKeyword := $00F69D50;
    LString := $00D69D85;
    LComment := $0079C37A;
  end
  else
  begin
    LKeyword := clNavy;
    LString := clMaroon;
    LComment := clGreen;
  end;
  // Attributes are matched by name rather than by class: every highlighter names them
  // differently, and this way one rule covers whichever language we end up showing.
  for I := 0 to AHighlighter.AttrCount - 1 do
  begin
    LAttribute := AHighlighter.Attribute[I];
    if LAttribute = nil then
      Continue;
    LName := LowerCase(LAttribute.Name);
    if Pos('comment', LName) > 0 then
      LAttribute.Foreground := LComment
    else if (Pos('key', LName) > 0) or (Pos('reserved', LName) > 0) or (Pos('directive', LName) > 0) then
      LAttribute.Foreground := LKeyword
    else if (Pos('string', LName) > 0) or (Pos('char', LName) > 0) then
      LAttribute.Foreground := LString
    else
      LAttribute.Foreground := ThemeColors.EditorText;
  end;
end;

procedure ApplyThemeToEditor(AEditor: TSynEdit);
var
  LColors: TThemeColors;
begin
  if AEditor = nil then
    Exit;
  LColors := ThemeColors;
  AEditor.Color := LColors.EditorBack;
  AEditor.Font.Color := LColors.EditorText;
  AEditor.Gutter.Color := LColors.GutterBack;
  AEditor.Gutter.Font.Color := LColors.GutterText;
  AEditor.ActiveLineColor := clNone;
  ApplyThemeToHighlighter(AEditor.Highlighter);
  AEditor.Invalidate;
  AEditor.InvalidateGutter;
end;

end.
