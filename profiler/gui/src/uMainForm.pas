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
  System.SysUtils, System.Classes, System.Math, System.UITypes, System.Types, System.RegularExpressions,
  Winapi.Windows, Winapi.Messages,
  Vcl.Graphics, Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.ComCtrls,
  System.Variants, System.IOUtils, System.StrUtils, System.IniFiles, Data.DB, FireDAC.Comp.Client,
  cxGraphics, cxControls, cxLookAndFeels, cxLookAndFeelPainters, cxStyles, cxClasses,
  cxCustomData, cxFilter, cxData, cxDataStorage, cxEdit, cxNavigator, cxDataControllerConditionalFormattingRulesManagerDialog,
  cxGridLevel, cxGridCustomTableView, cxGridTableView, cxGridDBTableView, cxGridCustomView, cxGrid,
  cxLabel, cxButtons, cxDropDownEdit, cxMemo, cxPC, cxCheckBox,
  cxProgressBar, cxTextEdit,
  cxTL, cxTLdxBarBuiltInMenu, cxInplaceContainer, cxTLData,
  dxBar, dxBarExtItems, dxStatusBar, Vcl.ImgList,
  dxDockControl, dxDockPanel,
  dxSkinsCore, dxSkinsDefaultPainters, dxSkinsForm,
  dxSkinOffice2019Colorful, dxSkinOffice2019Black,
  SynEdit, SynEditHighlighter, SynHighlighterCS, SynEditTypes, SynFunc,
  uSessionStore, uControlClient, uSetupDialog, uTheme, uSettings, uSettingsDialog,
  uLayouts, uLayoutDialog, uGlyphs;

