unit uGuiRender;

{
  Drawing a control into a picture, so that a panel can be handed to whoever asks for it
  without a window on screen and without capturing the desktop.

  Everything here works on a form that was never shown: what a control needs is a window
  handle, not a place on the monitor (TMainForm creates its handle in the constructor for
  the docking library, which is why this holds). Measured with a probe before the channel
  was written: a skinned TcxGrid and a hand-drawn TPaintBox both render, and one PaintTo
  of their container brings both.

  Two kinds of control, two ways in:
    - a windowed control (grids, tree lists, dock panels, the form) paints itself and its
      children into a device context through PaintTo;
    - a TPaintBox has no window of its own, so its paint handler is asked to draw on the
      bitmap's canvas instead (TGraphicControl.WMPaint takes the DC from the message).
}

interface

uses
  System.SysUtils, System.Classes, Vcl.Controls, Vcl.Graphics;

/// The control as a PNG, at the size it currently has. The background is painted first,
/// so an area the control leaves untouched is that colour rather than uninitialised.
function ControlToPng(AControl: TControl; ABackground: TColor = clBtnFace): TBytes;

/// The control as a bitmap; the caller owns it.
function ControlToBitmap(AControl: TControl; ABackground: TColor = clBtnFace): TBitmap;

/// A bitmap as PNG bytes.
function BitmapToPng(ABitmap: TBitmap): TBytes;

/// True when the picture is more than one flat colour: a render that produced nothing
/// looks exactly like a render that was never asked for, and this tells them apart.
function HasContent(ABitmap: TBitmap): Boolean;

implementation

uses
  // Winapi.Windows is deliberately absent: it has a TBitmap of its own (the GDI structure),
  // and a unit named here wins over Vcl.Graphics in the interface, so every TBitmap below
  // would quietly become the wrong type. Only the paint message is needed from there.
  System.Types, System.Math, Winapi.Messages, Vcl.Imaging.pngimage;

function ControlToBitmap(AControl: TControl; ABackground: TColor): TBitmap;
begin
  if AControl = nil then
    raise EArgumentException.Create('There is no control to render.');
  Result := TBitmap.Create;
  try
    Result.PixelFormat := pf24bit;
    Result.SetSize(Max(1, AControl.Width), Max(1, AControl.Height));
    Result.Canvas.Brush.Color := ABackground;
    Result.Canvas.FillRect(Rect(0, 0, Result.Width, Result.Height));
    if AControl is TWinControl then
      TWinControl(AControl).PaintTo(Result.Canvas, 0, 0)
    else
    begin
      Result.Canvas.Lock;
      try
        AControl.Perform(WM_PAINT, Result.Canvas.Handle, 0);
      finally
        Result.Canvas.Unlock;
      end;
    end;
  except
    Result.Free;
    raise;
  end;
end;

function BitmapToPng(ABitmap: TBitmap): TBytes;
var
  LPng: TPngImage;
  LStream: TBytesStream;
begin
  LPng := TPngImage.Create;
  try
    LPng.Assign(ABitmap);
    LStream := TBytesStream.Create;
    try
      LPng.SaveToStream(LStream);
      Result := Copy(LStream.Bytes, 0, LStream.Size);
    finally
      LStream.Free;
    end;
  finally
    LPng.Free;
  end;
end;

function ControlToPng(AControl: TControl; ABackground: TColor): TBytes;
var
  LBitmap: TBitmap;
begin
  LBitmap := ControlToBitmap(AControl, ABackground);
  try
    Result := BitmapToPng(LBitmap);
  finally
    LBitmap.Free;
  end;
end;

function HasContent(ABitmap: TBitmap): Boolean;
const
  CStep = 8;
var
  LFirst: TColor;
  LX, LY: Integer;
begin
  if (ABitmap = nil) or (ABitmap.Width = 0) or (ABitmap.Height = 0) then
    Exit(False);
  LFirst := ABitmap.Canvas.Pixels[0, 0];
  LY := 0;
  while LY < ABitmap.Height do
  begin
    LX := 0;
    while LX < ABitmap.Width do
    begin
      if ABitmap.Canvas.Pixels[LX, LY] <> LFirst then
        Exit(True);
      Inc(LX, CStep);
    end;
    Inc(LY, CStep);
  end;
  Result := False;
end;

end.
