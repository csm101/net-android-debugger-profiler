unit uLayoutDialog;

{
  Managing the saved panel layouts: pick one to load, rename or delete one, and choose
  which one the window opens with.
}

interface

uses
  System.SysUtils, System.Classes, System.UITypes,
  Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.StdCtrls,
  cxLabel, cxButtons, cxListBox,
  uSettings, uLayouts;

type
  TLayoutDialog = class(TForm)
  private
    FList: TcxListBox;
    FLoad: TcxButton;
    FRename: TcxButton;
    FDelete: TcxButton;
    FDefault: TcxButton;
    FSaveAs: TcxButton;
    FHint: TcxLabel;
    FChosen: string;
    procedure Build;
    procedure RefreshList;
    procedure UpdateButtons;
    procedure ListClick(Sender: TObject);
    procedure LoadClick(Sender: TObject);
    procedure RenameClick(Sender: TObject);
    procedure DeleteClick(Sender: TObject);
    procedure DefaultClick(Sender: TObject);
    procedure SaveAsClick(Sender: TObject);
    function Selected: string;
  public
    constructor Create(AOwner: TComponent); reintroduce;
    /// Returns the layout the user asked to load, or an empty string.
    function Execute: string;
  end;

implementation

constructor TLayoutDialog.Create(AOwner: TComponent);
begin
  inherited CreateNew(AOwner);
  Build;
end;

procedure TLayoutDialog.Build;
var
  LClose: TcxButton;
begin
  Caption := 'Saved layouts';
  BorderStyle := bsSizeable;
  Position := poOwnerFormCenter;
  ClientWidth := 480;
  ClientHeight := 320;
  // The list takes the room; the buttons keep to the right edge.
  Constraints.MinWidth := 496;
  Constraints.MinHeight := 300;

  FList := TcxListBox.Create(Self);
  FList.Parent := Self;
  FList.SetBounds(16, 16, 300, 240);
  FList.Anchors := [akLeft, akTop, akRight, akBottom];
  FList.OnClick := ListClick;

  FLoad := TcxButton.Create(Self);
  FLoad.Parent := Self;
  FLoad.SetBounds(332, 16, 130, 28);
  FLoad.Anchors := [akTop, akRight];
  FLoad.Caption := 'Load';
  FLoad.OnClick := LoadClick;

  FRename := TcxButton.Create(Self);
  FRename.Parent := Self;
  FRename.SetBounds(332, 52, 130, 28);
  FRename.Anchors := [akTop, akRight];
  FRename.Caption := 'Rename...';
  FRename.OnClick := RenameClick;

  FDelete := TcxButton.Create(Self);
  FDelete.Parent := Self;
  FDelete.SetBounds(332, 88, 130, 28);
  FDelete.Anchors := [akTop, akRight];
  FDelete.Caption := 'Delete';
  FDelete.OnClick := DeleteClick;

  FDefault := TcxButton.Create(Self);
  FDefault.Parent := Self;
  FDefault.SetBounds(332, 124, 130, 28);
  FDefault.Anchors := [akTop, akRight];
  FDefault.Caption := 'Open with this';
  FDefault.OnClick := DefaultClick;

  FSaveAs := TcxButton.Create(Self);
  FSaveAs.Parent := Self;
  FSaveAs.SetBounds(332, 172, 130, 28);
  FSaveAs.Anchors := [akTop, akRight];
  FSaveAs.Caption := 'Save current as...';
  FSaveAs.OnClick := SaveAsClick;

  FHint := TcxLabel.Create(Self);
  FHint.Transparent := True;
  FHint.Parent := Self;
  FHint.SetBounds(16, 264, 440, 20);
  FHint.Anchors := [akLeft, akRight, akBottom];

  LClose := TcxButton.Create(Self);
  LClose.Parent := Self;
  LClose.SetBounds(372, 284, 90, 28);
  LClose.Anchors := [akRight, akBottom];
  LClose.Caption := 'Close';
  LClose.ModalResult := mrCancel;
  LClose.Cancel := True;
end;

function TLayoutDialog.Selected: string;
begin
  if FList.ItemIndex < 0 then
    Result := ''
  else
    Result := FList.Items[FList.ItemIndex];
end;

procedure TLayoutDialog.RefreshList;
var
  LNames: TArray<string>;
  I: Integer;
begin
  FList.Items.BeginUpdate;
  try
    FList.Items.Clear;
    LNames := LayoutNames;
    for I := 0 to High(LNames) do
      FList.Items.Add(LNames[I]);
  finally
    FList.Items.EndUpdate;
  end;
  if GSettings.DefaultLayout = '' then
    FHint.Caption := 'The window opens with the layout it was last closed in.'
  else
    FHint.Caption := Format('The window opens with "%s".', [GSettings.DefaultLayout]);
  UpdateButtons;
end;

procedure TLayoutDialog.UpdateButtons;
var
  LHasSelection: Boolean;
begin
  LHasSelection := Selected <> '';
  FLoad.Enabled := LHasSelection;
  FRename.Enabled := LHasSelection;
  FDelete.Enabled := LHasSelection;
  FDefault.Enabled := LHasSelection;
end;

procedure TLayoutDialog.ListClick(Sender: TObject);
begin
  UpdateButtons;
end;

procedure TLayoutDialog.LoadClick(Sender: TObject);
begin
  FChosen := Selected;
  ModalResult := mrOk;
end;

procedure TLayoutDialog.RenameClick(Sender: TObject);
var
  LNewName: string;
begin
  LNewName := Selected;
  if not InputQuery('Rename layout', 'New name', LNewName) then
    Exit;
  try
    RenameLayout(Selected, Trim(LNewName));
    RefreshList;
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TLayoutDialog.DeleteClick(Sender: TObject);
begin
  if MessageDlg(Format('Delete the layout "%s"?', [Selected]), mtConfirmation, [mbYes, mbNo], 0) <> mrYes then
    Exit;
  DeleteLayout(Selected);
  RefreshList;
end;

/// Saving is part of managing: the arrangement you are looking at is the one worth
/// keeping, and naming it should not need a separate trip through the menu.
procedure TLayoutDialog.SaveAsClick(Sender: TObject);
var
  LName: string;
begin
  LName := Selected;
  if not InputQuery('Save layout', 'Name for the current arrangement', LName) then
    Exit;
  try
    SaveLayoutAs(Trim(LName));
    RefreshList;
    FList.ItemIndex := FList.Items.IndexOf(Trim(LName));
    UpdateButtons;
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TLayoutDialog.DefaultClick(Sender: TObject);
begin
  SetDefaultLayout(Selected);
  RefreshList;
end;

function TLayoutDialog.Execute: string;
begin
  FChosen := '';
  RefreshList;
  if ShowModal = mrOk then
    Result := FChosen
  else
    Result := '';
end;

end.