type
  TMainForm = class(TForm)
  private
    FStore: TSessionStore;
    FBarManager: TdxBarManager;
    FGlyphs: TImageList;
    FSuppressCombo: Boolean;
    FOpenButton: TdxBarButton;
    FRefreshButton: TdxBarButton;
    FStatus: TdxStatusBar;
    FUnits: TdxBarCombo;
    FThemeBox: TdxBarCombo;
    FSettingsButton: TdxBarButton;
    FLayoutButton: TdxBarButton;
    FSkinController: TdxSkinController;
    FSummaryTab: TTabSheet;
    FSummary: TcxMemo;
    FMonitorTab: TTabSheet;
    FMonitor: TPaintBox;
    FMonitorLabel: TcxLabel;
    FMonitorSamples: TArray<Int64>;
    FMonitorLast: TSessionCounters;
    FMemoryTab: TTabSheet;
    FMemoryPages: TcxPageControl;
    FAllocTypeGrid: TcxGrid;
    FAllocTypeView: TcxGridDBTableView;
    FAllocTypeQuery: TFDQuery;
    FAllocTypeSource: TDataSource;
    FAllocSiteGrid: TcxGrid;
    FAllocSiteView: TcxGridDBTableView;
    FAllocSiteQuery: TFDQuery;
    FAllocSiteSource: TDataSource;
    FHeapGrid: TcxGrid;
    FHeapView: TcxGridDBTableView;
    FHeapQuery: TFDQuery;
    FHeapSource: TDataSource;
    FHeapFrom: TcxComboBox;
    FHeapTo: TcxComboBox;
    FHeapGrowth: TcxCheckBox;
    FHeapChart: TPaintBox;
    FHeapPoints: TArray<THeapTotal>;
    FExplorer: TcxTreeList;
    FExplorerColumn: TcxTreeListColumn;
    FExplorerSplitter: TSplitter;
    FSessionsRoot: string;
    FDockManager: TdxDockingManager;
    FDockSite: TdxDockSite;
    FExplorerPanel: TdxDockPanel;
    FReportPanel: TdxDockPanel;
    FDetailsDock: TdxDockPanel;
    FTreePanel: TdxDockPanel;
    FGraphPanel: TdxDockPanel;
    FSourcePanel: TdxDockPanel;
    FMemoryPanel: TdxDockPanel;
    FMonitorPanel: TdxDockPanel;
    FSummaryPanel: TdxDockPanel;
    FLogPanel: TdxDockPanel;
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
    FEditorHeader: TcxLabel;
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
    FStartButton: TdxBarButton;
    FSnapshotButton: TdxBarButton;
    FPauseButton: TdxBarButton;
    FStopButton: TdxBarButton;
    FPaused: Boolean;
    FPendingDialog: string;
    FLog: TcxMemo;
    procedure BuildToolbar;
    procedure BuildExplorer;
    function AddDockPanel(const ACaption: string; ATarget: TdxCustomDockControl;
      AType: TdxDockingType): TdxDockPanel;
    procedure SaveLayout;
    procedure LoadLayout;
    procedure ResetLayoutClick(Sender: TObject);
    function LayoutFile: string;
    procedure BuildSummaryTab;
    procedure BuildMemoryTab;
    procedure BuildMonitorTab;
    procedure PaintMonitor(Sender: TObject);
    procedure PollCounters;
    function BuildBoundGrid(AParent: TWinControl; out AView: TcxGridDBTableView;
      out ASource: TDataSource): TcxGrid;
    procedure LoadMemory;
    procedure HeapSelectionChanged(Sender: TObject);
    procedure PaintHeapChart(Sender: TObject);
    procedure UpdateSummary;
    procedure UnitsChanged(Sender: TObject);
    procedure ThemeChanged(Sender: TObject);
    procedure ShowPreferencesInToolbar;
    procedure SetStatus(const AText: string);
    procedure ApplyTheme;
    procedure RecolourGlyphs;
    procedure SettingsClick(Sender: TObject);
    procedure LayoutsClick(Sender: TObject);
    procedure ApplyCodeFont;
    procedure ShowPendingDialog(Sender: TObject);
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
    procedure DressColumns(AView: TcxGridDBTableView);
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

  // One skin controller drives every DevExpress control; the rest of the window is
  // coloured explicitly by ApplyTheme.
  FSkinController := TdxSkinController.Create(Self);
  FSkinController.NativeStyle := False;

  BuildToolbar;

  // Docking, like AQTime: every panel can be moved, tabbed with another, floated or
  // closed, and the arrangement is remembered between runs.
  FDockManager := TdxDockingManager.Create(Self);
  FDockSite := TdxDockSite.Create(Self);
  FDockSite.Name := 'MainDockSite';
  FDockSite.Parent := Self;
  FDockSite.Align := alClient;
  // Docking needs real windows: the form is built before it is shown, so ask for the
  // handles now rather than letting the first DockTo walk a half-built control.
  HandleNeeded;
  FDockSite.HandleNeeded;

  // Order matters: the client panel first, then everything hangs off it. Docking a
  // side panel to an empty site first leaves the later ones with nowhere to go.
  FReportPanel := AddDockPanel('Report', FDockSite, dtClient);
  FExplorerPanel := AddDockPanel('Explorer', FDockSite, dtLeft);

  // The bottom strip is a tab container, which is where AQTime keeps Details, the
  // call views and the rest.
  FDetailsDock := AddDockPanel('Details', FDockSite, dtBottom);
  FTreePanel := AddDockPanel('Call tree', FDetailsDock, dtClient);
  FGraphPanel := AddDockPanel('Call graph', FDetailsDock, dtClient);
  FSourcePanel := AddDockPanel('Source', FDetailsDock, dtClient);
  FMemoryPanel := AddDockPanel('Memory', FDetailsDock, dtClient);
  FMonitorPanel := AddDockPanel('Monitor', FDetailsDock, dtClient);
  FSummaryPanel := AddDockPanel('Summary', FDetailsDock, dtClient);
  FLogPanel := AddDockPanel('Session log', FDetailsDock, dtClient);

  // Sizes last: a panel resized before its neighbours exist gets squeezed back by the
  // containers created afterwards.
  FExplorerPanel.Width := 280;
  FDetailsDock.Height := 260;

  BuildExplorer;
  BuildReportTab;
  BuildTreeTab;
  BuildGraphTab;
  BuildEditorTab;
  BuildMemoryTab;
  BuildMonitorTab;
  BuildSummaryTab;

  FLog := TcxMemo.Create(Self);
  FLog.Parent := FLogPanel;
  FLog.Align := alClient;
  FLog.Properties.ScrollBars := ssVertical;
  FLog.Properties.ReadOnly := True;
  FLog.Style.Font.Name := 'Consolas';

  // After the panels have their content: the layout moves them around, and the
  // preferences decide how everything is drawn.
  uSettings.LoadSettings;
  if GSettings.SessionsRoot <> '' then
  begin
    FSessionsRoot := GSettings.SessionsRoot;
    ReloadExplorer;
  end;
  ShowPreferencesInToolbar;
  LoadLayout;
  ApplyTheme;
  ApplyCodeFont;

  FClient := TControlClient.Create;
  FPoll := TTimer.Create(Self);
  FPoll.Interval := 1000;
  FPoll.Enabled := False;
  FPoll.OnTimer := PollTimer;
  OnShow := ShowPendingDialog;

  if (ParamCount >= 1) and not ParamStr(1).StartsWith('--') then
    LoadSession(ParamStr(1));
  // --tab=<report|tree|source> selects the visible panel: handy for a screenshot or a
  // shortcut that always opens where you left off.
  // --dialog=<settings|layouts> opens one straight away: it is how the dialogs get
  // exercised without a hand on the mouse.
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--dialog=', True) then
      FPendingDialog := ParamStr(LIndex).Substring(9);
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--tab=', True) then
    begin
      LTab := ParamStr(LIndex).Substring(6);
      if SameText(LTab, 'tree') then FTreePanel.Activate
      else if SameText(LTab, 'graph') then FGraphPanel.Activate
      else if SameText(LTab, 'source') then FSourcePanel.Activate
      else if SameText(LTab, 'summary') then FSummaryPanel.Activate
      else if SameText(LTab, 'memory') then FMemoryPanel.Activate
      else if SameText(LTab, 'monitor') then FMonitorPanel.Activate
      else FReportPanel.Activate;
    end;
  UpdateInfo;
end;

destructor TMainForm.Destroy;
var
  I: Integer;
begin
  SaveLayout;
  uSettings.SaveSettings;
  // Dock panels created at runtime must go before the form takes its own children down,
  // otherwise one of them is destroyed after the window it lives in and VCL complains
  // that it "has no parent window". This is what the DevExpress sample does too.
  I := dxDockingController.DockControlCount - 1;
  while I >= 0 do
  begin
    if dxDockingController.DockControls[I] is TdxDockPanel then
      dxDockingController.DockControls[I].Free;
    if dxDockingController.DockControlCount - 1 < I - 1 then
      I := dxDockingController.DockControlCount - 1
    else
      Dec(I);
  end;
  FAllocTypeQuery.Free;
  FAllocSiteQuery.Free;
  FHeapQuery.Free;
  FPoll.Enabled := False;
  FClient.Free;               // shuts the control service down with us
  FReportQuery.Free;
  FStore.Free;
  inherited Destroy;
end;

