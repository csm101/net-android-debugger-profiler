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
  System.SysUtils, System.Classes, System.Math, System.UITypes,
  Winapi.Windows, Winapi.Messages,
  Vcl.Graphics, Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.ComCtrls,
  System.Variants, System.IOUtils, System.StrUtils, Data.DB, FireDAC.Comp.Client,
  cxGraphics, cxControls, cxLookAndFeels, cxLookAndFeelPainters, cxStyles, cxClasses,
  cxCustomData, cxFilter, cxData, cxDataStorage, cxEdit, cxNavigator, cxDataControllerConditionalFormattingRulesManagerDialog,
  cxGridLevel, cxGridCustomTableView, cxGridTableView, cxGridDBTableView, cxGridCustomView, cxGrid,
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
    FPages: TPageControl;
    FReportTab: TTabSheet;
    FTreeTab: TTabSheet;
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
    procedure BuildReportTab;
    procedure BuildTreeTab;
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
  FPages := TPageControl.Create(Self);
  FPages.Parent := Self;
  FPages.Align := alClient;
  FReportTab := TTabSheet.Create(Self);
  FReportTab.PageControl := FPages;
  FReportTab.Caption := 'Report';
  FTreeTab := TTabSheet.Create(Self);
  FTreeTab.PageControl := FPages;
  FTreeTab.Caption := 'Call tree';
  FEditorTab := TTabSheet.Create(Self);
  FEditorTab.PageControl := FPages;
  FEditorTab.Caption := 'Source';
  BuildReportTab;
  BuildTreeTab;
  BuildEditorTab;

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
      else if SameText(LTab, 'source') then FPages.ActivePage := FEditorTab
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

  FInfoLabel := TLabel.Create(Self);
  FInfoLabel.Parent := FToolbar;
  FInfoLabel.Left := 650;
  FInfoLabel.Top := 12;
  FInfoLabel.Caption := 'No session open.';
  UpdateButtons('');
end;

procedure TMainForm.BuildReportTab;
begin
  FDetailsPanel := TPanel.Create(Self);
  FDetailsPanel.Parent := FReportTab;
  FDetailsPanel.Align := alBottom;
  FDetailsPanel.Height := 220;
  FDetailsPanel.BevelOuter := bvNone;

  FParentsGrid := BuildNeighbourGrid(FDetailsPanel, alLeft, 'Parents', FParentsView);
  FParentsGrid.Width := 560;
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
    Exit;
  end;
  FillNeighbours(FParentsView, FStore.Parents(AMethodId));
  FillNeighbours(FChildrenView, FStore.Children(AMethodId));
  ShowSourceOf(AMethodId);
end;

procedure TMainForm.FillNeighbours(AView: TcxGridTableView; const AItems: TNeighbours);
var
  I: Integer;
begin
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
