unit uMainForm;

{
  Main window of the profiler GUI, laid out after AQTime (docs/GUI_DESIGN.md):

    Report      one row per method, the anchor of everything else
    Details     the immediate Parents and Children of the focused method
    Call Tree   the recursive tree, with the critical path in bold

  The controls are built in code rather than in a .dfm on purpose: the DevExpress
  composites (grid views, tree list columns) are a long, error-prone thing to write
  by hand as a resource, and this keeps the layout reviewable as ordinary code.
}

interface

uses
  System.SysUtils, System.Classes, System.Math, System.UITypes, System.Types,
  Winapi.Windows, Winapi.Messages,
  Vcl.Graphics, Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.ComCtrls,
  System.Variants, System.IOUtils, System.StrUtils, Data.DB, FireDAC.Comp.Client,
  cxGraphics, cxControls, cxLookAndFeels, cxLookAndFeelPainters, cxStyles, cxClasses,
  cxCustomData, cxFilter, cxData, cxDataStorage, cxEdit, cxNavigator, cxDataControllerConditionalFormattingRulesManagerDialog,
  cxGridLevel, cxGridCustomTableView, cxGridTableView, cxGridDBTableView, cxGridCustomView, cxGrid,
  cxProgressBar, cxTextEdit,
  cxTL, cxTLdxBarBuiltInMenu, cxInplaceContainer, cxTLData,
  SynEdit, SynEditHighlighter, SynHighlighterCS, SynEditTypes, SynFunc,
  uSessionStore, uControlClient, uSetupDialog;