/// AQTime's toolbar, on a bar manager rather than a panel of buttons: it is skinned with
/// the rest, the user can rearrange or hide it, and the items lay themselves out instead
/// of sitting at hardcoded pixels that break on a different DPI.
procedure TMainForm.BuildToolbar;

  function NewBar(const ACaption: string): TdxBar;
  begin
    Result := FBarManager.Bars.Add;
    Result.Caption := ACaption;
    Result.DockingStyle := dsTop;
    Result.Visible := True;
  end;

  function NewButton(ABar: TdxBar; const ACaption, AHint: string; AGlyph: TGlyphKind;
    AClick: TNotifyEvent; ABeginGroup: Boolean = False): TdxBarButton;
  begin
    Result := FBarManager.AddButton;
    Result.Caption := ACaption;
    Result.Hint := AHint;
    Result.ImageIndex := Ord(AGlyph);
    Result.PaintStyle := psCaptionGlyph;
    Result.OnClick := AClick;
    ABar.ItemLinks.Add.Item := Result;
    ABar.ItemLinks[ABar.ItemLinks.Count - 1].BeginGroup := ABeginGroup;
  end;

  function NewCombo(ABar: TdxBar; const ACaption: string; AWidth: Integer;
    AChange: TNotifyEvent): TdxBarCombo;
  begin
    Result := TdxBarCombo(FBarManager.AddItem(TdxBarCombo));
    Result.Caption := ACaption;
    Result.Width := AWidth;
    Result.ShowCaption := True;
    Result.ShowEditor := False;
    Result.OnChange := AChange;
    ABar.ItemLinks.Add.Item := Result;
  end;

var
  LSession, LView: TdxBar;
begin
  // Filling a combo raises the same change event a click does, and at this point half
  // the window does not exist yet: the handlers stay out of the way until it does.
  FSuppressCombo := True;
  FBarManager := TdxBarManager.Create(Self);
  FBarManager.AllowReset := False;
  FGlyphs := BuildGlyphs(Self, ThemeColors.Text);
  FBarManager.ImageOptions.Images := FGlyphs;

  LSession := NewBar('Session');
  FOpenButton := NewButton(LSession, 'Open session...',
    'Open the results of a session already collected', gkOpen, OpenButtonClick);
  FRefreshButton := NewButton(LSession, 'Refresh',
    'Re-read the open session from disk', gkRefresh, RefreshButtonClick);
  FStartButton := NewButton(LSession, 'New session...',
    'Profile an app: pick the device, the mode and what to instrument', gkRun,
    StartButtonClick, True);
  FSnapshotButton := NewButton(LSession, 'Snapshot',
    'Refresh the results from what has been collected so far, without stopping the app',
    gkSnapshot, SnapshotButtonClick);
  FPauseButton := NewButton(LSession, 'Pause',
    'Stop recording without stopping the app: the methods stay instrumented, so their overhead remains',
    gkPause, PauseButtonClick);
  FStopButton := NewButton(LSession, 'Stop', 'End the session and analyse what it collected',
    gkStop, StopButtonClick);

  LView := NewBar('View');
  FSettingsButton := NewButton(LView, 'Settings...',
    'Theme, units, the font code is read in, and where sessions are kept', gkSettings,
    SettingsClick);
  FLayoutButton := NewButton(LView, 'Layouts...',
    'Save, load and manage panel arrangements', gkLayouts, LayoutsClick);

  FThemeBox := NewCombo(LView, 'Theme', 70, ThemeChanged);
  FThemeBox.Items.Add(ThemeName(atLight));
  FThemeBox.Items.Add(ThemeName(atDark));
  FThemeBox.ItemIndex := 0;

  FUnits := NewCombo(LView, 'Times in', 95, UnitsChanged);
  FUnits.Items.Add(TimeUnitName(tuAuto));
  FUnits.Items.Add(TimeUnitName(tuSeconds));
  FUnits.Items.Add(TimeUnitName(tuMilliseconds));
  FUnits.Items.Add(TimeUnitName(tuMicroseconds));
  FUnits.Items.Add(TimeUnitName(tuNanoseconds));
  FUnits.ItemIndex := 0;

  // Session first, then View: bars are laid out in docking order, not creation order,
  // and the run controls are what the eye should land on.
  LView.Move(LSession, True);

  FStatus := TdxStatusBar.Create(Self);
  FStatus.Parent := Self;
  FStatus.Align := alBottom;
  FStatus.Panels.Add.Fixed := False;
  SetStatus('No session open.');

  FSuppressCombo := False;
  UpdateButtons('');
end;

/// The glyphs are drawn in the theme's text colour, so a theme change means drawing them
/// again. The old list goes only after the bars point at the new one.
procedure TMainForm.RecolourGlyphs;
var
  LPrevious: TImageList;
begin
  if FBarManager = nil then
    Exit;
  LPrevious := FGlyphs;
  FGlyphs := BuildGlyphs(Self, ThemeColors.Text);
  FBarManager.ImageOptions.Images := FGlyphs;
  LPrevious.Free;
end;

/// AQTime's Explorer: the results you can open, and the categories inside the one that
/// is open. Double-clicking a session loads it.
/// One dockable panel, docked where it belongs on first run; after that the saved
/// layout decides.
function TMainForm.AddDockPanel(const ACaption: string; ATarget: TdxCustomDockControl;
  AType: TdxDockingType): TdxDockPanel;
// No manager assignment: the docking controller is global, a panel only needs an
// owner form and somewhere to dock.
var
  LTarget: TdxCustomDockControl;
