unit uGlyphs;

{
  Toolbar icons drawn in code.

  There are no icon assets in this repository and none worth adding: the toolbar needs
  eight monochrome glyphs, and eight shapes are cheaper to draw than to license. Each is
  drawn four times oversized and averaged down, which is where the antialiasing comes
  from - GDI does not give it for free - and tinted with the theme's text colour, so the
  toolbar follows light and dark like everything else.
}

interface

uses
  System.SysUtils, System.Classes, System.Math, System.Types,
  Winapi.Windows, Vcl.Graphics, Vcl.ImgList, Vcl.Controls;

type
  /// The glyphs, in the order they sit in the image list: ImageIndex = Ord(kind).
  TGlyphKind = (gkOpen, gkRefresh, gkRun, gkSnapshot, gkPause, gkStop, gkSettings, gkLayouts,
    gkExport, gkClear, gkArchive);

/// A 16x16 alpha image list holding every glyph, tinted with AColor.
function BuildGlyphs(AOwner: TComponent; AColor: TColor): TImageList;

implementation

const
  CScale = 4;
  CLarge = 16 * CScale;

procedure DrawGear(ACanvas: TCanvas);
var
  LPoints: array[0..31] of TPoint;
  LAngle: Double;
  LRadius: Double;
  I: Integer;
begin
  // Sixteen alternating points around the centre: the long ones are the teeth.
  for I := 0 to 31 do
  begin
    LAngle := I * Pi / 16;
    if (I mod 4) < 2 then
      LRadius := 30
    else
      LRadius := 22;
    LPoints[I].X := 32 + Round(LRadius * Cos(LAngle));
    LPoints[I].Y := 32 + Round(LRadius * Sin(LAngle));
  end;
  ACanvas.Polygon(LPoints);
  ACanvas.Brush.Color := clWhite;
  ACanvas.Pen.Color := clWhite;
  ACanvas.Ellipse(23, 23, 41, 41);
end;

procedure DrawRefresh(ACanvas: TCanvas);
begin
  // A ring with a bite taken out of it, plus an arrowhead: an arrow bent into a circle.
  ACanvas.Ellipse(8, 8, 56, 56);
  ACanvas.Brush.Color := clWhite;
  ACanvas.Pen.Color := clWhite;
  ACanvas.Ellipse(18, 18, 46, 46);
  ACanvas.Rectangle(32, 4, 60, 26);
  ACanvas.Brush.Color := clBlack;
  ACanvas.Pen.Color := clBlack;
  ACanvas.Polygon([Point(30, 2), Point(52, 14), Point(30, 26)]);
end;

procedure DrawGlyph(ACanvas: TCanvas; AKind: TGlyphKind);
begin
  ACanvas.Brush.Color := clBlack;
  ACanvas.Pen.Color := clBlack;
  ACanvas.Pen.Width := 1;
  case AKind of
    gkOpen:
      ACanvas.Polygon([Point(6, 14), Point(26, 14), Point(32, 22), Point(58, 22),
        Point(58, 52), Point(6, 52)]);
    gkRefresh:
      DrawRefresh(ACanvas);
    gkRun:
      ACanvas.Polygon([Point(18, 10), Point(52, 32), Point(18, 54)]);
    gkSnapshot:
      begin
        ACanvas.Rectangle(22, 12, 40, 24);
        ACanvas.RoundRect(8, 20, 56, 54, 10, 10);
        ACanvas.Brush.Color := clWhite;
        ACanvas.Pen.Color := clWhite;
        ACanvas.Ellipse(24, 26, 46, 48);
        ACanvas.Brush.Color := clBlack;
        ACanvas.Pen.Color := clBlack;
        ACanvas.Ellipse(29, 31, 41, 43);
      end;
    gkPause:
      begin
        ACanvas.Rectangle(18, 12, 29, 52);
        ACanvas.Rectangle(35, 12, 46, 52);
      end;
    gkStop:
      ACanvas.Rectangle(16, 16, 48, 48);
    gkSettings:
      DrawGear(ACanvas);
    gkArchive:
      begin
        // A box with a lid and a label: results put away under a name.
        ACanvas.Rectangle(10, 22, 54, 56);
        ACanvas.Rectangle(6, 10, 58, 24);
        ACanvas.Brush.Color := clWhite;
        ACanvas.Pen.Color := clWhite;
        ACanvas.Rectangle(24, 32, 40, 40);
      end;

    gkClear:
      begin
        // A bin: what Clear does to the results collected so far.
        ACanvas.Polygon([Point(16, 20), Point(48, 20), Point(44, 56), Point(20, 56)]);
        ACanvas.Rectangle(12, 12, 52, 20);
        ACanvas.Rectangle(26, 6, 38, 12);
        ACanvas.Brush.Color := clWhite;
        ACanvas.Pen.Color := clWhite;
        ACanvas.Pen.Width := 3;
        ACanvas.MoveTo(26, 27); ACanvas.LineTo(24, 49);
        ACanvas.MoveTo(38, 27); ACanvas.LineTo(40, 49);
      end;
    gkExport:
      begin
        // A sheet with an arrow leaving it.
        ACanvas.Pen.Width := 5;
        ACanvas.Brush.Color := clWhite;
        ACanvas.Rectangle(8, 6, 40, 58);
        ACanvas.Brush.Color := clBlack;
        ACanvas.Pen.Width := 6;
        ACanvas.MoveTo(30, 32);
        ACanvas.LineTo(56, 32);
        ACanvas.Polygon([Point(46, 20), Point(60, 32), Point(46, 44)]);
      end;
    gkLayouts:
      begin
        // Three panels: the shape of an arrangement worth saving.
        ACanvas.Pen.Width := 5;
        ACanvas.Brush.Color := clWhite;
        ACanvas.Rectangle(8, 10, 56, 54);
        ACanvas.MoveTo(30, 10);
        ACanvas.LineTo(30, 54);
        ACanvas.MoveTo(30, 32);
        ACanvas.LineTo(56, 32);
      end;
  end;