type
  TMainForm = class(TForm)
  private
    FStore: TSessionStore;
    FToolbar: TPanel;
    FOpenButton: TButton;
    FRefreshButton: TButton;
    FInfoLabel: TLabel;
    FUnits: TComboBox;
    FSummaryTab: TTabSheet;
    FSummary: TMemo;
    FExplorer: TcxTreeList;
    FExplorerColumn: TcxTreeListColumn;
    FExplorerSplitter: TSplitter;
    FSessionsRoot: string;
    FPages: TPageControl;
    FReportTab: TTabSheet;
    FTreeTab: TTabSheet;
    FGraphTab: TTabSheet;
    FGraph: TPaintBox;
    FGraphScroll: TScrollBox;
    FGraphMethodId: Integer;
    FGraphCentre: string;
    FGraphCentreValue: Int64;
    FGraphParents: TNeighbours;
    FGraphChildren: TNeighbours;
    FGraphBoxes: TArray<TRect>;
    FGraphBoxIds: TArray<Integer>;
    FEditorTab: TTabSheet;
    FEditor: TSynEdit;
    FEditorHeader: TLabel;
    FEditorHighlighter: TSynCSSyn;
    FEditorFile: string;
    FEditorStart: Integer;
    FEditorEnd: Integer;
    FReportSplitter: TSplitter;
    FGrid: TcxGrid;
    FGridView: TcxGridDBTableView;
    FGridLevel: TcxGridLevel;
    FReportQuery: TFDQuery;
    FReportSource: TDataSource;
    FDetailsPanel: TPanel;
    FParentsPie: TPaintBox;
    FChildrenPie: TPaintBox;
    FParentsShares: TArray<Int64>;
    FChildrenShares: TArray<Int64>;
    FParentsGrid: TcxGrid;
    FParentsView: TcxGridTableView;
    FChildrenGrid: TcxGrid;
    FChildrenView: TcxGridTableView;
    FTree: TcxTreeList;
    FTreeName: TcxTreeListColumn;
    FTreeInclusive: TcxTreeListColumn;
    FTreeExclusive: TcxTreeListColumn;
    FTreeCalls: TcxTreeListColumn;
    FCriticalStyle: TcxStyle;
    FClient: TControlClient;
    FSessionId: string;
    FPoll: TTimer;
    FStartButton: TButton;
    FSnapshotButton: TButton;
    FPauseButton: TButton;
    FStopButton: TButton;
    FPaused: Boolean;
    FLog: TMemo;
    procedure BuildToolbar;
    procedure BuildExplorer;
    procedure BuildSummaryTab;
    procedure UpdateSummary;
    procedure UnitsChanged(Sender: TObject);
    procedure ReloadExplorer;
    procedure ExplorerDblClick(Sender: TObject);
    procedure BuildReportTab;
    procedure BuildTreeTab;
    procedure BuildGraphTab;
    procedure PaintGraph(Sender: TObject);
    procedure GraphMouseDown(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
    procedure ShowGraphOf(AMethodId: Integer);
    function DrawGraphBox(ACanvas: TCanvas; const ARect: TRect; const AName: string;
      AValue: Int64; AIsCentre: Boolean): TRect;
    procedure BuildEditorTab;
    procedure ShowSourceOf(AMethodId: Integer);
    procedure EditorSpecialLineColors(Sender: TObject; Line: TSynNativeInt;
      var Special: Boolean; var FG, BG: TColor);
    function BuildNeighbourGrid(AParent: TWinControl; AAlign: TAlign; const ACaption: string;
      out AView: TcxGridTableView): TcxGrid;
    procedure OpenButtonClick(Sender: TObject);
    procedure RefreshButtonClick(Sender: TObject);
    procedure ReportFocusChanged(Sender: TcxCustomGridTableView;
      APrevFocusedRecord, AFocusedRecord: TcxCustomGridRecord; ANewItemRecordFocusingChanged: Boolean);
    procedure TreeExpanding(Sender: TcxCustomTreeList; ANode: TcxTreeListNode; var Allow: Boolean);
    procedure TreeGetContentStyle(Sender: TcxCustomTreeList; AColumn: TcxTreeListColumn;
      ANode: TcxTreeListNode; var AStyle: TcxStyle);
    procedure LoadSession(const APath: string);
    procedure LoadReport;
    procedure LoadTreeRoots;
    procedure LoadTreeChildren(ANode: TcxTreeListNode; AParentId: Integer);
    procedure LoadDetails(AMethodId: Integer);
    procedure DressReportColumns;
    procedure StartButtonClick(Sender: TObject);
    procedure SnapshotButtonClick(Sender: TObject);
    procedure PauseButtonClick(Sender: TObject);
    procedure StopButtonClick(Sender: TObject);
    procedure PollTimer(Sender: TObject);
    procedure UpdateButtons(const AState: string);
    function EnsureService: Boolean;
    procedure ShowLog(const ALines: TArray<string>);
    procedure NanosecondDisplayText(Sender: TcxCustomGridTableItem;
      ARecord: TcxCustomGridRecord; var AText: string);
    procedure FillNeighbours(AView: TcxGridTableView; const AItems: TNeighbours);
    procedure PaintParentsPie(Sender: TObject);
    procedure PaintChildrenPie(Sender: TObject);
    procedure PaintShares(ACanvas: TCanvas; const ARect: TRect; const AShares: TArray<Int64>);
    function FocusedMethodId: Integer;
    procedure UpdateInfo;
    function ValueCaption: string;
  public
    constructor Create(AOwner: TComponent); override;
    destructor Destroy; override;
  end;

var
  MainForm: TMainForm;

implementation

const
  /// Node.Data marks the critical path: the heaviest child of its parent.
  DataCritical: Pointer = Pointer(1);

constructor TMainForm.Create(AOwner: TComponent);
var
  LTab: string;
  LIndex: Integer;
begin
  inherited CreateNew(AOwner);
  Caption := '.NET for Android profiler';
  Width := 1200;
  Height := 800;
  Position := poScreenCenter;
  FStore := TSessionStore.Create;

  FCriticalStyle := TcxStyle.Create(Self);
  FCriticalStyle.Font.Style := [fsBold];

  BuildToolbar;
  BuildExplorer;
  FPages := TPageControl.Create(Self);
  FPages.Parent := Self;
  FPages.Align := alClient;
  FReportTab := TTabSheet.Create(Self);
  FReportTab.PageControl := FPages;
  FReportTab.Caption := 'Report';
  FTreeTab := TTabSheet.Create(Self);
  FTreeTab.PageControl := FPages;
  FTreeTab.Caption := 'Call tree';
  FGraphTab := TTabSheet.Create(Self);
  FGraphTab.PageControl := FPages;
  FGraphTab.Caption := 'Call graph';
  FEditorTab := TTabSheet.Create(Self);
  FEditorTab.PageControl := FPages;
  FEditorTab.Caption := 'Source';
  FSummaryTab := TTabSheet.Create(Self);
  FSummaryTab.PageControl := FPages;
  FSummaryTab.Caption := 'Summary';
  BuildReportTab;
  BuildTreeTab;
  BuildGraphTab;
  BuildEditorTab;
  BuildSummaryTab;

  FLog := TMemo.Create(Self);
  FLog.Parent := Self;
  FLog.Align := alBottom;
  FLog.Height := 110;
  FLog.ScrollBars := ssVertical;
  FLog.ReadOnly := True;
  FLog.Font.Name := 'Consolas';

  FClient := TControlClient.Create;
  FPoll := TTimer.Create(Self);
  FPoll.Interval := 500;
  FPoll.Enabled := False;
  FPoll.OnTimer := PollTimer;

  if (ParamCount >= 1) and not ParamStr(1).StartsWith('--') then
    LoadSession(ParamStr(1));
  // --tab=<report|tree|source> selects the visible panel: handy for a screenshot or a
  // shortcut that always opens where you left off.
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--tab=', True) then
    begin
      LTab := ParamStr(LIndex).Substring(6);
      if SameText(LTab, 'tree') then FPages.ActivePage := FTreeTab
      else if SameText(LTab, 'graph') then FPages.ActivePage := FGraphTab
      else if SameText(LTab, 'source') then FPages.ActivePage := FEditorTab
      else if SameText(LTab, 'summary') then FPages.ActivePage := FSummaryTab
      else FPages.ActivePage := FReportTab;
    end;
  UpdateInfo;
end;

destructor TMainForm.Destroy;
begin
  FPoll.Enabled := False;
  FClient.Free;               // shuts the control service down with us
  FReportQuery.Free;
  FStore.Free;
  inherited Destroy;
end;

procedure TMainForm.BuildToolbar;
begin
  FToolbar := TPanel.Create(Self);
  FToolbar.Parent := Self;
  FToolbar.Align := alTop;
  FToolbar.Height := 40;
  FToolbar.BevelOuter := bvNone;

  FOpenButton := TButton.Create(Self);
  FOpenButton.Parent := FToolbar;
  FOpenButton.Left := 8;
  FOpenButton.Top := 8;
  FOpenButton.Width := 110;
  FOpenButton.Caption := 'Open session...';
  FOpenButton.OnClick := OpenButtonClick;

  FRefreshButton := TButton.Create(Self);
  FRefreshButton.Parent := FToolbar;
  FRefreshButton.Left := 126;
  FRefreshButton.Top := 8;
  FRefreshButton.Width := 90;
  FRefreshButton.Caption := 'Refresh';
  FRefreshButton.OnClick := RefreshButtonClick;

  FStartButton := TButton.Create(Self);
  FStartButton.Parent := FToolbar;
  FStartButton.SetBounds(232, 8, 110, 25);
  FStartButton.Caption := 'New session...';
  FStartButton.OnClick := StartButtonClick;

  FSnapshotButton := TButton.Create(Self);
  FSnapshotButton.Parent := FToolbar;
  FSnapshotButton.SetBounds(350, 8, 90, 25);
  FSnapshotButton.Caption := 'Snapshot';
  FSnapshotButton.Hint := 'Refresh the results from what has been collected so far, without stopping the app';
  FSnapshotButton.ShowHint := True;
  FSnapshotButton.OnClick := SnapshotButtonClick;

  FPauseButton := TButton.Create(Self);
  FPauseButton.Parent := FToolbar;
  FPauseButton.SetBounds(448, 8, 90, 25);
  FPauseButton.Caption := 'Pause';
  FPauseButton.Hint := 'Stop recording without stopping the app: the methods stay instrumented, so their overhead remains';
  FPauseButton.ShowHint := True;
  FPauseButton.OnClick := PauseButtonClick;

  FStopButton := TButton.Create(Self);
  FStopButton.Parent := FToolbar;
  FStopButton.SetBounds(546, 8, 90, 25);
  FStopButton.Caption := 'Stop';
  FStopButton.OnClick := StopButtonClick;

  FUnits := TComboBox.Create(Self);
  FUnits.Parent := FToolbar;
  FUnits.SetBounds(646, 9, 120, 24);
  FUnits.Style := csDropDownList;
  FUnits.Items.Add(TimeUnitName(tuAuto));
  FUnits.Items.Add(TimeUnitName(tuSeconds));
  FUnits.Items.Add(TimeUnitName(tuMilliseconds));
  FUnits.Items.Add(TimeUnitName(tuMicroseconds));
  FUnits.Items.Add(TimeUnitName(tuNanoseconds));
  FUnits.ItemIndex := 0;
  FUnits.OnChange := UnitsChanged;

  FInfoLabel := TLabel.Create(Self);
  FInfoLabel.Parent := FToolbar;
  FInfoLabel.Left := 780;
  FInfoLabel.Top := 12;
  FInfoLabel.Caption := 'No session open.';
  UpdateButtons('');
end;

/// AQTime's Explorer: the results you can open, and the categories inside the one that
/// is open. Double-clicking a session loads it.
procedure TMainForm.BuildExplorer;
begin
  FExplorer := TcxTreeList.Create(Self);
  FExplorer.Parent := Self;
  FExplorer.Align := alLeft;
  FExplorer.Width := 280;
  FExplorer.OptionsData.Editing := False;
  FExplorer.OptionsSelection.CellSelect := False;
  FExplorer.OptionsView.Headers := False;
  FExplorer.OptionsView.ShowRoot := True;
  FExplorer.OnDblClick := ExplorerDblClick;
  FExplorerColumn := FExplorer.CreateColumn;
  FExplorerColumn.Caption.Text := 'Results';
  FExplorerColumn.Width := 260;

  FExplorerSplitter := TSplitter.Create(Self);
  FExplorerSplitter.Parent := Self;
  FExplorerSplitter.Align := alLeft;
  FExplorerSplitter.Width := 4;
end;

procedure TMainForm.ReloadExplorer;
var
  LSessions: TSessionEntries;
  LRoot, LNode, LChild: TcxTreeListNode;
  I: Integer;
begin
  FExplorer.BeginUpdate;
  try
    FExplorer.Clear;
    LRoot := FExplorer.Add;
    LRoot.Values[0] := 'Sessions';
    LRoot.Data := nil;
    if FSessionsRoot = '' then
      Exit;
    LSessions := ListSessions(FSessionsRoot);
    for I := 0 to High(LSessions) do
    begin
      LNode := LRoot.AddChild;
      LNode.Values[0] := Format('%s  (%s)', [LSessions[I].Id, LSessions[I].Mode]);
      // The path travels with the node so a double-click knows what to open.
      LNode.Texts[0] := LNode.Texts[0];
      LNode.Data := Pointer(NativeInt(I));
      if SameText(LSessions[I].DatabasePath, FStore.Path) then
      begin
        // The open session shows the categories, like AQTime's Routines / Modules tree.
        LChild := LNode.AddChild;
        LChild.Values[0] := 'Routines';
        LChild := LNode.AddChild;
        LChild.Values[0] := 'Modules';
        LChild := LNode.AddChild;
        LChild.Values[0] := 'Threads';
        LNode.Expand(True);
      end;
    end;
    LRoot.Expand(False);
  finally
    FExplorer.EndUpdate;
  end;
end;

procedure TMainForm.ExplorerDblClick(Sender: TObject);
var
  LSessions: TSessionEntries;
  LIndex: Integer;
begin
  if (FExplorer.FocusedNode = nil) or (FExplorer.FocusedNode.Level <> 1) then
    Exit;
  LSessions := ListSessions(FSessionsRoot);
  LIndex := Integer(NativeInt(FExplorer.FocusedNode.Data));
  if (LIndex >= 0) and (LIndex <= High(LSessions)) then
    LoadSession(LSessions[LIndex].DatabasePath);
end;

procedure TMainForm.BuildReportTab;
begin
  FDetailsPanel := TPanel.Create(Self);
  FDetailsPanel.Parent := FReportTab;
  FDetailsPanel.Align := alBottom;
  FDetailsPanel.Height := 220;
  FDetailsPanel.BevelOuter := bvNone;

  FParentsGrid := BuildNeighbourGrid(FDetailsPanel, alLeft, 'Parents', FParentsView);
  FParentsGrid.Parent.Width := 560;
  FChildrenGrid := BuildNeighbourGrid(FDetailsPanel, alClient, 'Children', FChildrenView);

  FReportSplitter := TSplitter.Create(Self);
  FReportSplitter.Parent := FReportTab;
  FReportSplitter.Align := alBottom;
  FReportSplitter.Height := 4;

  FGrid := TcxGrid.Create(Self);
  FGrid.Parent := FReportTab;
  FGrid.Align := alClient;
  FGridLevel := FGrid.Levels.Add;
  FGridView := FGrid.CreateView(TcxGridDBTableView) as TcxGridDBTableView;
  FGridLevel.GridView := FGridView;
  FGridView.OptionsBehavior.CellHints := True;
  FGridView.OptionsSelection.CellSelect := False;
  FGridView.OptionsView.GroupByBox := True;
  FGridView.OptionsData.Editing := False;
  FGridView.OptionsData.Deleting := False;
  FGridView.OptionsData.Inserting := False;
  FGridView.OnFocusedRecordChanged := ReportFocusChanged;

  FReportSource := TDataSource.Create(Self);
  FGridView.DataController.DataSource := FReportSource;
end;

function TMainForm.BuildNeighbourGrid(AParent: TWinControl; AAlign: TAlign; const ACaption: string;
  out AView: TcxGridTableView): TcxGrid;
var
  LPanel: TPanel;
  LLabel: TLabel;
  LGrid: TcxGrid;
  LLevel: TcxGridLevel;
  LPie: TPaintBox;
begin
  LPanel := TPanel.Create(Self);
  LPanel.Parent := AParent;
  LPanel.Align := AAlign;
  LPanel.BevelOuter := bvNone;

  LLabel := TLabel.Create(Self);
  LLabel.Parent := LPanel;
  LLabel.Align := alTop;
  LLabel.Caption := '  ' + ACaption;
  LLabel.Font.Style := [fsBold];

  LPie := TPaintBox.Create(Self);
  LPie.Parent := LPanel;
  LPie.Align := alLeft;
  LPie.Width := 110;
  if AAlign = alLeft then
  begin
    FParentsPie := LPie;
    LPie.OnPaint := PaintParentsPie;
  end
  else
  begin
    FChildrenPie := LPie;
    LPie.OnPaint := PaintChildrenPie;
  end;

  LGrid := TcxGrid.Create(Self);
  LGrid.Parent := LPanel;
  LGrid.Align := alClient;
  LLevel := LGrid.Levels.Add;
  AView := LGrid.CreateView(TcxGridTableView) as TcxGridTableView;
  LLevel.GridView := AView;
  AView.OptionsData.Editing := False;
  AView.OptionsSelection.CellSelect := False;
  AView.CreateColumn.Caption := 'Method';
  AView.CreateColumn.Caption := 'Value';
  AView.Columns[0].Width := 380;
  Result := LGrid;
end;

procedure TMainForm.BuildTreeTab;
begin
  FTree := TcxTreeList.Create(Self);
  FTree.Parent := FTreeTab;
  FTree.Align := alClient;
  FTree.OptionsData.Editing := False;
  FTree.OptionsSelection.CellSelect := False;
  FTree.OptionsView.ColumnAutoWidth := False;
  FTree.OptionsBehavior.CellHints := True;
  FTree.OnExpanding := TreeExpanding;
  FTree.Styles.OnGetContentStyle := TreeGetContentStyle;

  FTreeName := FTree.CreateColumn;
  FTreeName.Caption.Text := 'Method';
  FTreeName.Width := 620;
  FTreeInclusive := FTree.CreateColumn;
  FTreeInclusive.Caption.Text := 'Inclusive';
  FTreeInclusive.Width := 140;
  FTreeExclusive := FTree.CreateColumn;
  FTreeExclusive.Caption.Text := 'Self';
  FTreeExclusive.Width := 140;
  FTreeCalls := FTree.CreateColumn;
  FTreeCalls.Caption.Text := 'Calls';
  FTreeCalls.Width := 100;
end;

procedure TMainForm.BuildGraphTab;
begin
  FGraphScroll := TScrollBox.Create(Self);
  FGraphScroll.Parent := FGraphTab;
  FGraphScroll.Align := alClient;
  FGraphScroll.Color := clWindow;
  FGraphScroll.ParentColor := False;

  FGraph := TPaintBox.Create(Self);
  FGraph.Parent := FGraphScroll;
  FGraph.SetBounds(0, 0, 1200, 700);
  FGraph.OnPaint := PaintGraph;
  FGraph.OnMouseDown := GraphMouseDown;
  FGraphMethodId := -1;
end;

/// One box per method: the name on top, the metric underneath, exactly the shape AQTime
/// draws. Callers sit above the focused method, callees below, arrows follow the calls.
function TMainForm.DrawGraphBox(ACanvas: TCanvas; const ARect: TRect; const AName: string;
  AValue: Int64; AIsCentre: Boolean): TRect;
var
  LText: string;
  LTextRect: TRect;
begin
  Result := ARect;
  if AIsCentre then
  begin
    ACanvas.Brush.Color := $00F0E0C0;
    ACanvas.Pen.Width := 2;
  end
  else
  begin
    ACanvas.Brush.Color := $00F8F8F8;
    ACanvas.Pen.Width := 1;
  end;
  ACanvas.Pen.Color := $00808080;
  ACanvas.Rectangle(Result);
  ACanvas.Pen.Width := 1;

  ACanvas.Brush.Style := bsClear;
  LTextRect := Rect(Result.Left + 6, Result.Top + 4, Result.Right - 6, Result.Top + 22);
  ACanvas.Font.Style := [fsBold];
  // The full name never fits: keep the tail, which is the type and the method.
  LText := AName;
  if Length(LText) > 46 then
    LText := '...' + Copy(LText, Length(LText) - 43, 44);
  ACanvas.TextRect(LTextRect, LText, [tfEndEllipsis]);

  ACanvas.Font.Style := [];
  LTextRect := Rect(Result.Left + 6, Result.Top + 24, Result.Right - 6, Result.Bottom - 4);
  if FStore.Mode = smInstrumenting then
    LText := 'Time with children: ' + FormatNs(AValue)
  else
    LText := Format('%d samples', [AValue]);
  ACanvas.TextRect(LTextRect, LText, [tfEndEllipsis]);
  ACanvas.Brush.Style := bsSolid;
end;

procedure TMainForm.PaintGraph(Sender: TObject);
const
  BoxWidth = 330;
  BoxHeight = 48;
  Gap = 26;
var
  LCanvas: TCanvas;
  LCentreRect, LRect: TRect;
  I, LRow, LLeft, LCentreY: Integer;
  LTotal: Int64;
begin
  LCanvas := FGraph.Canvas;
  LCanvas.Brush.Color := clWindow;
  LCanvas.FillRect(FGraph.ClientRect);
  SetLength(FGraphBoxes, 0);
  SetLength(FGraphBoxIds, 0);
  if FGraphMethodId < 0 then
  begin
    LCanvas.Brush.Style := bsClear;
    LCanvas.TextOut(16, 16, 'Pick a method in the Report to see who calls it and what it calls.');
    LCanvas.Brush.Style := bsSolid;
    Exit;
  end;

  LCentreY := 40 + BoxHeight + 60;
  LCentreRect := Rect(40, LCentreY, 40 + BoxWidth, LCentreY + BoxHeight);

  // Callers, in a row above.
  LRow := 40;
  for I := 0 to High(FGraphParents) do
  begin
    LLeft := 40 + I * (BoxWidth + Gap);
    LRect := Rect(LLeft, LRow, LLeft + BoxWidth, LRow + BoxHeight);
    DrawGraphBox(LCanvas, LRect, FGraphParents[I].FullName, FGraphParents[I].Value, False);
    SetLength(FGraphBoxes, Length(FGraphBoxes) + 1);
    SetLength(FGraphBoxIds, Length(FGraphBoxIds) + 1);
    FGraphBoxes[High(FGraphBoxes)] := LRect;
    FGraphBoxIds[High(FGraphBoxIds)] := FGraphParents[I].MethodId;
    LCanvas.Pen.Color := $00A0A0A0;
    LCanvas.MoveTo(LRect.CenterPoint.X, LRect.Bottom);
    LCanvas.LineTo(LCentreRect.CenterPoint.X, LCentreRect.Top);
  end;

  DrawGraphBox(LCanvas, LCentreRect, FGraphCentre, FGraphCentreValue, True);

  // Callees, in a row below, with the share of the focused method's time on the arrow.
  LTotal := 0;
  for I := 0 to High(FGraphChildren) do
    Inc(LTotal, FGraphChildren[I].Value);
  LRow := LCentreY + BoxHeight + 60;
  for I := 0 to High(FGraphChildren) do
  begin
    LLeft := 40 + I * (BoxWidth + Gap);
    LRect := Rect(LLeft, LRow, LLeft + BoxWidth, LRow + BoxHeight);
    DrawGraphBox(LCanvas, LRect, FGraphChildren[I].FullName, FGraphChildren[I].Value, False);
    SetLength(FGraphBoxes, Length(FGraphBoxes) + 1);
    SetLength(FGraphBoxIds, Length(FGraphBoxIds) + 1);
    FGraphBoxes[High(FGraphBoxes)] := LRect;
    FGraphBoxIds[High(FGraphBoxIds)] := FGraphChildren[I].MethodId;
    LCanvas.Pen.Color := $00A0A0A0;
    LCanvas.MoveTo(LCentreRect.CenterPoint.X, LCentreRect.Bottom);
    LCanvas.LineTo(LRect.CenterPoint.X, LRect.Top);
    if LTotal > 0 then
    begin
      LCanvas.Brush.Style := bsClear;
      LCanvas.TextOut(LRect.CenterPoint.X - 12, LRect.Top - 18,
        Format('%.0f%%', [100.0 * FGraphChildren[I].Value / LTotal]));
      LCanvas.Brush.Style := bsSolid;
    end;
  end;

  FGraph.Width := Max(FGraphScroll.ClientWidth,
    40 + (Max(Length(FGraphParents), Length(FGraphChildren)) + 1) * (BoxWidth + Gap));
  FGraph.Height := Max(FGraphScroll.ClientHeight, LRow + BoxHeight + 40);
end;

/// Clicking a box walks the graph, which is the whole point of having one.
procedure TMainForm.GraphMouseDown(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
var
  I: Integer;
begin
  for I := 0 to High(FGraphBoxes) do
    if FGraphBoxes[I].Contains(Point(X, Y)) then
    begin
      ShowGraphOf(FGraphBoxIds[I]);
      Exit;
    end;
end;

procedure TMainForm.ShowGraphOf(AMethodId: Integer);
begin
  FGraphMethodId := AMethodId;
  if (AMethodId < 0) or not FStore.IsOpen then
  begin
    FGraphCentre := '';
    FGraphCentreValue := 0;
    FGraphParents := nil;
    FGraphChildren := nil;
  end
  else
  begin
    FGraphCentre := FStore.MethodName(AMethodId);
    FGraphCentreValue := FStore.MethodInclusive(AMethodId);
    FGraphParents := FStore.Parents(AMethodId);
    FGraphChildren := FStore.Children(AMethodId);
    // A wide fan is unreadable and slow to draw: the tail is in the Details table.
    if Length(FGraphParents) > 6 then SetLength(FGraphParents, 6);
    if Length(FGraphChildren) > 6 then SetLength(FGraphChildren, 6);
  end;
  if FGraph <> nil then
    FGraph.Invalidate;
end;

procedure TMainForm.BuildEditorTab;
begin
  FEditorHeader := TLabel.Create(Self);
  FEditorHeader.Parent := FEditorTab;
  FEditorHeader.Align := alTop;
  FEditorHeader.Caption := ' Pick a method in the Report to see its source.';

  FEditor := TSynEdit.Create(Self);
  FEditor.Parent := FEditorTab;
  FEditor.Align := alClient;
  FEditor.ReadOnly := True;
  FEditor.Gutter.ShowLineNumbers := True;
  FEditor.Font.Name := 'Consolas';
  FEditor.Font.Size := 10;
  FEditor.OnSpecialLineColors := EditorSpecialLineColors;
  FEditorHighlighter := TSynCSSyn.Create(Self);
  FEditor.Highlighter := FEditorHighlighter;
end;

/// The profiled range is painted rather than annotated per line: MonoVM gives no per-line
/// samples, so the honest thing to show is "this method, these figures, these lines".
procedure TMainForm.EditorSpecialLineColors(Sender: TObject; Line: TSynNativeInt;
  var Special: Boolean; var FG, BG: TColor);
begin
  if (FEditorStart > 0) and (Line >= FEditorStart) and (Line <= FEditorEnd) then
  begin
    Special := True;
    FG := clWindowText;
    BG := $00E8F4FF;      // a pale wash over the method the Report is pointing at
  end;
end;

procedure TMainForm.ShowSourceOf(AMethodId: Integer);
var
  LSource: TMethodSource;
begin
  FEditorStart := 0;
  FEditorEnd := 0;
  if (AMethodId < 0) or not FStore.IsOpen then
  begin
    FEditor.Lines.Clear;
    FEditorHeader.Caption := ' Pick a method in the Report to see its source.';
    Exit;
  end;
  LSource := FStore.MethodSource(AMethodId);
  if not LSource.Found then
  begin
    FEditor.Lines.Clear;
    FEditorHeader.Caption := Format(' %s: no source location. Run the session with a symbols directory (the app''s bin folder).',
      [FStore.MethodName(AMethodId)]);
    Exit;
  end;
  if not FileExists(LSource.FileName) then
  begin
    FEditor.Lines.Clear;
    FEditorHeader.Caption := Format(' %s is at %s:%d, but that file is not on this machine.',
      [FStore.MethodName(AMethodId), LSource.FileName, LSource.StartLine]);
    Exit;
  end;
  if not SameText(FEditorFile, LSource.FileName) then
  begin
    FEditor.Lines.LoadFromFile(LSource.FileName);
    FEditorFile := LSource.FileName;
  end;
  FEditorStart := LSource.StartLine;
  FEditorEnd := LSource.EndLine;
  FEditorHeader.Caption := Format(' %s   -   %s (%d-%d)',
    [FStore.MethodName(AMethodId), LSource.FileName, LSource.StartLine, LSource.EndLine]);
  FEditor.CaretY := LSource.StartLine;
  FEditor.TopLine := Max(1, LSource.StartLine - 5);
  FEditor.Invalidate;
end;

procedure TMainForm.BuildSummaryTab;
begin
  FSummary := TMemo.Create(Self);
  FSummary.Parent := FSummaryTab;
  FSummary.Align := alClient;
  FSummary.ReadOnly := True;
  FSummary.ScrollBars := ssBoth;
  FSummary.WordWrap := False;
  FSummary.Font.Name := 'Consolas';
end;

/// AQTime's Summary is a set of answers, not a table: what this run was, what it warns
/// about, and the handful of methods and types worth looking at first.
procedure TMainForm.UpdateSummary;
var
  LQuery: TFDQuery;
  LLines: TStringList;
  LSegments: TArray<TSegment>;
  I, LCount: Integer;
begin
  if FSummary = nil then
    Exit;
  LLines := TStringList.Create;
  try
    if not FStore.IsOpen then
    begin
      LLines.Add('No session open.');
      FSummary.Lines.Assign(LLines);
      Exit;
    end;
    LLines.Add(Format('%s session of %s on %s', [ModeToString(FStore.Mode), FStore.Package, FStore.Device]));
    LLines.Add(Format('state %s, started %s', [FStore.State, FStore.StartedUtc]));
    LLines.Add(Format('database %s (schema v%d)', [FStore.Path, FStore.SchemaVersion]));
    if FStore.Mode = smSampling then
      LLines.Add(Format('%d samples', [FStore.TotalSamples]));
    LSegments := FStore.Segments;
    if Length(LSegments) > 0 then
      LLines.Add(Format('%d result refreshes, last one "%s"', [Length(LSegments), LSegments[High(LSegments)].Kind]));
    LLines.Add('');

    LLines.Add(IfThen(FStore.Mode = smInstrumenting, 'Heaviest methods (self time)', 'Heaviest methods (self samples, CPU)'));
    LQuery := FStore.OpenReport;
    try
      LCount := 0;
      while not LQuery.Eof and (LCount < 10) do
      begin
        if FStore.Mode = smInstrumenting then
          LLines.Add(Format('  %-58s %12s  %8d calls',
            [LQuery.FieldByName('full_name').AsString,
             FormatNs(LQuery.FieldByName('self_ns').AsLargeInt),
             LQuery.FieldByName('calls').AsInteger]))
        else if FStore.Mode = smSampling then
          LLines.Add(Format('  %-58s %8d self  %8d incl',
            [LQuery.FieldByName('full_name').AsString,
             LQuery.FieldByName('self_samples').AsInteger,
             LQuery.FieldByName('total_samples').AsInteger]))
        else
          LLines.Add(Format('  %-58s %10d objects %12d bytes',
            [LQuery.FieldByName('full_name').AsString,
             LQuery.FieldByName('count').AsLargeInt,
             LQuery.FieldByName('bytes').AsLargeInt]));
        Inc(LCount);
        LQuery.Next;
      end;
    finally
      LQuery.Free;
    end;

    if FStore.CountOf('alloc_by_type') > 0 then
    begin
      LLines.Add('');
      LLines.Add('Most allocated types');
      LQuery := FStore.OpenAllocationsByType;
      try
        LCount := 0;
        while not LQuery.Eof and (LCount < 10) do
        begin
          LLines.Add(Format('  %-58s %10d objects', [LQuery.FieldByName('type_name').AsString,
            LQuery.FieldByName('count').AsLargeInt]));
          Inc(LCount);
          LQuery.Next;
        end;
      finally
        LQuery.Free;
      end;
    end;

    LLines.Add('');
    LLines.Add(Format('methods %d, threads %d, tree nodes %d',
      [FStore.CountOf('method'), FStore.CountOf('thread'),
       FStore.CountOf(IfThen(FStore.Mode = smInstrumenting, 'timing_tree', 'sample_tree'))]));
    for I := 0 to High(LSegments) do
      LLines.Add(Format('  segment %d: %s at %s (%d events)',
        [LSegments[I].Id, LSegments[I].Kind, LSegments[I].TakenUtc, LSegments[I].Events]));
    FSummary.Lines.Assign(LLines);
  finally
    LLines.Free;
  end;
end;

procedure TMainForm.UnitsChanged(Sender: TObject);
begin
  GTimeUnit := TTimeUnit(FUnits.ItemIndex);
  // Everything shows times: repaint the lot rather than guess which panel is visible.
  FGridView.LayoutChanged;
  FTree.Invalidate;
  FGraph.Invalidate;
  LoadTreeRoots;
  UpdateSummary;
end;

procedure TMainForm.OpenButtonClick(Sender: TObject);
var
  LDialog: TOpenDialog;
begin
  LDialog := TOpenDialog.Create(Self);
  try
    LDialog.Title := 'Open a profiling session';
    LDialog.Filter := 'Session database (session.db)|session.db|SQLite databases (*.db)|*.db';
    if LDialog.Execute then
      LoadSession(LDialog.FileName);
  finally
    LDialog.Free;
  end;
end;

procedure TMainForm.RefreshButtonClick(Sender: TObject);
begin
  if not FStore.IsOpen then
    Exit;
  // A running session rewrites these tables on every snapshot, so refreshing means
  // re-reading everything rather than re-querying deltas.
  FStore.Refresh;
  LoadReport;
  LoadTreeRoots;
  UpdateInfo;
end;

procedure TMainForm.LoadSession(const APath: string);
begin
  FStore.Open(APath);
  FSessionsRoot := TDirectory.GetParent(TDirectory.GetParent(APath));
  ReloadExplorer;
  UpdateSummary;
  LoadReport;
  LoadTreeRoots;
  UpdateInfo;
end;

procedure TMainForm.LoadReport;
begin
  FReportSource.DataSet := nil;
  FreeAndNil(FReportQuery);
  FGridView.ClearItems;
  if not FStore.IsOpen then
    Exit;
  FReportQuery := FStore.OpenReport;
  FReportSource.DataSet := FReportQuery;
  FGridView.DataController.CreateAllItems;
  DressReportColumns;
  LoadDetails(FocusedMethodId);
end;

/// Column captions and units: the database speaks in raw nanoseconds and sample counts,
/// the Report panel should not.
procedure TMainForm.DressReportColumns;
var
  I: Integer;
  LColumn: TcxGridDBColumn;
  LField: string;
begin
  for I := 0 to FGridView.ColumnCount - 1 do
  begin
    LColumn := FGridView.Columns[I];
    LField := LowerCase(LColumn.DataBinding.FieldName);
    if LField = 'method_id' then
    begin
      LColumn.Visible := False;                 // plumbing for the Details panel
      Continue;
    end;
    if LField.EndsWith('_ns') then
      LColumn.OnGetDisplayText := NanosecondDisplayText;
    if LField.StartsWith('pct_') then
    begin
      // A number tells you how much; a bar tells you which rows matter. AQTime shows
      // both, and so do we: the bar is the column, the value is its text.
      LColumn.PropertiesClass := TcxProgressBarProperties;
      with TcxProgressBarProperties(LColumn.Properties) do
      begin
        Min := 0;
        Max := 100;
        ShowText := True;
        BeginColor := $00E0A860;
        EndColor := $004AA3FF;
        SolidTextColor := True;
      end;
      LColumn.Width := 120;
    end;
    if LField = 'full_name' then
    begin
      LColumn.Caption := 'Method';
      LColumn.Width := 460;
    end
    else if LField = 'module' then
      LColumn.Caption := 'Module'
    else if LField = 'calls' then
      LColumn.Caption := 'Calls'
    else if LField = 'self_ns' then
      LColumn.Caption := 'Self time'
    else if LField = 'total_ns' then
      LColumn.Caption := 'Time with children'
    else if LField = 'min_ns' then
      LColumn.Caption := 'Min'
    else if LField = 'max_ns' then
      LColumn.Caption := 'Max'
    else if LField = 'avg_ns' then
      LColumn.Caption := 'Average'
    else if LField = 'pct_self' then
      LColumn.Caption := '% self'
    else if LField = 'pct_total' then
      LColumn.Caption := '% with children'
    else if LField = 'exception_leaves' then
      LColumn.Caption := 'Exception exits'
    else if LField = 'self_samples' then
      LColumn.Caption := 'Samples (self, CPU)'
    else if LField = 'total_samples' then
      LColumn.Caption := 'Samples (with children, CPU)'
    else if LField = 'self_wall' then
      LColumn.Caption := 'Samples (self, wall)'
    else if LField = 'total_wall' then
      LColumn.Caption := 'Samples (with children, wall)'
    else if LField = 'count' then
      LColumn.Caption := 'Objects'
    else if LField = 'bytes' then
      LColumn.Caption := 'Bytes';
  end;
end;

procedure TMainForm.NanosecondDisplayText(Sender: TcxCustomGridTableItem;
  ARecord: TcxCustomGridRecord; var AText: string);
var
  LValue: Variant;
begin
  LValue := ARecord.Values[Sender.Index];
  if VarIsNull(LValue) then
    AText := ''
  else
    AText := FormatNs(LValue);
end;

procedure TMainForm.LoadTreeRoots;
var
  LNodes: TTreeNodes;
begin
  FTree.Clear;
  if not FStore.IsOpen then
    Exit;
  if FStore.Mode = smHeapSnapshot then
    Exit;
  FTreeCalls.Visible := FStore.Mode = smInstrumenting;
  FTreeInclusive.Caption.Text := ValueCaption;
  LNodes := FStore.TreeChildren(-1);
  FTree.BeginUpdate;
  try
    LoadTreeChildren(nil, -1);
  finally
    FTree.EndUpdate;
  end;
  if FTree.Root.Count > 0 then
    FTree.Root.Items[0].Expand(False);
end;

procedure TMainForm.LoadTreeChildren(ANode: TcxTreeListNode; AParentId: Integer);
var
  LNodes: TTreeNodes;
  LChild: TcxTreeListNode;
  LBest: Int64;
  I: Integer;
begin
  LNodes := FStore.TreeChildren(AParentId);
  LBest := 0;
  for I := 0 to High(LNodes) do
    LBest := Max(LBest, LNodes[I].Inclusive);

  for I := 0 to High(LNodes) do
  begin
    if ANode = nil then
      LChild := FTree.Add
    else
      LChild := ANode.AddChild;
    LChild.Values[0] := LNodes[I].FullName;
    if FStore.Mode = smInstrumenting then
    begin
      LChild.Values[1] := FormatNs(LNodes[I].Inclusive);
      LChild.Values[2] := FormatNs(LNodes[I].Exclusive);
      LChild.Values[3] := LNodes[I].Calls;
    end
    else
    begin
      LChild.Values[1] := LNodes[I].Inclusive;
      LChild.Values[2] := LNodes[I].Exclusive;
    end;
    // The tree node id travels in Data so expansion can ask for its children, and
    // the critical path (the heaviest sibling) is marked for the bold style.
    LChild.Data := Pointer(NativeInt(LNodes[I].Id));
    if (LBest > 0) and (LNodes[I].Inclusive = LBest) then
      LChild.ImageIndex := 1
    else
      LChild.ImageIndex := 0;
    LChild.HasChildren := LNodes[I].HasChildren;
  end;
end;

procedure TMainForm.TreeExpanding(Sender: TcxCustomTreeList; ANode: TcxTreeListNode; var Allow: Boolean);
begin
  Allow := True;
  // Lazy: a node's children are read the first time it is opened. The tree can hold
  // hundreds of thousands of nodes, so loading it whole is not an option.
  if (ANode <> nil) and (ANode.Count = 0) and ANode.HasChildren then
  begin
    FTree.BeginUpdate;
    try
      LoadTreeChildren(ANode, Integer(NativeInt(ANode.Data)));
    finally
      FTree.EndUpdate;
    end;
  end;
end;

procedure TMainForm.TreeGetContentStyle(Sender: TcxCustomTreeList; AColumn: TcxTreeListColumn;
  ANode: TcxTreeListNode; var AStyle: TcxStyle);
begin
  if (ANode <> nil) and (ANode.ImageIndex = 1) then
    AStyle := FCriticalStyle;
end;

procedure TMainForm.ReportFocusChanged(Sender: TcxCustomGridTableView;
  APrevFocusedRecord, AFocusedRecord: TcxCustomGridRecord; ANewItemRecordFocusingChanged: Boolean);
begin
  LoadDetails(FocusedMethodId);
end;

function TMainForm.FocusedMethodId: Integer;
begin
  Result := -1;
  if (FReportQuery = nil) or not FReportQuery.Active then
    Exit;
  if FReportQuery.FindField('method_id') = nil then
    Exit;
  Result := FReportQuery.FieldByName('method_id').AsInteger;
end;

procedure TMainForm.LoadDetails(AMethodId: Integer);
begin
  if (AMethodId < 0) or not FStore.IsOpen then
  begin
    FillNeighbours(FParentsView, nil);
    FillNeighbours(FChildrenView, nil);
    ShowSourceOf(-1);
    ShowGraphOf(-1);
    Exit;
  end;
  FillNeighbours(FParentsView, FStore.Parents(AMethodId));
  FillNeighbours(FChildrenView, FStore.Children(AMethodId));
  ShowSourceOf(AMethodId);
  ShowGraphOf(AMethodId);
end;

/// The share each caller (or callee) has of the focused method's time: the pie is the
/// same numbers as the table beside it, read at a glance.
procedure TMainForm.PaintShares(ACanvas: TCanvas; const ARect: TRect; const AShares: TArray<Int64>);
const
  Palette: array[0..5] of TColor = ($004040FF, $0040C040, $0000D0FF, $00FF8040, $00C040C0, $00909090);
var
  LTotal, LRunning: Int64;
  LSize, LLeft, LTop: Integer;
  LStartAngle, LSweep: Double;
  I: Integer;
  LSquare: TRect;

  function PointOnCircle(AAngleDegrees: Double): TPoint;
  begin
    Result.X := LSquare.CenterPoint.X + Round(LSize / 2 * Cos(AAngleDegrees * Pi / 180));
    Result.Y := LSquare.CenterPoint.Y - Round(LSize / 2 * Sin(AAngleDegrees * Pi / 180));
  end;

begin
  ACanvas.Brush.Color := clWindow;
  ACanvas.FillRect(ARect);
  LTotal := 0;
  for I := 0 to High(AShares) do
    Inc(LTotal, AShares[I]);
  if LTotal <= 0 then
    Exit;

  LSize := Min(ARect.Width, ARect.Height) - 16;
  if LSize < 20 then
    Exit;
  LLeft := ARect.Left + (ARect.Width - LSize) div 2;
  LTop := ARect.Top + (ARect.Height - LSize) div 2;
  LSquare := Rect(LLeft, LTop, LLeft + LSize, LTop + LSize);

  LRunning := 0;
  for I := 0 to High(AShares) do
  begin
    if AShares[I] <= 0 then
      Continue;
    LStartAngle := 90 - 360 * (LRunning / LTotal);
    LSweep := 360 * (AShares[I] / LTotal);
    Inc(LRunning, AShares[I]);
    ACanvas.Brush.Color := Palette[I mod Length(Palette)];
    ACanvas.Pen.Color := clWhite;
    // A single slice covering everything must still be drawn: Pie with equal start and
    // end points draws nothing, so fill the whole circle instead.
    if LSweep >= 359.9 then
      ACanvas.Ellipse(LSquare)
    else
      ACanvas.Pie(LSquare.Left, LSquare.Top, LSquare.Right, LSquare.Bottom,
        PointOnCircle(LStartAngle).X, PointOnCircle(LStartAngle).Y,
        PointOnCircle(LStartAngle - LSweep).X, PointOnCircle(LStartAngle - LSweep).Y);
  end;
end;

procedure TMainForm.PaintParentsPie(Sender: TObject);
begin
  PaintShares(FParentsPie.Canvas, FParentsPie.ClientRect, FParentsShares);
end;

procedure TMainForm.PaintChildrenPie(Sender: TObject);
begin
  PaintShares(FChildrenPie.Canvas, FChildrenPie.ClientRect, FChildrenShares);
end;

procedure TMainForm.FillNeighbours(AView: TcxGridTableView; const AItems: TNeighbours);
var
  I: Integer;
begin
  // Feed the pie beside this table with the same values.
  if AView = FParentsView then
    SetLength(FParentsShares, Length(AItems))
  else
    SetLength(FChildrenShares, Length(AItems));
  for I := 0 to High(AItems) do
    if AView = FParentsView then
      FParentsShares[I] := AItems[I].Value
    else
      FChildrenShares[I] := AItems[I].Value;
  if FParentsPie <> nil then FParentsPie.Invalidate;
  if FChildrenPie <> nil then FChildrenPie.Invalidate;

  AView.BeginUpdate;
  try
    AView.DataController.RecordCount := Length(AItems);
    for I := 0 to High(AItems) do
    begin
      AView.DataController.Values[I, 0] := AItems[I].FullName;
      if FStore.Mode = smInstrumenting then
        AView.DataController.Values[I, 1] := FormatNs(AItems[I].Value)
      else
        AView.DataController.Values[I, 1] := AItems[I].Value;
    end;
  finally
    AView.EndUpdate;
  end;
end;

function TMainForm.ValueCaption: string;
begin
  if FStore.Mode = smInstrumenting then
    Result := 'Total time'
  else
    Result := 'Samples';
end;

procedure TMainForm.UpdateInfo;
var
  LSegments: TArray<TSegment>;
  LText: string;
begin
  if not FStore.IsOpen then
  begin
    FInfoLabel.Caption := 'No session open. Use "Open session..." and pick a session.db.';
    Exit;
  end;
  LText := Format('%s  |  %s on %s  |  state %s', [ModeToString(FStore.Mode), FStore.Package, FStore.Device, FStore.State]);
  if FStore.Mode = smSampling then
    LText := LText + Format('  |  %d samples', [FStore.TotalSamples]);
  LSegments := FStore.Segments;
  if Length(LSegments) > 0 then
    LText := LText + Format('  |  %d refreshes, last %s', [Length(LSegments), LSegments[High(LSegments)].Kind]);
  FInfoLabel.Caption := LText;
end;


// ---------------------------------------------------------------- live control

/// The GUI owns the control service: nap.exe next to us, or the development build.
function TMainForm.EnsureService: Boolean;
var
  LCandidates: TArray<string>;
  LPath: string;
begin
  if FClient.IsConnected then
    Exit(True);
  LCandidates := [
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), 'nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Debug\net10.0\nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Release\net10.0\nap.exe')];
  for LPath in LCandidates do
    if TFile.Exists(LPath) then
    begin
      try
        FClient.StartService(TPath.GetFullPath(LPath));
        FLog.Lines.Add('control service: ' + FClient.BaseUrl);
        Exit(True);
      except
        on E: Exception do
        begin
          MessageDlg('Cannot start the control service:' + sLineBreak + E.Message, mtError, [mbOK], 0);
          Exit(False);
        end;
      end;
    end;
  MessageDlg('nap.exe was not found next to this application.' + sLineBreak +
    'Build it (dotnet build src\NetAndroidProfiler.Cli) or copy it here.', mtError, [mbOK], 0);
  Result := False;