begin
  Result := TdxDockPanel.Create(Self);
  // A saved layout matches controls by Name, so panels built in code need one or the
  // layout comes back as a tree of strangers and leaves panels unparented.
  Result.Name := 'Panel' + TRegEx.Replace(ACaption, '[^A-Za-z0-9]', '');
  // A panel with no parent has no ParentForm, and the docking painter is resolved
  // through it: docking one straight after Create walks into a nil form.
  Result.Parent := Self;
  Result.Caption := ACaption;
  LTarget := ATarget;
  // Tabbing onto a panel that already has tabs means joining the container, not the
  // panel: docking to the panel again leaves this one homeless.
  if (AType = dtClient) and (ATarget is TdxDockPanel) and (TdxDockPanel(ATarget).TabContainer <> nil) then
    LTarget := TdxDockPanel(ATarget).TabContainer;
  Result.DockTo(LTarget, AType, 0);
end;

function TMainForm.LayoutFile: string;
begin
  Result := TPath.ChangeExtension(ParamStr(0), '.layout.ini');
end;

procedure TMainForm.SaveLayout;
begin
  try
    FDockManager.SaveLayoutToIniFile(LayoutFile);
  except
    // a layout that cannot be saved is not worth an error dialog on the way out
  end;
end;

procedure TMainForm.LoadLayout;
begin
  // A named layout marked as the one to open with wins; otherwise the window comes
  // back the way it was closed.
  if LayoutExists(GSettings.DefaultLayout) then
  begin
    try
      LoadNamedLayout(GSettings.DefaultLayout);
      Exit;
    except
      on E: Exception do
        FLog.Lines.Add('layout "' + GSettings.DefaultLayout + '" not restored: ' + E.Message);
    end;
  end;
  if not TFile.Exists(LayoutFile) then
    Exit;
  try
    FDockManager.LoadLayoutFromIniFile(LayoutFile);
  except
    // an old or broken layout file must not stop the application from opening
    on E: Exception do
      FLog.Lines.Add('layout not restored: ' + E.Message);
  end;
end;

procedure TMainForm.ResetLayoutClick(Sender: TObject);
begin
  if TFile.Exists(LayoutFile) then
    TFile.Delete(LayoutFile);
  MessageDlg('The panel layout will be back to its default the next time you start.',
    mtInformation, [mbOK], 0);
end;

procedure TMainForm.BuildExplorer;
begin
  FExplorer := TcxTreeList.Create(Self);
  FExplorer.Parent := FExplorerPanel;
  FExplorer.Align := alClient;
  FExplorer.OptionsData.Editing := False;
  FExplorer.OptionsSelection.CellSelect := False;
  FExplorer.OptionsView.Headers := False;
  FExplorer.OptionsView.ShowRoot := True;
  FExplorer.OnDblClick := ExplorerDblClick;
  FExplorerColumn := FExplorer.CreateColumn;
  FExplorerColumn.Caption.Text := 'Results';
  FExplorerColumn.Width := 260;

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
        LChild.Values[0] := 'Source files';
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
  LCategory: string;
  LColumn: TcxGridDBColumn;
begin
  if FExplorer.FocusedNode = nil then
    Exit;
  // A category under the open session regroups the Report instead of loading anything.
  if FExplorer.FocusedNode.Level = 2 then
  begin
    LCategory := VarToStr(FExplorer.FocusedNode.Values[0]);
    FGridView.BeginUpdate;
    try
      FGridView.DataController.Groups.ClearGrouping;
      if SameText(LCategory, 'Modules') then
        LColumn := FGridView.GetColumnByFieldName('module')
      else if SameText(LCategory, 'Source files') then
        LColumn := FGridView.GetColumnByFieldName('source_file')
      else
        LColumn := nil;
      if LColumn <> nil then
      begin
        LColumn.GroupIndex := 0;
        LColumn.Visible := True;
      end;
    finally
      FGridView.EndUpdate;
    end;
    FGridView.ViewData.Expand(True);
    FReportPanel.Activate;
    Exit;
  end;
  if FExplorer.FocusedNode.Level <> 1 then
    Exit;
  LSessions := ListSessions(FSessionsRoot);
  LIndex := Integer(NativeInt(FExplorer.FocusedNode.Data));
  if (LIndex >= 0) and (LIndex <= High(LSessions)) then
    LoadSession(LSessions[LIndex].DatabasePath);
end;

procedure TMainForm.BuildReportTab;
begin
  FDetailsPanel := TPanel.Create(Self);
  FDetailsPanel.Parent := FDetailsDock;
  FDetailsPanel.Align := alClient;
  FDetailsPanel.BevelOuter := bvNone;

  FParentsGrid := BuildNeighbourGrid(FDetailsPanel, alLeft, 'Parents', FParentsView);
  FParentsGrid.Parent.Width := 560;
  FChildrenGrid := BuildNeighbourGrid(FDetailsPanel, alClient, 'Children', FChildrenView);

  FGrid := TcxGrid.Create(Self);
  FGrid.Parent := FReportPanel;
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
  LLabel: TcxLabel;
  LGrid: TcxGrid;
  LLevel: TcxGridLevel;
  LPie: TPaintBox;