end;

/// Averages the oversized drawing down to 16x16 and paints it in AColor: black pixels
/// become opaque colour, white ones disappear, and everything between is the edge.
function Downsample(ALarge: TBitmap; AColor: TColor): TBitmap;
var
  LRows: array of PByte;
  LOut: PRGBQuad;
  LColor: TColor;
  LSum, LAlpha: Integer;
  X, Y, DX, DY: Integer;
begin
  LColor := ColorToRGB(AColor);
  SetLength(LRows, CLarge);
  for Y := 0 to CLarge - 1 do
    LRows[Y] := ALarge.ScanLine[Y];

  Result := TBitmap.Create;
  Result.PixelFormat := pf32bit;
  Result.SetSize(16, 16);
  Result.AlphaFormat := afIgnored;
  for Y := 0 to 15 do
  begin
    LOut := Result.ScanLine[Y];
    for X := 0 to 15 do
    begin
      LSum := 0;
      for DY := 0 to CScale - 1 do
        for DX := 0 to CScale - 1 do
          LSum := LSum + (LRows[Y * CScale + DY] + (X * CScale + DX) * 3)^;
      LAlpha := 255 - LSum div (CScale * CScale);
      // Premultiplied, which is what an alpha image list expects.
      LOut^.rgbBlue := (GetBValue(LColor) * LAlpha) div 255;
      LOut^.rgbGreen := (GetGValue(LColor) * LAlpha) div 255;
      LOut^.rgbRed := (GetRValue(LColor) * LAlpha) div 255;
      LOut^.rgbReserved := LAlpha;
      Inc(LOut);
    end;
  end;
  Result.AlphaFormat := afDefined;
end;

function BuildGlyphs(AOwner: TComponent; AColor: TColor): TImageList;
var
  LLarge, LSmall: TBitmap;
  LKind: TGlyphKind;
begin
  Result := TImageList.Create(AOwner);
  Result.ColorDepth := cd32Bit;
  Result.SetSize(16, 16);
  LLarge := TBitmap.Create;
  try
    LLarge.PixelFormat := pf24bit;
    LLarge.SetSize(CLarge, CLarge);
    for LKind := Low(TGlyphKind) to High(TGlyphKind) do
    begin
      LLarge.Canvas.Brush.Color := clWhite;
      LLarge.Canvas.FillRect(Rect(0, 0, CLarge, CLarge));
      DrawGlyph(LLarge.Canvas, LKind);
      LSmall := Downsample(LLarge, AColor);
      try
        Result.Add(LSmall, nil);
      finally
        LSmall.Free;
      end;
    end;
  finally
    LLarge.Free;
  end;
end;

end.