end;

procedure TMainForm.StartButtonClick(Sender: TObject);
var
  LDialog: TSetupDialog;
  LSetup: TSetupResult;
  LStatus: TSessionStatus;
begin
  if not EnsureService then
    Exit;
  LDialog := TSetupDialog.Create(Self, FClient);
  try
    if not LDialog.Execute(LSetup) then
      Exit;
  finally
    LDialog.Free;
  end;
  try
    LStatus := FClient.StartSession(LSetup.DeviceSerial, LSetup.Package, LSetup.Mode,
      LSetup.Engine, LSetup.Callspec, LSetup.DurationSeconds, LSetup.SymbolsDir);
  except
    on E: Exception do
    begin
      MessageDlg(E.Message, mtError, [mbOK], 0);
      Exit;
    end;
  end;
  FSessionId := LStatus.Id;
  FPaused := False;
  FLog.Lines.Add('session ' + FSessionId + ' started');
  UpdateButtons(LStatus.State);
  FPoll.Enabled := True;
end;

procedure TMainForm.PollTimer(Sender: TObject);
var
  LStatus: TSessionStatus;
begin
  if FSessionId = '' then
  begin
    FPoll.Enabled := False;
    Exit;
  end;
  try
    LStatus := FClient.Status(FSessionId);
  except
    on E: Exception do
    begin
      FPoll.Enabled := False;
      FLog.Lines.Add('status failed: ' + E.Message);
      Exit;
    end;
  end;
  ShowLog(LStatus.Log);
  UpdateButtons(LStatus.State);
  FInfoLabel.Caption := Format('session %s: %s', [FSessionId, LStatus.State]);
  if (LStatus.State = 'Ready') or (LStatus.State = 'Failed') then
  begin
    FPoll.Enabled := False;
    if LStatus.Error <> '' then
      MessageDlg(LStatus.Error, mtError, [mbOK], 0);
    if (LStatus.DatabasePath <> '') and TFile.Exists(LStatus.DatabasePath) then
      LoadSession(LStatus.DatabasePath);
  end;