begin
  LPanel := TPanel.Create(Self);
  LPanel.Parent := AParent;
  LPanel.Align := AAlign;
  LPanel.BevelOuter := bvNone;

  LLabel := TcxLabel.Create(Self);
  LLabel.Transparent := True;
  LLabel.Parent := LPanel;
  LLabel.Align := alTop;
  LLabel.Caption := '  ' + ACaption;
  LLabel.Style.Font.Style := [fsBold];

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
  FTree.Parent := FTreePanel;
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
  FGraphScroll.Parent := FGraphPanel;
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
    ACanvas.Brush.Color := ThemeColors.BoxCentreFill;
    ACanvas.Pen.Width := 2;
  end
  else
  begin
    ACanvas.Brush.Color := ThemeColors.BoxFill;
    ACanvas.Pen.Width := 1;
  end;
  ACanvas.Font.Color := ThemeColors.Text;
  ACanvas.Pen.Color := ThemeColors.Line;
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
  LCanvas.Brush.Color := ThemeColors.Window;
  LCanvas.Font.Color := ThemeColors.Text;
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
    LCanvas.Pen.Color := ThemeColors.Line;
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
    LCanvas.Pen.Color := ThemeColors.Line;
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
  FEditorHeader := TcxLabel.Create(Self);
  FEditorHeader.Transparent := True;
  FEditorHeader.Parent := FSourcePanel;
  FEditorHeader.Align := alTop;
  FEditorHeader.Caption := ' Pick a method in the Report to see its source.';

  FEditor := TSynEdit.Create(Self);
  FEditor.Parent := FSourcePanel;
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
    FG := ThemeColors.EditorText;
    BG := ThemeColors.RangeWash;   // a wash over the method the Report is pointing at
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

/// A grid bound to a query, which is what every memory view is.
function TMainForm.BuildBoundGrid(AParent: TWinControl; out AView: TcxGridDBTableView;
  out ASource: TDataSource): TcxGrid;
var
  LGrid: TcxGrid;
  LLevel: TcxGridLevel;
begin
  LGrid := TcxGrid.Create(Self);
  LGrid.Parent := AParent;
  LGrid.Align := alClient;
  LLevel := LGrid.Levels.Add;
  AView := LGrid.CreateView(TcxGridDBTableView) as TcxGridDBTableView;
  LLevel.GridView := AView;
  AView.OptionsData.Editing := False;
  AView.OptionsSelection.CellSelect := False;
  AView.OptionsView.GroupByBox := True;
  ASource := TDataSource.Create(Self);
  AView.DataController.DataSource := ASource;
  Result := LGrid;
end;

/// Memory, in the three questions the engine can answer: what was allocated, who
/// allocated it, and what is still alive (with the growth between two snapshots).
procedure TMainForm.BuildMemoryTab;
var
  LByType, LBySite, LHeap: TcxTabSheet;
  LHeapTop: TPanel;
  LLabel: TcxLabel;
begin
  FMemoryPages := TcxPageControl.Create(Self);
  FMemoryPages.Parent := FMemoryPanel;
  FMemoryPages.Align := alClient;

  LByType := TcxTabSheet.Create(Self);
  LByType.PageControl := FMemoryPages;
  LByType.Caption := 'Allocations by type';
  FAllocTypeGrid := BuildBoundGrid(LByType, FAllocTypeView, FAllocTypeSource);

  LBySite := TcxTabSheet.Create(Self);
  LBySite.PageControl := FMemoryPages;
  LBySite.Caption := 'Allocations by method';
  FAllocSiteGrid := BuildBoundGrid(LBySite, FAllocSiteView, FAllocSiteSource);

  LHeap := TcxTabSheet.Create(Self);
  LHeap.PageControl := FMemoryPages;
  LHeap.Caption := 'Live heap';

  LHeapTop := TPanel.Create(Self);
  LHeapTop.Parent := LHeap;
  LHeapTop.Align := alTop;
  LHeapTop.Height := 36;
  LHeapTop.BevelOuter := bvNone;

  LLabel := TcxLabel.Create(Self);
  LLabel.Transparent := True;
  LLabel.Parent := LHeapTop;
  LLabel.SetBounds(8, 10, 60, 16);
  LLabel.Caption := 'Snapshot';

  FHeapFrom := TcxComboBox.Create(Self);
  FHeapFrom.Parent := LHeapTop;
  FHeapFrom.SetBounds(70, 6, 80, 24);
  FHeapFrom.Properties.DropDownListStyle := lsFixedList;
  FHeapFrom.Properties.OnChange := HeapSelectionChanged;

  FHeapGrowth := TcxCheckBox.Create(Self);
  FHeapGrowth.Parent := LHeapTop;
  FHeapGrowth.SetBounds(160, 8, 140, 20);
  FHeapGrowth.Caption := 'growth against';
  FHeapGrowth.Properties.OnChange := HeapSelectionChanged;

  FHeapTo := TcxComboBox.Create(Self);
  FHeapTo.Parent := LHeapTop;
  FHeapTo.SetBounds(304, 6, 80, 24);
  FHeapTo.Properties.DropDownListStyle := lsFixedList;
  FHeapTo.Properties.OnChange := HeapSelectionChanged;

  // The chart above the grid: how the live set moved from snapshot to snapshot, which
  // is the shape a leak has before any single type looks suspicious.
  FHeapChart := TPaintBox.Create(Self);
  FHeapChart.Parent := LHeap;
  FHeapChart.Align := alTop;
  FHeapChart.Height := 112;
  FHeapChart.OnPaint := PaintHeapChart;

  FHeapGrid := BuildBoundGrid(LHeap, FHeapView, FHeapSource);
end;

procedure TMainForm.LoadMemory;
var
  LIds: TArray<Integer>;
  I: Integer;
begin
  FAllocTypeSource.DataSet := nil;
  FreeAndNil(FAllocTypeQuery);
  FAllocTypeView.ClearItems;
  FAllocSiteSource.DataSet := nil;
  FreeAndNil(FAllocSiteQuery);
  FAllocSiteView.ClearItems;
  FHeapSource.DataSet := nil;
  FreeAndNil(FHeapQuery);
  FHeapView.ClearItems;
  FHeapFrom.Properties.Items.Clear;
  FHeapTo.Properties.Items.Clear;
  if not FStore.IsOpen then
    Exit;

  if FStore.CountOf('alloc_by_type') > 0 then
  begin
    FAllocTypeQuery := FStore.OpenAllocationsByType;
    FAllocTypeSource.DataSet := FAllocTypeQuery;
    FAllocTypeView.DataController.CreateAllItems;
    DressColumns(FAllocTypeView);
  end;
  if FStore.CountOf('alloc_by_site') > 0 then
  begin
    FAllocSiteQuery := FStore.OpenAllocationsBySite;
    FAllocSiteSource.DataSet := FAllocSiteQuery;
    FAllocSiteView.DataController.CreateAllItems;
    DressColumns(FAllocSiteView);
  end;

  FHeapPoints := FStore.HeapTotals;
  if FHeapChart <> nil then
    FHeapChart.Invalidate;
  // Open the page that has something to show: a heap session has no allocation sites,
  // and the first page of an empty tab set reads as a broken panel.
  if (Length(FHeapPoints) > 0) and (FStore.CountOf('alloc_by_type') = 0) then
    FMemoryPages.ActivePageIndex := 2
  else
    FMemoryPages.ActivePageIndex := 0;
  LIds := FStore.HeapSnapshotIds;
  for I := 0 to High(LIds) do
  begin
    FHeapFrom.Properties.Items.Add(IntToStr(LIds[I]));
    FHeapTo.Properties.Items.Add(IntToStr(LIds[I]));
  end;
  if FHeapFrom.Properties.Items.Count > 0 then
  begin
    FHeapFrom.ItemIndex := 0;
    FHeapTo.ItemIndex := FHeapTo.Properties.Items.Count - 1;
    HeapSelectionChanged(nil);
  end;
end;

/// Bars per snapshot: bytes alive, with the object count written above each bar. Two
/// snapshots that look alike in bytes but differ in count say something different from
/// two that grow together, so both are on the chart.
procedure TMainForm.PaintHeapChart(Sender: TObject);
var
  LCanvas: TCanvas;
  LRect, LBar: TRect;
  LColors: TThemeColors;
  LMax: Int64;
  LWidth, I: Integer;
begin
  LCanvas := FHeapChart.Canvas;
  LColors := ThemeColors;
  LCanvas.Brush.Color := LColors.Window;
  LCanvas.Font.Color := LColors.Text;
  LCanvas.FillRect(FHeapChart.ClientRect);
  LRect := FHeapChart.ClientRect;
  LRect.Inflate(-32, -20);
  if (Length(FHeapPoints) = 0) or (LRect.Width < 40) then
  begin
    LCanvas.Brush.Style := bsClear;
    LCanvas.TextOut(12, 8, 'No heap snapshots in this session.');
    LCanvas.Brush.Style := bsSolid;
    Exit;
  end;

  LMax := 1;
  for I := 0 to High(FHeapPoints) do
    if FHeapPoints[I].Bytes > LMax then
      LMax := FHeapPoints[I].Bytes;

  LCanvas.Pen.Color := LColors.Line;
  LCanvas.MoveTo(LRect.Left, LRect.Bottom);
  LCanvas.LineTo(LRect.Right, LRect.Bottom);

  LWidth := Max(12, Min(80, LRect.Width div (Length(FHeapPoints) * 2)));
  for I := 0 to High(FHeapPoints) do
  begin
    LBar.Left := LRect.Left + 8 + I * (LWidth + 24);
    LBar.Right := LBar.Left + LWidth;
    LBar.Bottom := LRect.Bottom;
    LBar.Top := LRect.Bottom - Round(LRect.Height * (FHeapPoints[I].Bytes / LMax));
    if LBar.Top >= LBar.Bottom then
      LBar.Top := LBar.Bottom - 1;
    LCanvas.Brush.Color := LColors.Accent;
    LCanvas.FillRect(LBar);

    LCanvas.Brush.Style := bsClear;
    LCanvas.TextOut(LBar.Left, LBar.Top - 16, Format('%.1f MB', [FHeapPoints[I].Bytes / 1048576]));
    LCanvas.TextOut(LBar.Left, LRect.Bottom + 4, Format('#%d  %d obj', [FHeapPoints[I].Id, FHeapPoints[I].Objects]));
    LCanvas.Brush.Style := bsSolid;
  end;
end;

procedure TMainForm.HeapSelectionChanged(Sender: TObject);
var
  LFrom, LTo: Integer;
begin
  if not FStore.IsOpen or (FHeapFrom.ItemIndex < 0) then
    Exit;
  FHeapSource.DataSet := nil;
  FreeAndNil(FHeapQuery);
  FHeapView.ClearItems;
  LFrom := StrToIntDef(FHeapFrom.Text, 0);
  LTo := StrToIntDef(FHeapTo.Text, LFrom);
  if FHeapGrowth.Checked and (LTo <> LFrom) then
    FHeapQuery := FStore.OpenHeapGrowth(LFrom, LTo)
  else
    FHeapQuery := FStore.OpenHeapByType(LFrom);
  FHeapSource.DataSet := FHeapQuery;
  FHeapView.DataController.CreateAllItems;
  DressColumns(FHeapView);
end;