end;

procedure TMainForm.SnapshotButtonClick(Sender: TObject);
var
  LSegment: Integer;
  LStatus: TSessionStatus;
begin
  if FSessionId = '' then
    Exit;
  try
    LSegment := FClient.Snapshot(FSessionId);
    LStatus := FClient.Status(FSessionId);
    FLog.Lines.Add(Format('snapshot %d', [LSegment]));
    // Results are cumulative: the tables were rewritten with everything collected so
    // far, so this is a plain reload rather than a merge.
    if (LStatus.DatabasePath <> '') and TFile.Exists(LStatus.DatabasePath) then
      LoadSession(LStatus.DatabasePath);
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.PauseButtonClick(Sender: TObject);
begin
  if FSessionId = '' then
    Exit;
  try
    if FPaused then
    begin
      FClient.Resume(FSessionId);
      FPaused := False;
      FLog.Lines.Add('resumed');
    end
    else
    begin
      FClient.Pause(FSessionId);
      FPaused := True;
      FLog.Lines.Add('paused - the methods stay instrumented, so the overhead remains');
    end;
    FPauseButton.Caption := IfThen(FPaused, 'Resume', 'Pause');
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.StopButtonClick(Sender: TObject);
begin
  if FSessionId = '' then
    Exit;
  try
    FClient.Stop(FSessionId);
    FLog.Lines.Add('stopping...');
    FPoll.Enabled := True;         // the analysis runs after collection ends
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.UpdateButtons(const AState: string);
var
  LRunning: Boolean;
begin
  LRunning := (FSessionId <> '') and
    ((AState = 'Collecting') or (AState = 'WaitingForApp') or (AState = 'Preparing'));
  FSnapshotButton.Enabled := LRunning;
  FPauseButton.Enabled := LRunning;
  FStopButton.Enabled := LRunning;
  if not LRunning then
  begin
    FPaused := False;
    FPauseButton.Caption := 'Pause';
  end;
end;

procedure TMainForm.ShowLog(const ALines: TArray<string>);
var
  I: Integer;
begin
  // The service answers with the tail of the log; show what is new, not the whole tail.
  for I := 0 to High(ALines) do
    if FLog.Lines.IndexOf(ALines[I]) < 0 then
      FLog.Lines.Add(ALines[I]);
  while FLog.Lines.Count > 500 do
    FLog.Lines.Delete(0);
end;

end.