/// AQTime's Monitor: what the run is doing right now. Ours plots how fast the session
/// is producing data - the trace on the provider engine, the pulled event files on the
/// weaver - which is the number that tells you whether a session is worth waiting for.
procedure TMainForm.BuildMonitorTab;
begin
  FMonitorLabel := TcxLabel.Create(Self);
  FMonitorLabel.Transparent := True;
  FMonitorLabel.Parent := FMonitorPanel;
  FMonitorLabel.Align := alTop;
  FMonitorLabel.Caption := ' No session running.';

  FMonitor := TPaintBox.Create(Self);
  FMonitor.Parent := FMonitorPanel;
  FMonitor.Align := alClient;
  FMonitor.OnPaint := PaintMonitor;
end;

procedure TMainForm.PaintMonitor(Sender: TObject);
var
  LCanvas: TCanvas;
  LRect: TRect;
  LMax: Int64;
  I, LX, LY, LPrevX, LPrevY: Integer;
  LStep: Double;
begin
  LCanvas := FMonitor.Canvas;
  LRect := FMonitor.ClientRect;
  LCanvas.Brush.Color := ThemeColors.Window;
  LCanvas.Font.Color := ThemeColors.Text;
  LCanvas.FillRect(LRect);
  LRect.Inflate(-40, -30);
  if LRect.Width < 40 then
    Exit;

  LCanvas.Pen.Color := ThemeColors.Line;
  LCanvas.MoveTo(LRect.Left, LRect.Bottom);
  LCanvas.LineTo(LRect.Right, LRect.Bottom);
  LCanvas.MoveTo(LRect.Left, LRect.Top);
  LCanvas.LineTo(LRect.Left, LRect.Bottom);

  if Length(FMonitorSamples) < 2 then
  begin
    LCanvas.Brush.Style := bsClear;
    LCanvas.TextOut(LRect.Left + 8, LRect.Top + 8, 'Waiting for a running session...');
    LCanvas.Brush.Style := bsSolid;
    Exit;
  end;

  LMax := 1;
  for I := 0 to High(FMonitorSamples) do
    if FMonitorSamples[I] > LMax then
      LMax := FMonitorSamples[I];

  LStep := LRect.Width / (Length(FMonitorSamples) - 1);
  LPrevX := LRect.Left;
  LPrevY := LRect.Bottom - Round(LRect.Height * (FMonitorSamples[0] / LMax));
  LCanvas.Pen.Color := ThemeColors.Accent;
  LCanvas.Pen.Width := 2;
  for I := 1 to High(FMonitorSamples) do
  begin
    LX := LRect.Left + Round(I * LStep);
    LY := LRect.Bottom - Round(LRect.Height * (FMonitorSamples[I] / LMax));
    LCanvas.MoveTo(LPrevX, LPrevY);
    LCanvas.LineTo(LX, LY);
    LPrevX := LX;
    LPrevY := LY;
  end;
  LCanvas.Pen.Width := 1;

  LCanvas.Brush.Style := bsClear;
  LCanvas.TextOut(LRect.Left + 4, LRect.Top - 18, Format('peak %.1f MB', [LMax / 1048576]));
  LCanvas.Brush.Style := bsSolid;
end;

/// Called on the same tick as the status poll while a session is live.
procedure TMainForm.PollCounters;
var
  LCounters: TSessionCounters;
  LValue: Int64;
begin
  if FSessionId = '' then
    Exit;
  try
    LCounters := FClient.Counters(FSessionId);
  except
    Exit;      // the monitor is a nicety: never let it break the session view
  end;
  FMonitorLast := LCounters;
  LValue := LCounters.TraceBytes;
  if LValue = 0 then
    LValue := LCounters.EventBytes;
  SetLength(FMonitorSamples, Length(FMonitorSamples) + 1);
  FMonitorSamples[High(FMonitorSamples)] := LValue;
  if Length(FMonitorSamples) > 600 then          // ten minutes at one point a second
    FMonitorSamples := Copy(FMonitorSamples, 1, Length(FMonitorSamples) - 1);
  FMonitorLabel.Caption := Format(' %s   elapsed %.0f s   collected %.2f MB   snapshots %d',
    [LCounters.State, LCounters.ElapsedSeconds, LValue / 1048576, LCounters.Snapshots]);
  FMonitor.Invalidate;
end;

procedure TMainForm.BuildSummaryTab;
begin
  FSummary := TcxMemo.Create(Self);
  FSummary.Parent := FSummaryPanel;
  FSummary.Align := alClient;
  FSummary.Properties.ReadOnly := True;
  FSummary.Properties.ScrollBars := ssBoth;
  FSummary.Properties.WordWrap := False;
  FSummary.Style.Font.Name := 'Consolas';
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

/// The toolbar combos say what the settings say. Assigning to them raises the same
/// change event a click does, so the handlers step aside while we do it.
procedure TMainForm.ShowPreferencesInToolbar;
begin
  FSuppressCombo := True;
  try
    FThemeBox.ItemIndex := Ord(GTheme);
    FUnits.ItemIndex := Ord(GTimeUnit);
  finally
    FSuppressCombo := False;
  end;
end;

procedure TMainForm.SetStatus(const AText: string);
begin
  if FStatus <> nil then
    FStatus.Panels[0].Text := AText;
end;

procedure TMainForm.ThemeChanged(Sender: TObject);
begin
  if FSuppressCombo then
    Exit;
  GTheme := TAppTheme(FThemeBox.ItemIndex);
  GSettings.Theme := GTheme;
  ApplyTheme;
end;

/// Colour everything that does not follow the skin: the plain VCL controls, the editor,
/// and the panels we paint ourselves.
procedure TMainForm.ApplyTheme;
var
  LColors: TThemeColors;
begin
  LColors := ThemeColors;
  FSkinController.SkinName := SkinNameFor(GTheme);
  Color := LColors.Window;
  RecolourGlyphs;
  // The cx controls follow the skin, so nothing to colour here: only the plain
  // canvases and the editor below still need telling.
  ApplyThemeToEditor(FEditor);
  if FHeapChart <> nil then FHeapChart.Invalidate;
  if FParentsPie <> nil then FParentsPie.Invalidate;
  if FChildrenPie <> nil then FChildrenPie.Invalidate;
  if FGraph <> nil then FGraph.Invalidate;
  if FMonitor <> nil then FMonitor.Invalidate;
  Invalidate;
end;

/// The font from the settings, applied where code and logs are read.
procedure TMainForm.ApplyCodeFont;
begin
  if (Trim(GSettings.CodeFontName) = '') or (GSettings.CodeFontSize <= 0) then
    Exit;
  if FEditor <> nil then
  begin
    FEditor.Font.Name := GSettings.CodeFontName;
    FEditor.Font.Size := GSettings.CodeFontSize;
    FEditor.Gutter.Font.Assign(FEditor.Font);
    FEditor.Gutter.Font.Color := ThemeColors.GutterText;
  end;
  if FLog <> nil then
  begin
    FLog.Style.Font.Name := GSettings.CodeFontName;
    FLog.Style.Font.Size := GSettings.CodeFontSize;
  end;
  if FSummary <> nil then
  begin
    FSummary.Style.Font.Name := GSettings.CodeFontName;
    FSummary.Style.Font.Size := GSettings.CodeFontSize;
  end;
end;

procedure TMainForm.ShowPendingDialog(Sender: TObject);
var
  LWhich: string;
begin
  LWhich := FPendingDialog;
  FPendingDialog := '';
  if SameText(LWhich, 'settings') then
    SettingsClick(nil)
  else if SameText(LWhich, 'layouts') then
    LayoutsClick(nil);
end;

procedure TMainForm.SettingsClick(Sender: TObject);
var
  LDialog: TSettingsDialog;
begin
  LDialog := TSettingsDialog.Create(Self);
  try
    if not LDialog.Execute then
      Exit;
  finally
    LDialog.Free;
  end;
  ShowPreferencesInToolbar;
  ApplyTheme;
  ApplyCodeFont;
  UnitsChanged(nil);
end;

procedure TMainForm.LayoutsClick(Sender: TObject);
var
  LDialog: TLayoutDialog;
  LName: string;
begin
  LDialog := TLayoutDialog.Create(Self);
  try
    LName := LDialog.Execute;
  finally
    LDialog.Free;
  end;
  if LName = '' then
    Exit;
  try
    LoadNamedLayout(LName);
  except
    on E: Exception do
      MessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.UnitsChanged(Sender: TObject);
begin
  if FSuppressCombo then
    Exit;
  GTimeUnit := TTimeUnit(FUnits.ItemIndex);
  GSettings.TimeUnit := GTimeUnit;
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
  LoadMemory;
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
begin
  DressColumns(FGridView);
end;

procedure TMainForm.DressColumns(AView: TcxGridDBTableView);
var
  I: Integer;
  LColumn: TcxGridDBColumn;
  LField: string;
begin
  for I := 0 to AView.ColumnCount - 1 do
  begin
    LColumn := AView.Columns[I];
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
      LColumn.Caption := 'Bytes'
    else if LField = 'type_name' then
    begin
      LColumn.Caption := 'Type';
      LColumn.Width := 420;
    end
    else if LField = 'allocating_method' then
    begin
      LColumn.Caption := 'Allocating method';
      LColumn.Width := 420;
    end
    else if LField = 'count_from' then
      LColumn.Caption := 'Objects before'
    else if LField = 'count_to' then
      LColumn.Caption := 'Objects after'
    else if LField = 'delta_objects' then
      LColumn.Caption := 'Objects gained'
    else if LField = 'delta_bytes' then
      LColumn.Caption := 'Bytes gained'
    else if LField = 'taken_utc' then
      LColumn.Caption := 'Taken'
    else if LField = 'total_objects' then
      LColumn.Caption := 'Objects'
    else if LField = 'total_bytes' then
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
var
  LColors: TThemeColors;
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
  LColors := ThemeColors;
  ACanvas.Brush.Color := LColors.Window;
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
    ACanvas.Brush.Color := LColors.Slices[I mod Length(LColors.Slices)];
    ACanvas.Pen.Color := LColors.Window;
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
    SetStatus('No session open. Use "Open session..." and pick a session.db.');
    Exit;
  end;
  LText := Format('%s  |  %s on %s  |  state %s', [ModeToString(FStore.Mode), FStore.Package, FStore.Device, FStore.State]);
  if FStore.Mode = smSampling then
    LText := LText + Format('  |  %d samples', [FStore.TotalSamples]);
  LSegments := FStore.Segments;
  if Length(LSegments) > 0 then
    LText := LText + Format('  |  %d refreshes, last %s', [Length(LSegments), LSegments[High(LSegments)].Kind]);
  SetStatus(LText);
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
    GSettings.NapExePath,
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), 'nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Debug\net10.0\nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Release\net10.0\nap.exe')];
  for LPath in LCandidates do
    if (LPath <> '') and TFile.Exists(LPath) then
    begin
      try
        // The sessions folder from the settings, so the Explorer and the service agree
        // on where results live.
        FClient.StartService(TPath.GetFullPath(LPath), GSettings.SessionsRoot);
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
  SetLength(FMonitorSamples, 0);
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
  PollCounters;
  UpdateButtons(LStatus.State);
  SetStatus(Format('session %s: %s', [FSessionId, LStatus.State]));
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
