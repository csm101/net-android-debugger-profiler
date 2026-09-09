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
  System.Generics.Collections,
  Winapi.Windows, Winapi.Messages, Winapi.ShellAPI,
  Vcl.Graphics, Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.StdCtrls, Vcl.ExtCtrls, Vcl.ComCtrls, Vcl.Menus,
  Vcl.FileCtrl,
  System.Variants, System.IOUtils, System.StrUtils, System.IniFiles, Data.DB, FireDAC.Comp.Client,
  dxCore, cxGraphics, cxControls, cxLookAndFeels, cxLookAndFeelPainters, cxStyles, cxClasses, cxScrollBar,
  cxCustomData, cxFilter, cxData, cxDataStorage, cxEdit, cxNavigator, cxDataControllerConditionalFormattingRulesManagerDialog,
  cxGridLevel, cxGridCustomTableView, cxGridTableView, cxGridDBTableView, cxGridCustomView, cxGrid,
  cxGridExportLink, cxFindPanel, cxTLExportLink,
  cxLabel, cxButtons, cxDropDownEdit, cxMemo, cxPC, cxCheckBox, cxSplitter, cxScrollBox,
  cxProgressBar, cxTextEdit,
  cxTL, cxTLdxBarBuiltInMenu, cxInplaceContainer, cxTLData,
  dxBar, dxBarExtItems, dxStatusBar, Vcl.ImgList,
  dxDockControl, dxDockPanel, dxPanel, dxMessageDialog, dxInputDialogs,
  dxSkinsCore, dxSkinsDefaultPainters, dxSkinsForm,
  dxSkinOffice2019Colorful, dxSkinOffice2019Black,
  SynEdit, SynEditHighlighter, SynHighlighterCS, SynEditTypes, SynFunc,
  uSessionStore, uSessionSpec, uControlClient, uSetupDialog, uJobDialog, uTheme, uSettings, uSettingsDialog,
  uLayouts, uLayoutDialog, uGlyphs, uGuiRender;

type
  TExplorerKind = (ekSession, ekCategory, ekArchive, ekGroup);

  {
    One box of the call graph. The graph is a tree grown from the method in focus: its
    callers in the column to the left, its callees to the right, and each callee carrying
    a [+] that opens its own callees in the next column. Nothing is expanded by itself
    beyond the first level - a real application's call graph is unreadable when it is
    complete, and useful when somebody follows one branch of it.
  }
  TGraphNode = record
    MethodId: Integer;
    Name: string;
    /// 0 the method in focus, 1.. its callees by depth, -1 a caller.
    Depth: Integer;
    Stats: TMethodStats;
    Expanded: Boolean;
    HasCallees: Boolean;
    HiddenChildren: Integer;
    Box, Toggle: TRect;
    /// Where the arrows out of this box turn: its own vertical line in the gap to the next
    /// column. One per box, at its own distance from it - two boxes sharing a line is how a
    /// picture ends up saying that both of them call both callees.
    Trunk: Integer;
  end;

  /// A call from one box to another. A method reached from two places is one box with two
  /// arrows into it, which is how AQTime draws it and what makes "this is called from
  /// three points of this branch" visible at a glance.
  TGraphEdge = record
    FromNode, ToNode: Integer;
    Calls, Value: Int64;
  end;

  /// A row of the Explorer: a result database to open, or a way to regroup the Report.
  TExplorerRef = record
    Kind: TExplorerKind;
    DatabasePath: string;
    Category: string;
  end;

  TMainForm = class(TForm)
  private
    FStore: TSessionStore;
    FBarManager: TdxBarManager;
    FGlyphs: TImageList;
    FSuppressCombo: Boolean;
    FFileMenu: TdxBarSubItem;
    FRefreshButton: TdxBarButton;
    FStatus: TdxStatusBar;
    FUnits: TdxBarCombo;
    FSettingsButton: TdxBarButton;
    FExportButton: TdxBarButton;
    /// The Layouts entry is a menu: the saved arrangements, the one marked as the
    /// default, and what can be done with them. Its items are rebuilt on every popup,
    /// so FLayoutItems holds them to be freed before the next build.
    FLayoutMenu: TdxBarSubItem;
    FLayoutItems: TList;
    FLayoutNames: TArray<string>;
    /// The window was on screen at least once: only then does its arrangement mean
    /// anything worth writing back.
    FWasShown: Boolean;
    /// The arrangement was deliberately thrown away: do not write it back on the way out.
    FForgetLayout: Boolean;
    FSkinController: TdxSkinController;
    FSummary: TcxMemo;
    FMonitor: TPaintBox;
    FMonitorLabel: TcxLabel;
    FMonitorSamples: TArray<Int64>;
    FMonitorLast: TSessionCounters;
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
    /// What a node of the Explorer stands for. The tree mixes sessions, the categories
    /// of the open one and the archived results of each, so the node carries its meaning
    /// instead of it being guessed from the node's level.
    FExplorerRefs: TArray<TExplorerRef>;
    FExplorer: TcxTreeList;
    FExplorerColumn: TcxTreeListColumn;
    FSessionsRoot: string;
    FActivePanel: string;
    FBuilt: Boolean;
    FPendingRender: string;
    /// --expand=<levels>: how far to open the call graph before the picture is taken.
    FPendingExpand: Integer;
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
    FGraph: TPaintBox;
    FGraphScroll: TcxScrollBox;
    FGraphMethodId: Integer;

    FGraphHistory: TArray<Integer>;
    FGraphParentsHidden: Integer;
    FGraphChildrenHidden: Integer;
    FGraphNodes: TArray<TGraphNode>;
    FGraphEdges: TArray<TGraphEdge>;
    /// The methods whose callees are open. A method is one box, so being open is a
    /// property of the method and not of the way somebody arrived at it.
    FGraphOpen: TStringList;
    FEditor: TSynEdit;
    FEditorHeader: TcxLabel;
    FEditorHighlighter: TSynCSSyn;
    FEditorScrollV: TcxScrollBar;
    FEditorScrollH: TcxScrollBar;
    FEditorScrollHost: TdxPanel;
    FEditorScrollCorner: TdxPanel;
    FUpdatingEditorScrollBars: Boolean;
    FEditorFile: string;
    FEditorStart: Integer;
    FEditorEnd: Integer;
    FDetailsSplitter: TcxSplitter;
    FGrid: TcxGrid;
    FGridView: TcxGridDBTableView;
    FGridLevel: TcxGridLevel;
    FReportQuery: TFDQuery;
    FReportSource: TDataSource;
    FDetailsPanel: TdxPanel;
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
    /// The result database of the session this window started, empty when none is running.
    FLiveDatabasePath: string;
    FPoll: TTimer;
    FExplorerMenu: TdxBarPopupMenu;
    FSnapshotButton: TdxBarButton;
    FArchiveButton: TdxBarButton;
    FPauseButton: TdxBarButton;
    FRecordButton: TdxBarButton;
    FRunAgainButton: TdxBarButton;
    FStopButton: TdxBarButton;
    FClearButton: TdxBarButton;
    FPaused: Boolean;
    /// True while the running session is one the live controls can act on.
    FLiveControllable: Boolean;
    FPendingDialog: string;

    /// Said once per session, in the log, rather than on every state poll.
    FLiveExplained: Boolean;
    FBuildStarted: UInt64;
    FBuildFinished: UInt64;
    /// Lines written while the Log panel had no window: kept until it has one.
    FPendingLog: TStringList;
    /// The same, for the Summary panel.
    FSummaryPending: TStringList;
    FLog: TcxMemo;
    procedure BuildToolbar;
    procedure BuildExplorer;
    procedure BuildFileMenu(ABar: TdxBar);
    /// Every folder the Explorer lists sessions from: the standard one, and the ones a
    /// session was deliberately saved in.
    function SessionFolders: TArray<string>;
    function FocusedSession(out AEntry: TExplorerRef): Boolean;
    procedure ExplorerMouseDown(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
    procedure ExplorerMenuPopup(Sender: TObject);
    procedure ExplorerOpenClick(Sender: TObject);
    procedure ExplorerRenameClick(Sender: TObject);
    procedure ExplorerDeleteClick(Sender: TObject);
    procedure ExplorerRevealClick(Sender: TObject);
    procedure RefreshExplorerClick(Sender: TObject);
    procedure AddSessionFolderClick(Sender: TObject);
    procedure ExitClick(Sender: TObject);
    function AddDockPanel(const ACaption: string; ATarget: TdxCustomDockControl;
      AType: TdxDockingType): TdxDockPanel;
    procedure SaveLayout;
    procedure LoadLayout;
    function LayoutFile: string;
    procedure ApplyBuiltInSizes;
    procedure ApplyBuiltInLayout;
    function HasPanels: Boolean;
    procedure LogLayoutProblem(const AText: string);
    procedure BuildLayoutMenu(ABar: TdxBar);
    procedure UpdateLayoutMenu(Sender: TObject);
    procedure ApplyNamedLayout(const AName: string);
    procedure LayoutMenuItemClick(Sender: TObject);
    procedure LayoutSaveCurrentClick(Sender: TObject);
    procedure LayoutDefaultClick(Sender: TObject);
    procedure LayoutBuiltInClick(Sender: TObject);
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
    procedure ShowPreferencesInToolbar;
    procedure SetStatus(const AText: string);
    procedure ApplyTheme;
    procedure RecolourGlyphs;
    procedure SettingsClick(Sender: TObject);
    procedure LayoutsClick(Sender: TObject);
    procedure ExportClick(Sender: TObject);
    procedure ExportGrid(AGrid: TcxGrid; const AFileName: string);
    function FocusedGrid: TcxGrid;
    procedure ApplyCodeFont;
    procedure ShowPendingDialog(Sender: TObject);

    procedure ExplainLiveButtons(ARunning: Boolean);
    procedure LogLine(const AText: string);
    procedure SetSummary(ALines: TStrings);
    procedure FlushSummary;
    procedure SummaryPanelActivated(Sender: TdxCustomDockControl; AActive: Boolean);
    function AddExplorerRef(AKind: TExplorerKind; const APath, ACategory: string): Pointer;
    procedure ReloadExplorer;
    procedure ExplorerDblClick(Sender: TObject);
    procedure BuildReportTab;
    procedure BuildTreeTab;
    procedure BuildGraphTab;
    procedure PaintGraph(Sender: TObject);
    procedure BuildGraphNodes;
    procedure LayoutGraphNodes;
    function AddGraphNode(AMethodId: Integer; const AName: string; ADepth: Integer): Integer;
    function GraphNodeOf(AMethodId: Integer): Integer;
    procedure DrawGraphNode(ACanvas: TCanvas; const ANode: TGraphNode);
    procedure DrawGraphEdge(ACanvas: TCanvas; const AEdge: TGraphEdge; AMax: Int64);
    procedure GraphResized(Sender: TObject);
    function GraphValueLine(const ACaption: string; AValue: Int64): string;
    procedure GraphMouseDown(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
    procedure GraphMouseMove(Sender: TObject; Shift: TShiftState; X, Y: Integer);
    procedure ShowGraphOf(AMethodId: Integer);
    procedure GraphBack;
    procedure BuildEditorTab;
    procedure EditorStatusChanged(Sender: TObject; Changes: TSynStatusChanges);
    procedure EditorScrollBarScrolled(Sender: TObject; AScrollCode: TScrollCode; var AScrollPos: Integer);
    procedure UpdateEditorScrollBars;
    procedure EditorResized(Sender: TObject);
    procedure DockLayoutChanged(Sender: TdxCustomDockControl);
    function EditorLongestLine: Integer;
    procedure ShowSourceOf(AMethodId: Integer);
    procedure EditorSpecialLineColors(Sender: TObject; Line: TSynNativeInt;
      var Special: Boolean; var FG, BG: TColor);
    function BuildNeighbourGrid(AParent: TWinControl; AAlign: TAlign; const ACaption: string;
      AIsParents: Boolean; out AView: TcxGridTableView): TcxGrid;
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
    procedure RunAgainClick(Sender: TObject);
    procedure ExplorerRunAgainClick(Sender: TObject);
    /// Opens the setup dialog on the session in that directory and starts what it answers.
    procedure RunAgain(const ASessionDirectory: string);
    /// The directory of the session whose results are open, empty when none is.
    function OpenSessionDirectory: string;
    function StartSessionFrom(const ARequest: TSessionRequest; APrefilled: Boolean): Boolean;
    procedure SnapshotButtonClick(Sender: TObject);
    procedure ArchiveButtonClick(Sender: TObject);
    procedure ClearButtonClick(Sender: TObject);
    procedure PauseButtonClick(Sender: TObject);
    procedure RecordButtonClick(Sender: TObject);
    procedure StopButtonClick(Sender: TObject);
    procedure PollTimer(Sender: TObject);
    procedure UpdateButtons(const AState: string);
    function EnsureService: Boolean;
    procedure CheckPrerequisites;
    procedure ShowLog(const ALines: TArray<string>);
    procedure NanosecondDisplayText(Sender: TcxCustomGridTableItem;
      ARecord: TcxCustomGridRecord; var AText: string);
    procedure FillNeighbours(AView: TcxGridTableView; const AItems: TNeighbours);
    procedure PaintParentsPie(Sender: TObject);
    procedure PaintChildrenPie(Sender: TObject);
    procedure PaintShares(ACanvas: TCanvas; const ARect: TRect; const AShares: TArray<Int64>);
    function FocusedMethodId: Integer;
    function PanelByName(const AName: string): TdxDockPanel;
    function ReportColumn(const AField: string): TcxGridDBColumn;
    function ResolveSessionPath(const APath: string): string;
    procedure RenderPanelToFile(const APanelAndFile: string);
    procedure GiveRoomTo(const APanel: string);
    function FocusFoundRow: Boolean;
    procedure RenderPending;
    procedure ReportRenderFailure(const AMessage: string);
    procedure UpdateInfo;
    function ValueCaption: string;
  protected
    /// The whole window is built inside the constructor, so its handle is created before
    /// Application.CreateForm has had a chance to say that this is the main form - and a
    /// VCL form that is not (yet) the main form is created without WS_EX_APPWINDOW and
    /// owned by the hidden application window. That is why the profiler had no taskbar
    /// button. Saying it here settles it whatever the order.
    procedure CreateParams(var Params: TCreateParams); override;
    /// A window that was never on screen has no arrangement of its own: whatever it is
    /// holding is what the code built or what a picture needed. This is what says the
    /// arrangement is the user's, and so worth saving.
    procedure DoShow; override;
  public
    constructor Create(AOwner: TComponent); override;
    destructor Destroy; override;

    { What the control channel (uGuiControl) drives: the same operations the mouse
      performs, named, so that an agent can open a session, look at a panel and be
      handed a picture of it without a window on screen. Main thread only. }
    function ActivatePanel(const AName: string): Boolean;
    function ActivePanelName: string;
    function PanelNames: string;
    function PanelControl(const AName: string): TControl;
    procedure OpenSessionPath(const APath: string);
    function FocusMethodByName(const AName: string): Boolean;
    procedure FilterReport(const AText: string);
    procedure SortReportBy(const AField: string; ADescending: Boolean);
    procedure SetWindowVisible(AVisible: Boolean);
    procedure ResizeClient(AWidth, AHeight: Integer);
    function CurrentSessionPath: string;
    function CurrentMethodName: string;
    /// Open the call graph's branches down to this many levels of callees, the way clicking
    /// every [+] would. What a picture of "the graph two levels deep" needs, and what an
    /// agent asks for when it wants one.
    procedure ExpandGraph(ALevels: Integer);
  end;

var
  MainForm: TMainForm;
  /// Set by the program in --control mode. A window that is being driven must not
  /// write the layout it was given for a picture over the one its owner arranged.
  GDrivenWindow: Boolean = False;
  /// Set by the program before anything else, so the log can say how long starting took.
  /// A number in the log beats an argument about whether it "feels" slow.
  GStartTicks: UInt64 = 0;

implementation

const
  /// Node.Data marks the critical path: the heaviest child of its parent.
  DataCritical: Pointer = Pointer(1);

procedure TMainForm.CreateParams(var Params: TCreateParams);
begin
  inherited CreateParams(Params);
  Params.ExStyle := Params.ExStyle or WS_EX_APPWINDOW;
  Params.WndParent := 0;
end;

constructor TMainForm.Create(AOwner: TComponent);
var
  LTab: string;
  LIndex: Integer;
begin
  inherited CreateNew(AOwner);
  // The window needs a name of its own, and this is not cosmetic: a saved docking layout
  // records each control's form as `ParentForm=<the form's Name>`, and the loader skips
  // every section whose ParentForm it cannot resolve. A form built with CreateNew has no
  // Name, so the file was written with an empty one and nothing in it was ever loaded
  // back - the arrangement was cleared and the window came up bare, every single time.
  Name := 'MainForm';
  FBuildStarted := GetTickCount64;
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
  FDockManager.OnLayoutChanged := DockLayoutChanged;
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
  ApplyBuiltInSizes;

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
  // An empty setting means "wherever the service keeps them", and that is a real place:
  // without this the Explorer is empty on a machine that has never been to Settings,
  // which reads as "the profiler found none of my sessions".
  if GSettings.SessionsRoot <> '' then
    FSessionsRoot := GSettings.SessionsRoot
  else if GetEnvironmentVariable('NAP_SESSIONS_ROOT') <> '' then
    FSessionsRoot := GetEnvironmentVariable('NAP_SESSIONS_ROOT')
  else
    FSessionsRoot := TPath.Combine(TPath.Combine(GetEnvironmentVariable('LOCALAPPDATA'),
      'net-android-profiler'), 'sessions');
  ReloadExplorer;
  ShowPreferencesInToolbar;
  // A driven window renders from the arrangement this code just built, not from the one
  // somebody left behind: a picture should not depend on where a panel was last dragged.
  if not GDrivenWindow then
    LoadLayout;
  ApplyTheme;
  ApplyCodeFont;

  FClient := TControlClient.Create;
  FPoll := TTimer.Create(Self);
  FPoll.Interval := 1000;
  FPoll.Enabled := False;
  FPoll.OnTimer := PollTimer;
  FBuildFinished := GetTickCount64;
  OnShow := ShowPendingDialog;
  OnResize := EditorResized;

  // A session that cannot be opened is a message, not the end of the window: the log
  // panel says what was wrong and File > Open still works.
  if (ParamCount >= 1) and not ParamStr(1).StartsWith('--') then
    try
      LoadSession(ParamStr(1));
    except
      on E: Exception do
        LogLine(E.Message);
    end;
  // --tab=<report|tree|source> selects the visible panel: handy for a screenshot or a
  // shortcut that always opens where you left off.
  // --dialog=<settings|layouts|setup> opens one straight away: it is how the dialogs
  // get exercised without a hand on the mouse.
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--dialog=', True) then
      FPendingDialog := ParamStr(LIndex).Substring(9);
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--tab=', True) then
    begin
      LTab := ParamStr(LIndex).Substring(6);
      if not ActivatePanel(LTab) then
        ActivatePanel('report');
    end;
  // --export=<file> writes the report of the session given on the command line and
  // quits: the same export the button performs, available to a build script. It runs
  // with the rest of the startup work, because it has nothing to export until the
  // session named on the command line has been read.
  // --render=<panel>:<file.png> writes a picture of one panel and quits: the channel's
  // rendering, available to a script that has no channel.
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--render=', True) then
      FPendingRender := ParamStr(LIndex).Substring(9)
    else if ParamStr(LIndex).StartsWith('--expand=', True) then
      FPendingExpand := StrToIntDef(ParamStr(LIndex).Substring(9), 0);
  for LIndex := 1 to ParamCount do
    if ParamStr(LIndex).StartsWith('--export=', True) then
    begin
      ExportGrid(FGrid, ParamStr(LIndex).Substring(9));
      Application.ShowMainForm := False;
      PostMessage(Handle, WM_CLOSE, 0, 0);
    end;
  UpdateInfo;
  FBuilt := True;
  if FPendingRender <> '' then
  begin
    Application.ShowMainForm := False;
    // Once the message loop is running the panels have their windows; rendering from
    // inside the constructor is what fails, quietly and confusingly.
    TThread.ForceQueue(nil, RenderPending);
  end;
end;

/// The --render= command line, performed once the window is up: one picture, then out.
procedure TMainForm.RenderPending;
begin
  try
    if FPendingExpand > 0 then
      ExpandGraph(FPendingExpand);
    RenderPanelToFile(FPendingRender);
  except
    on E: Exception do
      ReportRenderFailure(E.Message);
  end;
  PostMessage(Handle, WM_CLOSE, 0, 0);
end;

/// A failed --render has nobody to tell: the window is not shown and stdout belongs to
/// the channel. The log file beside the exe is where a script looks.
procedure TMainForm.ReportRenderFailure(const AMessage: string);
var
  LFile: string;
begin
  LFile := TPath.ChangeExtension(ParamStr(0), '.render.log');
  TFile.AppendAllText(LFile, Format('%s  %s' + sLineBreak, [DateTimeToStr(Now), AMessage]));
end;

destructor TMainForm.Destroy;
var
  I: Integer;
begin
  FPendingLog.Free;
  FSummaryPending.Free;
  FGraphOpen.Free;
  FLayoutItems.Free;
  // Only a window somebody actually looked at has an arrangement worth remembering.
  // A run that never showed one - --export, --render, the control channel - is holding
  // whatever the code built or a picture needed, and writing that back is how the
  // window came up empty the next time it was opened by hand.
  if FWasShown and not FForgetLayout then
    SaveLayout;
  if not GDrivenWindow then
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
    // A toolbar is part of the window, not a thing to take apart: it cannot be closed,
    // cannot be customized, and (see the end of this method) cannot be moved or torn off
    // into a floating window.
    Result.AllowClose := False;
    Result.AllowCustomizing := False;
    Result.AllowQuickCustomizing := False;
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
  // Starting a session and opening one are File commands: they are how work begins, not
  // controls of the run. What stays on the bar is what acts on the session in progress.
  BuildFileMenu(LSession);
  FRefreshButton := NewButton(LSession, 'Refresh',
    'Re-read the open session from disk', gkRefresh, RefreshButtonClick, True);
  FRefreshButton.ShortCut := TextToShortCut('F5');
  // AQTime's name for it, and the better one: the button does not take a picture of
  // anything, it produces the results of what has been collected so far.
  FSnapshotButton := NewButton(LSession, 'Get Results',
    'Produce the results from what has been collected so far, without stopping the app',
    gkSnapshot, SnapshotButtonClick);
  FArchiveButton := NewButton(LSession, 'Archive...',
    'Keep the results as they are now, under a name, and go on profiling: they stay in the '
    + 'Explorer and open again whenever you want',
    gkArchive, ArchiveButtonClick);
  FRunAgainButton := NewButton(LSession, 'Run again...',
    'Profile the same thing once more: the setup of the session you are looking at, ready to start',
    gkRun, RunAgainClick);
  FRecordButton := NewButton(LSession, 'Record',
    'Start measuring now: the app has been running unmeasured, and what follows is what the results hold',
    gkRun, RecordButtonClick);
  FPauseButton := NewButton(LSession, 'Pause',
    'Stop recording without stopping the app: the methods stay instrumented, so their overhead remains',
    gkPause, PauseButtonClick);
  FClearButton := NewButton(LSession, 'Clear',
    'Throw away what has been collected so far and keep going - the instrumentation stays in place',
    gkClear, ClearButtonClick);
  FStopButton := NewButton(LSession, 'Stop', 'End the session and analyse what it collected',
    gkStop, StopButtonClick);

  LView := NewBar('View');
  FSettingsButton := NewButton(LView, 'Settings...',
    'Theme, units, the font code is read in, and where sessions are kept', gkSettings,
    SettingsClick);
  BuildLayoutMenu(LView);
  FExportButton := NewButton(LView, 'Export...',
    'Write the table you are looking at to a spreadsheet or a text file', gkExport,
    ExportClick);

  // The theme is not here: it is chosen once, and Settings is where a choice made once
  // belongs. The unit times are shown in stays, because it is changed while reading a
  // result - "is that 12 ms or 12 us" is a question about the row under the cursor, not
  // a preference - and walking through a dialog to answer it every time is the sort of
  // friction that makes people stop asking.
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
  // Without this a dxStatusBar paints the classic Windows style whatever the skin says:
  // stpsStandard is its default, and it is the one style that ignores the look and feel.
  FStatus.PaintStyle := stpsUseLookAndFeel;
  FStatus.Align := alBottom;
  FStatus.Panels.Add.Fixed := False;
  SetStatus('No session open.');

  // Nailed down, after the bars have been docked where they belong: with every docking
  // style refused - dsNone, which is what dxBar calls floating, included - TdxBar.CanMoving
  // is false, so the bars cannot be dragged along the row, moved to another edge, or torn
  // off into a floating window. A toolbar that floats is a way for a window to be broken
  // by accident, and there is nothing on the other side of the trade.
  FBarManager.NotDocking := [Low(TdxBarDockingStyle)..High(TdxBarDockingStyle)];

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
  // An arrangement with no panels in it is not an arrangement, it is an accident - and
  // writing it makes the next start empty too, which is how one bad moment became
  // permanent. The last good file stays where it is.
  if not HasPanels then
  begin
    LogLayoutProblem('the window had no panels: the saved arrangement was left as it was');
    Exit;
  end;
  try
    FDockManager.SaveLayoutToIniFile(LayoutFile);
  except
    // a layout that cannot be saved is not worth an error dialog on the way out
  end;
end;

{ A layout that goes wrong does it before there is a window to say so in: the log file
  beside the executable is where the reason survives. }
procedure TMainForm.LogLayoutProblem(const AText: string);
begin
  try
    TFile.AppendAllText(TPath.ChangeExtension(ParamStr(0), '.layout.log'),
      Format('%s  %s' + sLineBreak, [DateTimeToStr(Now), AText]));
  except
    // diagnostics must not be the thing that breaks the start-up
  end;
end;

{ Restoring an arrangement can fail in two ways, and only one of them raises: the load
  throws, or it "succeeds" and leaves a window with no panels in it. Both end the same
  way - the built-in arrangement - because an empty grey rectangle is never what anybody
  wanted, and it is not something a person can fix from inside the window. }
procedure TMainForm.LoadLayout;

  function Restored(const AWhat: string; ALoad: TProc): Boolean;
  begin
    Result := False;
    try
      ALoad;
    except
      on E: Exception do
      begin
        LogLayoutProblem(AWhat + ' not restored: ' + E.Message);
        LogLine(AWhat + ' not restored: ' + E.Message);
        Exit;
      end;
    end;
    Result := HasPanels;
    if not Result then
    begin
      // The docking library finishes a load through the message queue - the containers it
      // reads are built as the form settles - so what it has just read is not necessarily
      // in place the instant the call returns. Asking twice is the difference between
      // restoring somebody's arrangement and throwing it away every single start.
      Application.ProcessMessages;
      Result := HasPanels;
    end;
    if not Result then
    begin
      LogLayoutProblem(AWhat + ' loaded but left the window without panels');
      LogLine(AWhat + ' left the window empty: the built-in arrangement is being used instead');
    end;
  end;

begin
  // A named layout marked as the one to open with wins; otherwise the window comes
  // back the way it was closed.
  if LayoutExists(GSettings.DefaultLayout) then
    if Restored('layout "' + GSettings.DefaultLayout + '"',
      procedure begin LoadNamedLayout(GSettings.DefaultLayout); end) then
      Exit;
  if TFile.Exists(LayoutFile) then
    if Restored('the last arrangement',
      procedure begin FDockManager.LoadLayoutFromIniFile(LayoutFile); end) then
      Exit;
  // Nothing was restored, or what was restored is unusable. The panels were docked by the
  // constructor before this ran, so they only need putting back when a failed load moved
  // them; when nothing was tried at all this costs one re-dock and changes nothing.
  ApplyBuiltInLayout;
end;

{ The Layouts entry of the toolbar is a menu, the way an IDE keeps its window
  arrangements: the saved layouts first, the one the window opens with marked, then what
  can be done with them. The items are rebuilt every time it drops down, so a layout
  saved or deleted meanwhile is in the list without a restart. }
procedure TMainForm.BuildLayoutMenu(ABar: TdxBar);
begin
  FLayoutItems := TList.Create;
  FLayoutMenu := TdxBarSubItem(FBarManager.AddItem(TdxBarSubItem));
  FLayoutMenu.Caption := 'Layouts';
  FLayoutMenu.Hint := 'Panel arrangements: load one, save this one, pick the one to open with';
  FLayoutMenu.ImageIndex := Ord(gkLayouts);
  FLayoutMenu.ShowCaption := True;
  FLayoutMenu.OnPopup := UpdateLayoutMenu;
  ABar.ItemLinks.Add.Item := FLayoutMenu;
  UpdateLayoutMenu(nil);
end;

procedure TMainForm.UpdateLayoutMenu(Sender: TObject);

  function NewItem(AParent: TCustomdxBarSubItem; const ACaption: string; AClick: TNotifyEvent;
    ATag: Integer = 0; ABeginGroup: Boolean = False): TdxBarButton;
  var
    LLink: TdxBarItemLink;
  begin
    Result := TdxBarButton.Create(FBarManager);
    Result.Category := 0;
    Result.Visible := ivAlways;
    Result.Caption := ACaption;
    Result.Tag := ATag;
    Result.OnClick := AClick;
    FLayoutItems.Add(Result);
    LLink := AParent.ItemLinks.Add;
    LLink.Item := Result;
    LLink.BeginGroup := ABeginGroup;
  end;

  procedure ShowTick(AItem: TdxBarButton; AIsOn: Boolean);
  begin
    AItem.ButtonStyle := bsChecked;
    AItem.Down := AIsOn;
  end;

var
  LOpenWith: TdxBarSubItem;
  LItem: TdxBarButton;
  LIndex: Integer;
begin
  if FLayoutMenu = nil then
    Exit;
  FLayoutMenu.ItemLinks.Clear;
  for LIndex := 0 to FLayoutItems.Count - 1 do
    TdxBarItem(FLayoutItems[LIndex]).Free;
  FLayoutItems.Clear;

  FLayoutNames := LayoutNames;
  for LIndex := 0 to High(FLayoutNames) do
  begin
    LItem := NewItem(FLayoutMenu, FLayoutNames[LIndex], LayoutMenuItemClick, LIndex);
    if SameText(FLayoutNames[LIndex], GSettings.DefaultLayout) then
      LItem.Caption := LItem.Caption + '  [default]';
  end;
  if Length(FLayoutNames) = 0 then
  begin
    LItem := NewItem(FLayoutMenu, 'No layouts saved yet', nil);
    LItem.Enabled := False;
  end;

  NewItem(FLayoutMenu, '&Save this arrangement as...', LayoutSaveCurrentClick, 0, True);

  // Which layout the window opens with is a choice about all of them, so it is a list of
  // its own rather than a command that acts on whatever happens to be selected.
  LOpenWith := TdxBarSubItem(FBarManager.AddItem(TdxBarSubItem));
  LOpenWith.Caption := '&Open the window with';
  FLayoutItems.Add(LOpenWith);
  FLayoutMenu.ItemLinks.Add.Item := LOpenWith;
  ShowTick(NewItem(LOpenWith, 'The arrangement you left', LayoutDefaultClick, -1),
    GSettings.DefaultLayout = '');
  for LIndex := 0 to High(FLayoutNames) do
    ShowTick(NewItem(LOpenWith, FLayoutNames[LIndex], LayoutDefaultClick, LIndex),
      SameText(FLayoutNames[LIndex], GSettings.DefaultLayout));

  LItem := NewItem(FLayoutMenu, '&Manage layouts...', LayoutsClick);
  LItem.Enabled := Length(FLayoutNames) > 0;
  NewItem(FLayoutMenu, 'Back to the &built-in arrangement', LayoutBuiltInClick, 0, True);
end;

procedure TMainForm.ApplyNamedLayout(const AName: string);
begin
  try
    LoadNamedLayout(AName);
    FForgetLayout := False;
    SetStatus('Layout "' + AName + '" applied.');
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.LayoutMenuItemClick(Sender: TObject);
var
  LIndex: Integer;
begin
  LIndex := TdxBarButton(Sender).Tag;
  if (LIndex < 0) or (LIndex > High(FLayoutNames)) then
    Exit;
  ApplyNamedLayout(FLayoutNames[LIndex]);
end;

procedure TMainForm.LayoutSaveCurrentClick(Sender: TObject);
var
  LName: string;
begin
  LName := '';
  if not dxInputQuery('Save layout', 'A name for this arrangement:', LName) then
    Exit;
  LName := Trim(LName);
  if LName = '' then
    Exit;
  if LayoutExists(LName) and (dxMessageDlg(Format('There is already a layout called "%s". Replace it?',
    [LName]), mtConfirmation, [mbYes, mbNo], 0) <> mrYes) then
    Exit;
  try
    SaveLayoutAs(LName);
    FForgetLayout := False;
    SetStatus('Layout saved as "' + LName + '".');
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
  UpdateLayoutMenu(nil);
end;

procedure TMainForm.LayoutDefaultClick(Sender: TObject);
var
  LIndex: Integer;
begin
  LIndex := TdxBarButton(Sender).Tag;
  if LIndex < 0 then
  begin
    uLayouts.SetDefaultLayout('');
    SetStatus('The window will open the way you left it.');
  end
  else if LIndex <= High(FLayoutNames) then
  begin
    uLayouts.SetDefaultLayout(FLayoutNames[LIndex]);
    SetStatus('The window will open with the layout "' + FLayoutNames[LIndex] + '".');
  end;
  UpdateLayoutMenu(nil);
end;

{ The panel sizes of the built-in arrangement. Kept apart from the docking because the
  constructor docks the panels as it creates them, while the reset re-docks panels that
  already exist - the sizes are the same either way. }
procedure TMainForm.ApplyBuiltInSizes;
begin
  FExplorerPanel.Width := 280;
  // The bottom strip is a tab container by now, and a panel inside one does not carry
  // its own height: size the container, or the call views open as a 100px sliver.
  if FDetailsDock.ParentDockControl <> nil then
    FDetailsDock.ParentDockControl.Height := 320
  else
    FDetailsDock.Height := 320;
end;

{ Back to the factory arrangement, on screen and now - this is what somebody reaches for
  after making a mess of the panels, and "it will be right the next time you start" is not
  that. The panels are taken apart first: docking one that is still tabbed with another
  moves its container instead of the panel, and the arrangement comes out half-built.
  The order below is the constructor's, and has to stay the same as it. }
procedure TMainForm.ApplyBuiltInLayout;

  procedure Attach(APanel: TdxDockPanel; ATarget: TdxCustomDockControl; AType: TdxDockingType);
  var
    LTarget: TdxCustomDockControl;
  begin
    // Hidden or auto-hidden panels come back: this is the arrangement everything starts in.
    if APanel.AutoHide then
      APanel.AutoHide := False;
    APanel.Visible := True;
    LTarget := ATarget;
    // Tabbing onto a panel that already has tabs means joining the container, not the
    // panel: docking to the panel again leaves this one homeless.
    if (AType = dtClient) and (ATarget is TdxDockPanel) and (TdxDockPanel(ATarget).TabContainer <> nil) then
      LTarget := TdxDockPanel(ATarget).TabContainer;
    APanel.DockTo(LTarget, AType, 0);
  end;

var
  LPanel: TdxDockPanel;
begin
  // Docked straight to where they belong, in the constructor's order. An earlier version
  // undocked everything first "to start clean" and left the window with no panels at all
  // when a re-dock did not take - which is the one outcome a "back to the built-in
  // arrangement" command must never produce.
  DisableAlign;
  try
    Attach(FReportPanel, FDockSite, dtClient);
    Attach(FExplorerPanel, FDockSite, dtLeft);
    Attach(FDetailsDock, FDockSite, dtBottom);
    for LPanel in [FTreePanel, FGraphPanel, FSourcePanel, FMemoryPanel, FMonitorPanel,
      FSummaryPanel, FLogPanel] do
      Attach(LPanel, FDetailsDock, dtClient);
    ApplyBuiltInSizes;
  finally
    EnableAlign;
  end;
  FReportPanel.Activate;
end;

/// Whether there is a window to speak of: a dock site with nothing in it is the empty
/// grey rectangle, not an arrangement.
function TMainForm.HasPanels: Boolean;
begin
  Result := (FDockSite <> nil) and (FDockSite.ChildCount > 0);
end;

procedure TMainForm.LayoutBuiltInClick(Sender: TObject);
begin
  if dxMessageDlg('Put the panels back the way the profiler starts out?' + sLineBreak
    + 'The saved layouts are kept, but the window stops opening with one of them.',
    mtConfirmation, [mbOK, mbCancel], 0) <> mrOk then
    Exit;
  // A named layout marked as the one to open with would undo this at the next start,
  // which is not what "back to the built-in arrangement" can mean.
  uLayouts.SetDefaultLayout('');
  try
    ApplyBuiltInLayout;
  except
    on E: Exception do
    begin
      dxMessageDlg('The panels could not be rearranged: ' + E.Message + sLineBreak
        + 'Closing and reopening the window puts them back.', mtError, [mbOK], 0);
      // Whatever is on screen now is half-built: it must not be what the window opens
      // with, so the remembered arrangement goes and the built-in one is what is left.
      FForgetLayout := True;
      if TFile.Exists(LayoutFile) then
        TFile.Delete(LayoutFile);
    end;
  end;
  UpdateLayoutMenu(nil);
  SetStatus('The panels are back the way the profiler starts out.');
end;

procedure TMainForm.DoShow;
begin
  inherited;
  FWasShown := True;
end;

{ File: where work begins. A new session and an already collected one are the same kind
  of act - opening a document - and neither belongs among the buttons that control a run
  in progress. }
procedure TMainForm.BuildFileMenu(ABar: TdxBar);

  function NewItem(const ACaption, AHint: string; AGlyph: TGlyphKind; AClick: TNotifyEvent;
    const AShortCut: string = ''; ABeginGroup: Boolean = False): TdxBarButton;
  var
    LLink: TdxBarItemLink;
  begin
    Result := FBarManager.AddButton;
    Result.Caption := ACaption;
    Result.Hint := AHint;
    Result.ImageIndex := Ord(AGlyph);
    Result.OnClick := AClick;
    if AShortCut <> '' then
      Result.ShortCut := TextToShortCut(AShortCut);
    LLink := FFileMenu.ItemLinks.Add;
    LLink.Item := Result;
    LLink.BeginGroup := ABeginGroup;
  end;

begin
  FFileMenu := TdxBarSubItem(FBarManager.AddItem(TdxBarSubItem));
  FFileMenu.Caption := 'File';
  FFileMenu.ImageIndex := Ord(gkOpen);
  FFileMenu.ShowCaption := True;
  ABar.ItemLinks.Add.Item := FFileMenu;

  NewItem('&New session...',
    'Profile an app: pick the device, the mode and what to instrument', gkRun,
    StartButtonClick, 'Ctrl+N');
  NewItem('&Open session...', 'Open the results of a session already collected', gkOpen,
    OpenButtonClick, 'Ctrl+O');
  NewItem('Add a sessions &folder...',
    'Show the sessions kept in another folder - yours, or somebody else''s - in the Explorer',
    gkOpen, AddSessionFolderClick);
  NewItem('E&xit', 'Close the profiler', gkStop, ExitClick, '', True);
end;

procedure TMainForm.ExitClick(Sender: TObject);
begin
  Close;
end;

procedure TMainForm.BuildExplorer;

  procedure MenuItem(const ACaption: string; AGlyph: TGlyphKind; AClick: TNotifyEvent;
    ABeginGroup: Boolean = False);
  var
    LItem: TdxBarButton;
    LLink: TdxBarItemLink;
  begin
    LItem := FBarManager.AddButton;
    LItem.Caption := ACaption;
    LItem.ImageIndex := Ord(AGlyph);
    LItem.OnClick := AClick;
    LLink := FExplorerMenu.ItemLinks.Add;
    LLink.Item := LItem;
    LLink.BeginGroup := ABeginGroup;
  end;

begin
  FExplorer := TcxTreeList.Create(Self);
  FExplorer.Parent := FExplorerPanel;
  FExplorer.Align := alClient;
  FExplorer.OptionsData.Editing := False;
  FExplorer.OptionsSelection.CellSelect := False;
  FExplorer.OptionsView.Headers := False;
  FExplorer.OptionsView.ShowRoot := True;
  // Without this the column keeps the width it was created with, however wide the user
  // makes the panel.
  FExplorer.OptionsView.ColumnAutoWidth := True;
  FExplorer.OnDblClick := ExplorerDblClick;
  FExplorer.OnMouseDown := ExplorerMouseDown;
  FExplorerColumn := FExplorer.CreateColumn;
  FExplorerColumn.Caption.Text := 'Results';
  FExplorerColumn.Width := 260;

  { What can be done to a session, where a person looks for it: on the session itself.
    A dxBar popup rather than a VCL one, so it is drawn by the skin like everything else. }
  FExplorerMenu := TdxBarPopupMenu.Create(Self);
  FExplorerMenu.BarManager := FBarManager;
  FExplorerMenu.OnPopup := ExplorerMenuPopup;
  MenuItem('&Open', gkOpen, ExplorerOpenClick);
  MenuItem('&Run again...', gkRun, ExplorerRunAgainClick);
  MenuItem('&Rename...', gkArchive, ExplorerRenameClick);
  MenuItem('&Delete...', gkClear, ExplorerDeleteClick);
  MenuItem('Show in &folder', gkOpen, ExplorerRevealClick, True);
  MenuItem('Add a sessions folder...', gkOpen, AddSessionFolderClick);
  MenuItem('Refresh the list', gkRefresh, RefreshExplorerClick);
  FExplorer.PopupMenu := FExplorerMenu;
end;

/// A node's meaning, kept in an array the node indexes into. Index 0 means "nothing to
/// open": TcxTreeListNode.Data is nil on a node nobody assigned.
function TMainForm.AddExplorerRef(AKind: TExplorerKind; const APath, ACategory: string): Pointer;
var
  LRef: TExplorerRef;
begin
  LRef.Kind := AKind;
  LRef.DatabasePath := APath;
  LRef.Category := ACategory;
  FExplorerRefs := FExplorerRefs + [LRef];
  Result := Pointer(NativeInt(Length(FExplorerRefs)));
end;

{ Every folder sessions are listed from: the standard one plus the folders a session was
  deliberately saved in. A folder that is no longer there is skipped rather than reported:
  it is somebody's removed drive, not a fault of the profiler. }
function TMainForm.SessionFolders: TArray<string>;
begin
  Result := nil;
  if (FSessionsRoot <> '') and TDirectory.Exists(FSessionsRoot) then
    Result := [FSessionsRoot];
  for var LFolder in GSettings.SessionFolders do
    if TDirectory.Exists(LFolder) and not SameText(LFolder, FSessionsRoot) then
      Result := Result + [LFolder];
end;

{ The Explorer, grouped by what the sessions are of. A profiler used on more than one
  product otherwise shows a wall of timestamps in which yesterday's run of the thing you
  care about is indistinguishable from a test run of something else - so sessions sit
  under the solution they came from, falling back to the project and then to the package
  for sessions taken before a session recorded where it came from. }
procedure TMainForm.ReloadExplorer;

  function GroupOf(const AEntry: TSessionEntry): string;
  begin
    if AEntry.Solution <> '' then
      Exit(TPath.GetFileNameWithoutExtension(AEntry.Solution));
    if AEntry.Project <> '' then
      Exit(TPath.GetFileNameWithoutExtension(AEntry.Project));
    if AEntry.Package <> '' then
      Exit(AEntry.Package);
    Result := 'Sessions of unknown apps';
  end;

  function CaptionOf(const AEntry: TSessionEntry): string;
  begin
    if AEntry.Name <> '' then
      Result := Format('%s  (%s)', [AEntry.Name, AEntry.Mode])
    else
      Result := Format('%s  (%s)', [AEntry.Id, AEntry.Mode]);
  end;

var
  LSessions: TSessionEntries;
  LArchives: TArchiveEntries;
  LRoot, LGroup, LNode, LChild: TcxTreeListNode;
  LGroups: TDictionary<string, TcxTreeListNode>;
  LName: string;
  I, J: Integer;
begin
  FExplorer.BeginUpdate;
  LGroups := TDictionary<string, TcxTreeListNode>.Create;
  try
    FExplorer.Clear;
    FExplorerRefs := nil;
    LRoot := FExplorer.Add;
    LRoot.Values[0] := 'Sessions';
    LRoot.Data := nil;
    for var LFolder in SessionFolders do
    begin
      LSessions := ListSessions(LFolder);
      for I := 0 to High(LSessions) do
      begin
        LName := GroupOf(LSessions[I]);
        if not LGroups.TryGetValue(LName, LGroup) then
        begin
          LGroup := LRoot.AddChild;
          LGroup.Values[0] := LName;
          LGroup.Data := AddExplorerRef(ekGroup, '', LName);
          LGroups.Add(LName, LGroup);
        end;
        LNode := LGroup.AddChild;
        LNode.Values[0] := CaptionOf(LSessions[I]);
        LNode.Data := AddExplorerRef(ekSession, LSessions[I].DatabasePath, '');
        if SameText(LSessions[I].DatabasePath, FStore.Path) then
        begin
          // The open session shows the categories, like AQTime's Routines / Modules tree.
          LChild := LNode.AddChild;
          LChild.Values[0] := 'Routines';
          LChild.Data := AddExplorerRef(ekCategory, '', 'Routines');
          LChild := LNode.AddChild;
          LChild.Values[0] := 'Modules';
          LChild.Data := AddExplorerRef(ekCategory, '', 'Modules');
          LChild := LNode.AddChild;
          LChild.Values[0] := 'Source files';
          LChild.Data := AddExplorerRef(ekCategory, '', 'Source files');
        end;
        // The results that were kept during that session, whether or not it is still open.
        LArchives := ListArchives(TPath.GetDirectoryName(LSessions[I].DatabasePath));
        for J := 0 to High(LArchives) do
        begin
          LChild := LNode.AddChild;
          LChild.Values[0] := LArchives[J].Name;
          LChild.Data := AddExplorerRef(ekArchive, LArchives[J].DatabasePath, '');
        end;
        if LNode.Count > 0 then
          LNode.Expand(True);
      end;
    end;
    LRoot.Expand(True);
  finally
    LGroups.Free;
    FExplorer.EndUpdate;
  end;
end;

/// The reference of the focused node, whatever kind it is; False on the headings.
function TMainForm.FocusedSession(out AEntry: TExplorerRef): Boolean;
var
  LIndex: Integer;
begin
  AEntry := Default(TExplorerRef);
  if FExplorer.FocusedNode = nil then
    Exit(False);
  LIndex := Integer(NativeInt(FExplorer.FocusedNode.Data));
  if (LIndex < 1) or (LIndex > Length(FExplorerRefs)) then
    Exit(False);
  AEntry := FExplorerRefs[LIndex - 1];
  Result := True;
end;

/// A right-click acts on what is under the pointer, which is what everyone expects and
/// what a tree list does not do on its own.
procedure TMainForm.ExplorerMouseDown(Sender: TObject; Button: TMouseButton;
  Shift: TShiftState; X, Y: Integer);
begin
  if (Button = mbRight) and FExplorer.HitTest.HitAtNode then
    FExplorer.FocusedNode := FExplorer.HitTest.HitNode;
end;

procedure TMainForm.ExplorerMenuPopup(Sender: TObject);
var
  LRef: TExplorerRef;
  LFound, LIsSession, LIsResult: Boolean;
begin
  LFound := FocusedSession(LRef);
  LIsSession := LFound and (LRef.Kind = ekSession);
  LIsResult := LFound and (LRef.Kind in [ekSession, ekArchive]);
  // 0 Open, 1 Rename, 2 Delete, 3 Show in folder: the rest act on the list itself.
  FExplorerMenu.ItemLinks[0].Item.Enabled := LIsResult;
  // Run again reads the session's own spec, which an archive does not have of its own.
  FExplorerMenu.ItemLinks[1].Item.Enabled := LIsSession and (FSessionId = '');
  FExplorerMenu.ItemLinks[2].Item.Enabled := LIsSession;
  FExplorerMenu.ItemLinks[3].Item.Enabled := LIsSession;
  FExplorerMenu.ItemLinks[4].Item.Enabled := LIsResult;
end;

procedure TMainForm.ExplorerOpenClick(Sender: TObject);
var
  LRef: TExplorerRef;
begin
  if FocusedSession(LRef) and (LRef.Kind in [ekSession, ekArchive]) and TFile.Exists(LRef.DatabasePath) then
    LoadSession(LRef.DatabasePath);
end;

procedure TMainForm.ExplorerRenameClick(Sender: TObject);
var
  LRef: TExplorerRef;
  LDirectory, LName: string;
begin
  if not FocusedSession(LRef) or (LRef.Kind <> ekSession) then
    Exit;
  LDirectory := TPath.GetDirectoryName(LRef.DatabasePath);
  LName := '';
  if not dxInputQuery('Rename session', 'What is this session about?', LName) then
    Exit;
  if not EnsureService then
    Exit;
  try
    // What a name is - and that the id everything else refers to does not move - is the
    // engine's rule, so the GUI asks it rather than rewriting session.json behind its back.
    FClient.RenameSession(LDirectory, Trim(LName));
  except
    on E: Exception do
    begin
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
      Exit;
    end;
  end;
  ReloadExplorer;
end;

procedure TMainForm.ExplorerDeleteClick(Sender: TObject);
var
  LRef: TExplorerRef;
  LDirectory: string;
begin
  if not FocusedSession(LRef) or (LRef.Kind <> ekSession) then
    Exit;
  LDirectory := TPath.GetDirectoryName(LRef.DatabasePath);
  if dxMessageDlg('Delete this session and everything it recorded?' + sLineBreak + LDirectory
    + sLineBreak + sLineBreak + 'The results, the trace, the log and the archives kept during '
    + 'it go with it, and none of it can be brought back.',
    mtWarning, [mbYes, mbNo], 0) <> mrYes then
    Exit;
  if not EnsureService then
    Exit;
  // The session being deleted may be the one on screen: let go of the database first, or
  // Windows refuses to remove a file the GUI still holds open.
  if SameText(LRef.DatabasePath, FStore.Path) then
  begin
    FStore.Close;
    FEditorFile := '';
    ShowSourceOf(-1);
    UpdateInfo;
  end;
  try
    FClient.DeleteSession(LDirectory);
  except
    on E: Exception do
    begin
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
      Exit;
    end;
  end;
  ReloadExplorer;
end;

procedure TMainForm.ExplorerRevealClick(Sender: TObject);
var
  LRef: TExplorerRef;
begin
  if not FocusedSession(LRef) or (LRef.DatabasePath = '') then
    Exit;
  ShellExecute(0, 'open', 'explorer.exe', PChar('/select,"' + LRef.DatabasePath + '"'), nil, SW_SHOWNORMAL);
end;

procedure TMainForm.RefreshExplorerClick(Sender: TObject);
begin
  ReloadExplorer;
end;

{ Sessions kept somewhere else are still sessions: pointing the Explorer at their folder
  is how somebody reads a colleague's recording, or their own from a project folder. }
procedure TMainForm.AddSessionFolderClick(Sender: TObject);
var
  LFolder: string;
begin
  LFolder := '';
  if not SelectDirectory('Folder holding profiling sessions', '', LFolder) then
    Exit;
  RememberSessionFolder(LFolder);
  ReloadExplorer;
end;

procedure TMainForm.ExplorerDblClick(Sender: TObject);
var
  LIndex: Integer;
  LRef: TExplorerRef;
  LCategory: string;
  LColumn: TcxGridDBColumn;
begin
  if FExplorer.FocusedNode = nil then
    Exit;
  LIndex := Integer(NativeInt(FExplorer.FocusedNode.Data));
  if (LIndex < 1) or (LIndex > Length(FExplorerRefs)) then
    Exit;
  LRef := FExplorerRefs[LIndex - 1];
  // An archive is an ordinary result database: opening one is opening a session, which is
  // what lets it be read long after the session that produced it ended.
  if LRef.Kind in [ekSession, ekArchive] then
  begin
    if TFile.Exists(LRef.DatabasePath) then
      LoadSession(LRef.DatabasePath);
    Exit;
  end;
  // A category under the open session regroups the Report instead of loading anything.
  begin
    LCategory := LRef.Category;
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
  end;
end;

procedure TMainForm.BuildReportTab;
begin
  FDetailsPanel := TdxPanel.Create(Self);
  FDetailsPanel.Parent := FDetailsDock;
  FDetailsPanel.Align := alClient;

  // One above the other, not side by side: both tables hold method names, and a method
  // name is long. Side by side each got half the width and showed a truncated name twice;
  // stacked, both get the whole width and the reading goes down the page - callers above,
  // callees below, which is also the direction the call stack runs.
  FParentsGrid := BuildNeighbourGrid(FDetailsPanel, alTop, 'Parents', True, FParentsView);
  FParentsGrid.Parent.Height := 200;
  FDetailsSplitter := TcxSplitter.Create(Self);
  FDetailsSplitter.Parent := FDetailsPanel;
  FDetailsSplitter.Control := FParentsGrid.Parent;
  FDetailsSplitter.AlignSplitter := salTop;
  FChildrenGrid := BuildNeighbourGrid(FDetailsPanel, alClient, 'Children', False, FChildrenView);

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
  AIsParents: Boolean; out AView: TcxGridTableView): TcxGrid;
var
  LPanel: TdxPanel;
  LLabel: TcxLabel;
  LGrid: TcxGrid;
  LLevel: TcxGridLevel;
  LPie: TPaintBox;
begin
  LPanel := TdxPanel.Create(Self);
  LPanel.Parent := AParent;
  LPanel.Align := AAlign;

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
  if AIsParents then
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
  // The method name takes what is left: stacked tables have the full width to spend on it.
  AView.OptionsView.ColumnAutoWidth := True;
  AView.Columns[1].Width := 120;
  AView.Columns[1].Options.HorzSizing := False;
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
  // A cxScrollBox rather than the VCL one: its scrollbars are painted by the skin like
  // every other bar in the window, and its background is a colour we set rather than the
  // system's white - which on a dark theme was a white sheet flashing on every resize and
  // standing there in the open whenever the window grew past the drawing.
  FGraphScroll := TcxScrollBox.Create(Self);
  FGraphScroll.Parent := FGraphPanel;
  FGraphScroll.Align := alClient;
  FGraphScroll.BorderStyle := cxcbsNone;
  FGraphScroll.LookAndFeel.NativeStyle := False;
  FGraphScroll.DoubleBuffered := True;
  FGraphScroll.OnResize := GraphResized;

  FGraph := TPaintBox.Create(Self);
  FGraph.Parent := FGraphScroll;
  FGraph.SetBounds(0, 0, 1200, 700);
  FGraph.OnPaint := PaintGraph;
  FGraph.OnMouseDown := GraphMouseDown;
  FGraph.OnMouseMove := GraphMouseMove;
  FGraph.ShowHint := True;
  FGraphMethodId := -1;
  FGraphOpen := TStringList.Create;
end;

/// A line with a head on it: without the head the direction of a call is a guess.
procedure DrawGraphArrow(ACanvas: TCanvas; const AFrom, ATo: TPoint);
const
  Head = 7;
var
  LAngle: Double;
begin
  ACanvas.Pen.Color := ThemeColors.Line;
  ACanvas.Brush.Color := ThemeColors.Line;
  ACanvas.MoveTo(AFrom.X, AFrom.Y);
  ACanvas.LineTo(ATo.X, ATo.Y);
  LAngle := ArcTan2(ATo.Y - AFrom.Y, ATo.X - AFrom.X);
  ACanvas.Polygon([ATo,
    Point(ATo.X - Round(Head * Cos(LAngle - Pi / 7)), ATo.Y - Round(Head * Sin(LAngle - Pi / 7))),
    Point(ATo.X - Round(Head * Cos(LAngle + Pi / 7)), ATo.Y - Round(Head * Sin(LAngle + Pi / 7)))]);
end;

{ ------------------------------------------------------------------ the call graph

  Read from the method in focus outwards, the way AQTime draws it: the callers in the
  column to its left, the callees to its right, one column per level. Every box that has
  callees carries a [+]; opening it brings its callees into the next column, closing it
  takes that whole branch away. Nothing past the first level opens by itself - a real
  application's call graph is unreadable when it is complete, and useful when somebody
  follows one branch of it - and what is open is remembered per box, so the same method
  reached two ways is two boxes that open independently.
}

const
  CGraphBoxWidth = 258;
  CGraphBoxHeight = 76;
  CGraphColumnGap = 78;
  CGraphRowGap = 12;
  CGraphMargin = 24;
  CGraphMaxCallers = 8;
  CGraphMaxCallees = 14;
  CGraphToggle = 13;

{ What to write on a box: the type and the method, which is what tells one apart from
  another. The namespace is the same for most of the graph and eats the width; the whole
  name is a hover away. }
function GraphTitle(const AFullName: string): string;
var
  LSuffix, LName: string;
  LParts, LKept: TArray<string>;
  LBracket, I: Integer;
begin
  LName := AFullName;
  LSuffix := '';
  LBracket := Pos('(', LName);
  if LBracket > 0 then
  begin
    // "(async body)" and "(iterator body)" say what the row is; shortened, they still do,
    // and they leave room for the name.
    LSuffix := ' ' + Copy(LName, LBracket, Length(LName));
    LSuffix := StringReplace(LSuffix, ' body)', ')', [rfReplaceAll]);
    LName := Trim(Copy(LName, 1, LBracket - 1));
  end;
  // The empty parts are what a constructor's "Type..ctor" leaves behind: dropping them
  // keeps the two that matter, the type and the method.
  LParts := LName.Split(['.']);
  LKept := nil;
  for I := 0 to High(LParts) do
    if LParts[I] <> '' then
      LKept := LKept + [LParts[I]];
  if Length(LKept) > 2 then
    LKept := Copy(LKept, Length(LKept) - 2, 2);
  Result := string.Join('.', LKept) + LSuffix;
end;

/// A figure in the words of the session's mode: nanoseconds for instrumenting, samples
/// for sampling, where "time" would be a lie.
function TMainForm.GraphValueLine(const ACaption: string; AValue: Int64): string;
begin
  if FStore.Mode = smInstrumenting then
    Result := ACaption + ': ' + FormatNs(AValue)
  else
    Result := ACaption + ': ' + IntToStr(AValue);
end;

function TMainForm.AddGraphNode(AMethodId: Integer; const AName: string; ADepth: Integer): Integer;
begin
  SetLength(FGraphNodes, Length(FGraphNodes) + 1);
  Result := High(FGraphNodes);
  FGraphNodes[Result].MethodId := AMethodId;
  FGraphNodes[Result].Name := AName;
  FGraphNodes[Result].Depth := ADepth;
  FGraphNodes[Result].Stats := FStore.MethodStats(AMethodId);
  FGraphNodes[Result].HasCallees := FStore.HasCallees(AMethodId);
  FGraphNodes[Result].Expanded := FGraphOpen.IndexOf(IntToStr(AMethodId)) >= 0;
end;

/// The box a method already has in this graph, or -1. Callers are not counted: a method
/// that both calls the focus and is called by it is two different statements.
function TMainForm.GraphNodeOf(AMethodId: Integer): Integer;
var
  I: Integer;
begin
  for I := 0 to High(FGraphNodes) do
    if (FGraphNodes[I].Depth >= 0) and (FGraphNodes[I].MethodId = AMethodId) then
      Exit(I);
  Result := -1;
end;

{ The graph is grown breadth first from the method in focus. A callee that already has a
  box gets another arrow into that box rather than a box of its own - the graph is a graph,
  not a tree - which also ends recursion: a method is visited once. }
procedure TMainForm.BuildGraphNodes;

  procedure AddEdge(AFrom, ATo: Integer; ACalls, AValue: Int64);
  begin
    SetLength(FGraphEdges, Length(FGraphEdges) + 1);
    FGraphEdges[High(FGraphEdges)].FromNode := AFrom;
    FGraphEdges[High(FGraphEdges)].ToNode := ATo;
    FGraphEdges[High(FGraphEdges)].Calls := ACalls;
    FGraphEdges[High(FGraphEdges)].Value := AValue;
  end;

  procedure AddCallees(ANodeIndex: Integer);
  var
    LCallees: TNeighbours;
    LDepth, LCount, LChild, I: Integer;
  begin
    LCallees := FStore.Children(FGraphNodes[ANodeIndex].MethodId);
    LDepth := FGraphNodes[ANodeIndex].Depth + 1;
    LCount := Min(Length(LCallees), CGraphMaxCallees);
    FGraphNodes[ANodeIndex].HiddenChildren := Length(LCallees) - LCount;
    // The store answers heaviest first, and the order they are added in is the order they
    // are stacked in: the call that costs most is the one at the top of the column.
    for I := 0 to LCount - 1 do
    begin
      LChild := GraphNodeOf(LCallees[I].MethodId);
      if LChild < 0 then
      begin
        LChild := AddGraphNode(LCallees[I].MethodId, LCallees[I].FullName, LDepth);
        if FGraphNodes[LChild].Expanded then
          AddCallees(LChild);
      end;
      AddEdge(ANodeIndex, LChild, LCallees[I].Calls, LCallees[I].Value);
    end;
  end;

var
  LCallers: TNeighbours;
  LFocus, LCaller, LCount, I: Integer;
begin
  FGraphNodes := nil;
  FGraphEdges := nil;
  FGraphParentsHidden := 0;
  FGraphChildrenHidden := 0;
  if (FGraphMethodId < 0) or not FStore.IsOpen then
    Exit;
  LFocus := AddGraphNode(FGraphMethodId, FStore.MethodName(FGraphMethodId), 0);
  // The callers are one level and no further: a caller's own callers are that method's
  // graph, one click away. The callees of the method in focus are always shown.
  LCallers := FStore.Parents(FGraphMethodId);
  LCount := Min(Length(LCallers), CGraphMaxCallers);
  FGraphParentsHidden := Length(LCallers) - LCount;
  for I := 0 to LCount - 1 do
  begin
    LCaller := AddGraphNode(LCallers[I].MethodId, LCallers[I].FullName, -1);
    AddEdge(LCaller, LFocus, LCallers[I].Calls, LCallers[I].Value);
  end;
  AddCallees(LFocus);
  FGraphChildrenHidden := FGraphNodes[LFocus].HiddenChildren;
end;

{ Where every box goes. Each column is packed from the top in the order the boxes were
  discovered, which is heaviest call first; the method in focus sits against the middle of
  the column it calls, and its callers against the middle of it. }
procedure TMainForm.LayoutGraphNodes;

  function ColumnLeft(ADepth: Integer): Integer;
  begin
    Result := CGraphMargin + (ADepth + 1) * (CGraphBoxWidth + CGraphColumnGap);
  end;

  { Every box that calls something gets a vertical line of its own in the gap it calls
    across, spaced out from its neighbours in the same column. Sharing one line - which is
    what routing through the middle of the gap does - draws two boxes as if each called
    every callee of the other. }
  procedure AssignTrunks;
  var
    LSources: TArray<Integer>;
    LDeepest, LDepth, LStep, I, J: Integer;
  begin
    LDeepest := 0;
    for I := 0 to High(FGraphNodes) do
    begin
      FGraphNodes[I].Trunk := FGraphNodes[I].Box.Right + CGraphColumnGap div 2;
      LDeepest := Max(LDeepest, FGraphNodes[I].Depth);
    end;
    for LDepth := -1 to LDeepest do
    begin
      LSources := nil;
      for I := 0 to High(FGraphNodes) do
        if FGraphNodes[I].Depth = LDepth then
          for J := 0 to High(FGraphEdges) do
            if (FGraphEdges[J].FromNode = I)
              and (FGraphNodes[FGraphEdges[J].ToNode].Box.Left > FGraphNodes[I].Box.Left) then
            begin
              LSources := LSources + [I];
              Break;
            end;
      if Length(LSources) = 0 then
        Continue;
      LStep := (CGraphColumnGap - 10) div (Length(LSources) + 1);
      for I := 0 to High(LSources) do
        FGraphNodes[LSources[I]].Trunk := FGraphNodes[LSources[I]].Box.Right + 8 + (I + 1) * LStep;
    end;
  end;

  procedure PlaceBox(AIndex, ALeft, ATop: Integer);
  begin
    FGraphNodes[AIndex].Box := Rect(ALeft, ATop, ALeft + CGraphBoxWidth, ATop + CGraphBoxHeight);
    FGraphNodes[AIndex].Toggle := TRect.Empty;
    if not FGraphNodes[AIndex].HasCallees then
      Exit;
    FGraphNodes[AIndex].Toggle := Rect(ALeft + CGraphBoxWidth - CGraphToggle div 2,
      ATop + CGraphBoxHeight div 2 - CGraphToggle div 2,
      ALeft + CGraphBoxWidth + CGraphToggle div 2 + 1,
      ATop + CGraphBoxHeight div 2 + CGraphToggle div 2 + 1);
  end;

  function SpanCentre(ADepth: Integer): Integer;
  var
    LTop, LBottom, I: Integer;
  begin
    LTop := MaxInt;
    LBottom := 0;
    for I := 0 to High(FGraphNodes) do
      if FGraphNodes[I].Depth = ADepth then
      begin
        LTop := Min(LTop, FGraphNodes[I].Box.Top);
        LBottom := Max(LBottom, FGraphNodes[I].Box.Bottom);
      end;
    if LTop = MaxInt then
      Exit(CGraphMargin + CGraphBoxHeight div 2);
    Result := (LTop + LBottom) div 2;
  end;

  procedure StackColumn(ADepth, ACentre: Integer);
  var
    LTop, I, LCount: Integer;
  begin
    LCount := 0;
    for I := 0 to High(FGraphNodes) do
      if FGraphNodes[I].Depth = ADepth then
        Inc(LCount);
    if LCount = 0 then
      Exit;
    LTop := ACentre - (LCount * (CGraphBoxHeight + CGraphRowGap) - CGraphRowGap) div 2;
    for I := 0 to High(FGraphNodes) do
      if FGraphNodes[I].Depth = ADepth then
      begin
        PlaceBox(I, ColumnLeft(ADepth), LTop);
        Inc(LTop, CGraphBoxHeight + CGraphRowGap);
      end;
  end;

var
  LMaxDepth, LDepth, LTop, LShift, LBottom, LRight, I: Integer;
begin
  if Length(FGraphNodes) = 0 then
    Exit;
  LMaxDepth := 0;
  for I := 0 to High(FGraphNodes) do
    LMaxDepth := Max(LMaxDepth, FGraphNodes[I].Depth);

  // The callee columns first, each packed from the top of the drawing.
  for LDepth := 1 to LMaxDepth do
  begin
    LTop := CGraphMargin;
    for I := 0 to High(FGraphNodes) do
      if FGraphNodes[I].Depth = LDepth then
      begin
        PlaceBox(I, ColumnLeft(LDepth), LTop);
        Inc(LTop, CGraphBoxHeight + CGraphRowGap);
      end;
  end;
  // Then the focus against the middle of what it calls, and the callers against it.
  StackColumn(0, IfThen(LMaxDepth >= 1, SpanCentre(1), CGraphMargin + CGraphBoxHeight div 2));
  StackColumn(-1, SpanCentre(0));

  // Nothing may sit above the top edge: what is drawn there cannot be scrolled to.
  LShift := MaxInt;
  for I := 0 to High(FGraphNodes) do
    LShift := Min(LShift, FGraphNodes[I].Box.Top);
  LShift := CGraphMargin - LShift;
  if LShift > 0 then
    for I := 0 to High(FGraphNodes) do
    begin
      FGraphNodes[I].Box.Offset(0, LShift);
      if not FGraphNodes[I].Toggle.IsEmpty then
        FGraphNodes[I].Toggle.Offset(0, LShift);
    end;

  AssignTrunks;

  LBottom := 0;
  LRight := 0;
  for I := 0 to High(FGraphNodes) do
  begin
    LBottom := Max(LBottom, FGraphNodes[I].Box.Bottom);
    LRight := Max(LRight, FGraphNodes[I].Box.Right);
  end;
  // At least the whole viewport: a canvas smaller than the window leaves the scroll box's
  // own background showing, and that background is not the one the theme paints.
  FGraph.SetBounds(0, 0, Max(FGraphScroll.ClientWidth, LRight + CGraphMargin),
    Max(FGraphScroll.ClientHeight, LBottom + CGraphMargin + 24));
end;

/// The window grew or shrank: the canvas has to cover it, or the space that is not canvas
/// shows through in a colour nobody chose.
procedure TMainForm.GraphResized(Sender: TObject);
begin
  if (FGraph = nil) or (FGraphScroll = nil) then
    Exit;
  if Length(FGraphNodes) = 0 then
    FGraph.SetBounds(0, 0, FGraphScroll.ClientWidth, FGraphScroll.ClientHeight)
  else
    LayoutGraphNodes;
  FGraph.Invalidate;
end;

/// One box: the method on a title bar, its figures underneath, and the [+] that opens
/// what it calls. The method in focus is the one painted in the accent colour.
procedure TMainForm.DrawGraphNode(ACanvas: TCanvas; const ANode: TGraphNode);
var
  LIsFocus: Boolean;
  LRect: TRect;
  LText: string;
  LLine: Integer;

  procedure Line(const AText: string);
  var
    LLineRect: TRect;
    LLineText: string;
  begin
    LLineRect := Rect(ANode.Box.Left + 8, ANode.Box.Top + 26 + LLine * 16,
      ANode.Box.Right - 8, ANode.Box.Top + 42 + LLine * 16);
    // TextRect clips through a var string, so it needs one of its own.
    LLineText := AText;
    ACanvas.TextRect(LLineRect, LLineText, [tfEndEllipsis]);
    Inc(LLine);
  end;

begin
  LIsFocus := ANode.Depth = 0;
  ACanvas.Pen.Color := ThemeColors.Line;
  ACanvas.Pen.Width := IfThen(LIsFocus, 2, 1);
  ACanvas.Brush.Color := IfThen(LIsFocus, ThemeColors.Accent, ThemeColors.BoxFill);
  ACanvas.Rectangle(ANode.Box);
  ACanvas.Pen.Width := 1;

  // The title bar, and under it the line AQTime draws between name and figures.
  ACanvas.Brush.Style := bsClear;
  ACanvas.Font.Color := IfThen(LIsFocus, clWhite, ThemeColors.Text);
  ACanvas.Font.Style := [fsBold];
  LText := GraphTitle(ANode.Name);
  LRect := Rect(ANode.Box.Left + 8, ANode.Box.Top + 5, ANode.Box.Right - 8, ANode.Box.Top + 23);
  ACanvas.TextRect(LRect, LText, [tfEndEllipsis]);
  ACanvas.Pen.Color := IfThen(LIsFocus, clWhite, ThemeColors.Line);
  ACanvas.MoveTo(ANode.Box.Left + 1, ANode.Box.Top + 24);
  ACanvas.LineTo(ANode.Box.Right - 1, ANode.Box.Top + 24);

  ACanvas.Font.Style := [];
  LLine := 0;
  if ANode.Stats.Calls > 0 then
    Line(Format('Calls: %d', [ANode.Stats.Calls]));
  Line(GraphValueLine(IfThen(FStore.Mode = smInstrumenting, 'Time', 'Samples'), ANode.Stats.SelfValue));
  Line(GraphValueLine(IfThen(FStore.Mode = smInstrumenting, 'With children', 'With callees'),
    ANode.Stats.Total));
  ACanvas.Brush.Style := bsSolid;

  if ANode.Toggle.IsEmpty then
    Exit;
  ACanvas.Brush.Color := ThemeColors.Window;
  ACanvas.Pen.Color := ThemeColors.Line;
  ACanvas.Rectangle(ANode.Toggle);
  ACanvas.Pen.Color := ThemeColors.Text;
  ACanvas.MoveTo(ANode.Toggle.Left + 3, ANode.Toggle.CenterPoint.Y);
  ACanvas.LineTo(ANode.Toggle.Right - 3, ANode.Toggle.CenterPoint.Y);
  if not ANode.Expanded then
  begin
    ACanvas.MoveTo(ANode.Toggle.CenterPoint.X, ANode.Toggle.Top + 3);
    ACanvas.LineTo(ANode.Toggle.CenterPoint.X, ANode.Toggle.Bottom - 3);
  end;
end;

{ An elbow from one box to the next, the way a call is drawn on paper: out of the right
  edge, across the gap, up or down to the callee, and into its left edge. What the drawing
  says about a call: the number on it is how many times it happened, and the heavier the
  call - the share of the graph's biggest - the thicker and the more accented the line. A
  call that goes back to a box in the same column or to the left of it is drawn faintly:
  it is a return into the graph, not another step outwards.
}
procedure TMainForm.DrawGraphEdge(ACanvas: TCanvas; const AEdge: TGraphEdge; AMax: Int64);
const
  Head = 6;
var
  LFrom, LTo: TRect;
  LStart, LEnd: TPoint;
  LMidX, LWidth: Integer;
  LBackwards: Boolean;
begin
  LFrom := FGraphNodes[AEdge.FromNode].Box;
  LTo := FGraphNodes[AEdge.ToNode].Box;
  LBackwards := LTo.Left <= LFrom.Left;
  LStart := Point(LFrom.Right, LFrom.CenterPoint.Y);
  LEnd := Point(LTo.Left, LTo.CenterPoint.Y);
  LWidth := 1;
  if (AMax > 0) and (AEdge.Value > 0) then
    LWidth := 1 + Trunc(2.5 * AEdge.Value / AMax);
  ACanvas.Pen.Width := IfThen(LBackwards, 1, LWidth);
  if LBackwards then
    ACanvas.Pen.Color := ThemeColors.Subtle
  else
    ACanvas.Pen.Color := IfThen(LWidth > 2, ThemeColors.Accent, ThemeColors.Line);

  if LBackwards then
  begin
    // Round the outside rather than cut through the boxes in between.
    LMidX := Max(LFrom.Right, LTo.Right) + CGraphColumnGap div 2;
    ACanvas.MoveTo(LStart.X, LStart.Y);
    ACanvas.LineTo(LMidX, LStart.Y);
    ACanvas.LineTo(LMidX, LTo.Bottom + 6);
    ACanvas.LineTo(LTo.CenterPoint.X, LTo.Bottom + 6);
    ACanvas.LineTo(LTo.CenterPoint.X, LTo.Bottom);
    LEnd := Point(LTo.CenterPoint.X, LTo.Bottom);
    ACanvas.Brush.Color := ACanvas.Pen.Color;
    ACanvas.Polygon([LEnd, Point(LEnd.X - 4, LEnd.Y + Head), Point(LEnd.X + 4, LEnd.Y + Head)]);
  end
  else
  begin
    // The turn happens on the source's own vertical line, not in the middle of the gap.
    LMidX := Min(FGraphNodes[AEdge.FromNode].Trunk, LEnd.X - 12);
    ACanvas.MoveTo(LStart.X + CGraphToggle div 2, LStart.Y);
    ACanvas.LineTo(LMidX, LStart.Y);
    ACanvas.LineTo(LMidX, LEnd.Y);
    ACanvas.LineTo(LEnd.X - Head, LEnd.Y);
    ACanvas.Brush.Color := ACanvas.Pen.Color;
    ACanvas.Polygon([LEnd, Point(LEnd.X - Head, LEnd.Y - 4), Point(LEnd.X - Head, LEnd.Y + 4)]);
  end;
  ACanvas.Pen.Width := 1;

  if AEdge.Calls <= 0 then
    Exit;
  ACanvas.Brush.Style := bsClear;
  ACanvas.Font.Color := ThemeColors.Subtle;
  ACanvas.TextOut(LMidX + 4, LEnd.Y - 16, IntToStr(AEdge.Calls));
  ACanvas.Font.Color := ThemeColors.Text;
  ACanvas.Brush.Style := bsSolid;
end;

procedure TMainForm.PaintGraph(Sender: TObject);
var
  LCanvas: TCanvas;
  LMax: Int64;
  I: Integer;
begin
  LCanvas := FGraph.Canvas;
  LCanvas.Brush.Color := ThemeColors.Window;
  LCanvas.Font.Color := ThemeColors.Text;
  LCanvas.FillRect(FGraph.ClientRect);
  if Length(FGraphNodes) = 0 then
  begin
    LCanvas.Brush.Style := bsClear;
    LCanvas.TextOut(16, 16, 'Pick a method in the Report to see who calls it and what it calls.');
    LCanvas.Brush.Style := bsSolid;
    Exit;
  end;

  LMax := 0;
  for I := 0 to High(FGraphEdges) do
    LMax := Max(LMax, FGraphEdges[I].Value);

  // The edges first: a box drawn over its own arrow is what makes a graph look drawn by
  // hand rather than read.
  for I := 0 to High(FGraphEdges) do
    DrawGraphEdge(LCanvas, FGraphEdges[I], LMax);
  for I := 0 to High(FGraphNodes) do
    DrawGraphNode(LCanvas, FGraphNodes[I]);

  LCanvas.Brush.Style := bsClear;
  LCanvas.Font.Color := ThemeColors.Subtle;
  if FGraphParentsHidden > 0 then
    LCanvas.TextOut(CGraphMargin, 6, Format('+%d more callers - the Details panel has them all',
      [FGraphParentsHidden]));
  if Length(FGraphHistory) > 0 then
    LCanvas.TextOut(FGraphNodes[0].Box.Left, FGraph.Height - 20,
      'click a box to walk there, right-click to go back, double-click for the source');
  LCanvas.Font.Color := ThemeColors.Text;
  LCanvas.Brush.Style := bsSolid;
end;

/// Clicking a box walks the graph, which is the whole point of having one; clicking the
/// [+] opens or closes what that box calls, without moving anywhere. The right button
/// walks back, and a double click leaves the graph for the code.
procedure TMainForm.GraphMouseDown(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
var
  LPoint: TPoint;
  LIndex, I: Integer;
begin
  if Button = mbRight then
  begin
    GraphBack;
    Exit;
  end;
  LPoint := Point(X, Y);
  for I := 0 to High(FGraphNodes) do
    if not FGraphNodes[I].Toggle.IsEmpty and FGraphNodes[I].Toggle.Contains(LPoint) then
    begin
      LIndex := FGraphOpen.IndexOf(IntToStr(FGraphNodes[I].MethodId));
      if LIndex >= 0 then
        FGraphOpen.Delete(LIndex)
      else
        FGraphOpen.Add(IntToStr(FGraphNodes[I].MethodId));
      BuildGraphNodes;
      LayoutGraphNodes;
      FGraph.Invalidate;
      Exit;
    end;
  for I := 0 to High(FGraphNodes) do
    if FGraphNodes[I].Box.Contains(LPoint) then
    begin
      if ssDouble in Shift then
      begin
        ShowSourceOf(FGraphNodes[I].MethodId);
        FSourcePanel.Activate;
      end
      else
        ShowGraphOf(FGraphNodes[I].MethodId);
      Exit;
    end;
end;

procedure TMainForm.ExpandGraph(ALevels: Integer);
var
  LOpened: Boolean;
  LLevel, I: Integer;
begin
  if FGraphMethodId < 0 then
    Exit;
  for LLevel := 1 to Max(0, ALevels) do
  begin
    LOpened := False;
    for I := 0 to High(FGraphNodes) do
      if FGraphNodes[I].HasCallees and not FGraphNodes[I].Expanded and (FGraphNodes[I].Depth >= 0)
        and (FGraphOpen.IndexOf(IntToStr(FGraphNodes[I].MethodId)) < 0) then
      begin
        FGraphOpen.Add(IntToStr(FGraphNodes[I].MethodId));
        LOpened := True;
      end;
    if not LOpened then
      Break;
    BuildGraphNodes;
  end;
  LayoutGraphNodes;
  FGraph.Invalidate;
end;

/// The box under the pointer says its whole name, which the box itself has no room for.
procedure TMainForm.GraphMouseMove(Sender: TObject; Shift: TShiftState; X, Y: Integer);
var
  LHint: string;
  I: Integer;
begin
  LHint := '';
  for I := 0 to High(FGraphNodes) do
    if FGraphNodes[I].Box.Contains(Point(X, Y)) then
    begin
      LHint := FGraphNodes[I].Name;
      Break;
    end;
  if LHint = FGraph.Hint then
    Exit;
  Application.CancelHint;
  FGraph.Hint := LHint;
end;

/// Walking a graph without a way back means starting from the Report every time.
procedure TMainForm.GraphBack;
var
  LPrevious: Integer;
begin
  if Length(FGraphHistory) = 0 then
    Exit;
  LPrevious := FGraphHistory[High(FGraphHistory)];
  SetLength(FGraphHistory, Length(FGraphHistory) - 1);
  FGraphMethodId := -1;   // stop ShowGraphOf recording where we came from
  ShowGraphOf(LPrevious);
end;

procedure TMainForm.ShowGraphOf(AMethodId: Integer);
begin
  if (FGraphMethodId >= 0) and (AMethodId <> FGraphMethodId) then
    FGraphHistory := FGraphHistory + [FGraphMethodId];
  FGraphMethodId := AMethodId;
  // Walking to another method starts its graph closed: what was open belonged to the
  // branch somebody was following, and carrying it over reopens things at random.
  FGraphOpen.Clear;
  BuildGraphNodes;
  if FGraph <> nil then
  begin
    LayoutGraphNodes;
    FGraph.Invalidate;
  end;
end;

procedure TMainForm.BuildEditorTab;
begin
  FEditorHeader := TcxLabel.Create(Self);
  FEditorHeader.Transparent := True;
  FEditorHeader.Parent := FSourcePanel;
  FEditorHeader.Align := alTop;
  FEditorHeader.Caption := ' Pick a method in the Report to see its source.';

  // SynEdit scrolls with the window's own non-client bars, which no skin touches: on the
  // dark theme they stay bright grey beside a dark editor. They are turned off and driven
  // from DevExpress ones, which the skin paints like every other bar here. The arrangement
  // - a host at the bottom holding the horizontal bar and a square corner - is the one that
  // works in CVSTreeGraph, where this was solved first.
  FEditorScrollHost := TdxPanel.Create(Self);
  FEditorScrollHost.Parent := FSourcePanel;
  FEditorScrollHost.Align := alBottom;
  FEditorScrollHost.Visible := False;

  FEditorScrollCorner := TdxPanel.Create(Self);
  FEditorScrollCorner.Parent := FEditorScrollHost;
  FEditorScrollCorner.Align := alRight;
  FEditorScrollCorner.Visible := False;

  FEditorScrollH := TcxScrollBar.Create(Self);
  FEditorScrollH.Parent := FEditorScrollHost;
  FEditorScrollH.Align := alClient;
  FEditorScrollH.Kind := sbHorizontal;
  FEditorScrollH.UnlimitedTracking := True;
  FEditorScrollH.OnScroll := EditorScrollBarScrolled;

  FEditorScrollV := TcxScrollBar.Create(Self);
  FEditorScrollV.Parent := FSourcePanel;
  FEditorScrollV.Align := alRight;
  FEditorScrollV.Kind := sbVertical;
  FEditorScrollV.UnlimitedTracking := True;
  FEditorScrollV.Visible := False;
  FEditorScrollV.OnScroll := EditorScrollBarScrolled;

  FEditor := TSynEdit.Create(Self);
  FEditor.Parent := FSourcePanel;
  FEditor.Align := alClient;
  FEditor.ScrollBars := ssNone;
  FEditor.ReadOnly := True;
  FEditor.Gutter.ShowLineNumbers := True;
  FEditor.Font.Name := 'Consolas';
  FEditor.Font.Size := 10;
  FEditor.OnSpecialLineColors := EditorSpecialLineColors;
  FEditor.OnStatusChange := EditorStatusChanged;

  FEditorHighlighter := TSynCSSyn.Create(Self);
  FEditor.Highlighter := FEditorHighlighter;
end;

/// A resize changes how much is on screen without moving the caret, so the editor raises no
/// status change of its own and the bars would keep the size the panel had before. What
/// changes that room is the window and the docking layout, so both say so here. SynEdit does
/// not publish OnResize, or this would hang off the editor itself.
procedure TMainForm.EditorResized(Sender: TObject);
begin
  UpdateEditorScrollBars;
end;

procedure TMainForm.DockLayoutChanged(Sender: TdxCustomDockControl);
begin
  UpdateEditorScrollBars;
  // Panels moved after the arrangement was thrown away: this one is wanted again.
  FForgetLayout := False;
end;

/// The editor moved, or was given another file: the bars follow it. Everything that scrolls
/// - the wheel, the caret, a new source, a resize - passes through here.
procedure TMainForm.EditorStatusChanged(Sender: TObject; Changes: TSynStatusChanges);
begin
  UpdateEditorScrollBars;
end;

/// A bar moved: the editor follows it, and the bar is told where it ended up.
procedure TMainForm.EditorScrollBarScrolled(Sender: TObject; AScrollCode: TScrollCode;
  var AScrollPos: Integer);
begin
  if FUpdatingEditorScrollBars then
    Exit;
  if Sender = FEditorScrollV then
    FEditor.TopLine := AScrollPos
  else if Sender = FEditorScrollH then
    FEditor.LeftChar := AScrollPos;
  UpdateEditorScrollBars;
  if Sender is TcxScrollBar then
    AScrollPos := TcxScrollBar(Sender).Position;
end;

/// How wide the widest line is: what there is to scroll sideways. A source file is short
/// enough for this to cost nothing.
function TMainForm.EditorLongestLine: Integer;
var
  LLine: string;
begin
  Result := 1;
  if FEditor = nil then
    Exit;
  for LLine in FEditor.Lines do
    if Length(LLine) > Result then
      Result := Length(LLine);
end;

/// What the bars say about the editor. The values are 1-based, as the editor's own TopLine
/// and LeftChar are, and the maximum is never smaller than a page: a scroll bar refuses a
/// page as large as its range, and the four values are only valid together.
procedure TMainForm.UpdateEditorScrollBars;
var
  LBarSize, LBaseHeight, LWidth, LHeight: Integer;
  LNeedH, LNeedV, LWasH, LWasV: Boolean;
  LLines, LColumns, LVisibleLines, LVisibleColumns, LVertMax, LHorzMax: Integer;
begin
  // While the constructor is still running the panels have no parent window yet, and
  // sizing them there is what "PanelSource has no parent window" means. The same holds
  // later, for the moment the window is first shown: the dock panel is creating its own
  // window just then, and showing or hiding a bar inside it re-enters that.
  if not FBuilt then
    Exit;
  // A window that is not shown has panels without handles and that is fine - it is how the
  // driven window renders. What must be left alone is the moment of showing, when the panel
  // is creating its window and has none yet.
  if (FSourcePanel = nil) or (Visible and not FSourcePanel.HandleAllocated) then
    Exit;
  if FUpdatingEditorScrollBars or (FEditor = nil) or (FEditorScrollV = nil) then
    Exit;
  FUpdatingEditorScrollBars := True;
  try
    LBarSize := GetSystemMetrics(SM_CXVSCROLL);
    if LBarSize <= 0 then
      LBarSize := 17;
    LBaseHeight := FSourcePanel.ClientHeight;
    if FEditorHeader <> nil then
      Dec(LBaseHeight, FEditorHeader.Height);

    // Whether a bar is needed depends on the room the other one leaves, so the two answers
    // are settled together rather than one after the other.
    LNeedH := False;
    LNeedV := False;
    repeat
      LWasH := LNeedH;
      LWasV := LNeedV;
      LWidth := FSourcePanel.ClientWidth;
      if LNeedV then
        Dec(LWidth, LBarSize);
      LHeight := LBaseHeight;
      if LNeedH then
        Dec(LHeight, LBarSize);
      LVisibleColumns := Max(1, (Max(1, LWidth) - FEditor.Gutter.RealGutterWidth) div Max(1, FEditor.CharWidth));
      LVisibleLines := Max(1, Max(1, LHeight) div Max(1, FEditor.LineHeight));
      LLines := Max(1, FEditor.Lines.Count);
      LColumns := EditorLongestLine;
      LNeedH := LColumns > LVisibleColumns;
      LNeedV := LLines > LVisibleLines;
    until (LNeedH = LWasH) and (LNeedV = LWasV);

    FEditorScrollHost.Height := LBarSize;
    FEditorScrollV.Width := LBarSize;
    FEditorScrollCorner.Width := LBarSize;
    FEditorScrollHost.Visible := LNeedH;
    FEditorScrollV.Visible := LNeedV;
    FEditorScrollCorner.Visible := LNeedH and LNeedV;

    LVisibleLines := Max(1, FEditor.LinesInWindow);
    LVisibleColumns := Max(1, (FEditor.ClientWidth - FEditor.Gutter.RealGutterWidth) div Max(1, FEditor.CharWidth));
    LLines := Max(1, FEditor.Lines.Count);
    LColumns := EditorLongestLine;
    LVertMax := Max(LLines, LVisibleLines + 1);
    LHorzMax := Max(LColumns, LVisibleColumns + 1);

    if not LNeedV then
      FEditor.TopLine := 1
    else if FEditor.TopLine > Max(1, LVertMax - LVisibleLines + 1) then
      FEditor.TopLine := Max(1, LVertMax - LVisibleLines + 1);
    if not LNeedH then
      FEditor.LeftChar := 1
    else if FEditor.LeftChar > Max(1, LHorzMax - LVisibleColumns + 1) then
      FEditor.LeftChar := Max(1, LHorzMax - LVisibleColumns + 1);

    FEditorScrollV.SetScrollParams(1, LVertMax, FEditor.TopLine, LVisibleLines, True);
    FEditorScrollH.SetScrollParams(1, LHorzMax, FEditor.LeftChar, LVisibleColumns, True);
    FEditorScrollV.LargeChange := LVisibleLines;
    FEditorScrollH.LargeChange := LVisibleColumns;
  finally
    FUpdatingEditorScrollBars := False;
  end;
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
    // Two different situations, and telling them apart is the whole point: a session
    // recorded without symbols can never show source, while a running one simply has not
    // resolved this method yet - the next refresh does it.
    if FStore.SymbolsDir = '' then
      FEditorHeader.Caption := Format(' %s: this session was recorded without a symbols directory, so it carries no source locations.',
        [FStore.MethodName(AMethodId)])
    // Only a session this window is running can still be refreshed. A stored one whose
    // state says Collecting is not alive - it is a recording that stopped there - and
    // telling somebody to press Get Results on it is sending them nowhere.
    else if (FSessionId <> '') and SameText(FStore.Path, FLiveDatabasePath) then
      FEditorHeader.Caption := Format(' %s: no source location yet - press Get Results to produce them from what is collected so far.',
        [FStore.MethodName(AMethodId)])
    else if not FStore.HasAnySourceLocations then
      FEditorHeader.Caption := Format(' %s: these results carry no source locations at all, although the session had symbols (%s). '
        + 'Record it again - Run again... - and they will be there.',
        [FStore.MethodName(AMethodId), FStore.SymbolsDir])
    else
      FEditorHeader.Caption := Format(' %s: no source location. Its assembly''s pdb in %s does not carry one (compiler-generated methods often do not).',
        [FStore.MethodName(AMethodId), FStore.SymbolsDir]);
    Exit;
  end;
  if not FileExists(LSource.FileName) then
  begin
    FEditor.Lines.Clear;
    FEditorHeader.Caption := Format(' %s is at %s:%d, but that file is not on this machine%s.',
      [FStore.MethodName(AMethodId), LSource.FileName, LSource.StartLine,
       IfThen(FStore.Solution = '', '', ' (the session came from ' + FStore.Solution + ')')]);
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
  UpdateEditorScrollBars;
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
  // Ctrl+F over a 200k-row report is not a luxury: the find panel filters as you type
  // and highlights what matched, and it costs no space until it is asked for.
  AView.FindPanel.DisplayMode := fpdmManual;
  AView.FindPanel.InfoText := 'Type to find a method, a type or a module';
  ASource := TDataSource.Create(Self);
  AView.DataController.DataSource := ASource;
  Result := LGrid;
end;

/// Memory, in the three questions the engine can answer: what was allocated, who
/// allocated it, and what is still alive (with the growth between two snapshots).
procedure TMainForm.BuildMemoryTab;
var
  LByType, LBySite, LHeap: TcxTabSheet;
  LHeapTop: TdxPanel;
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

  LHeapTop := TdxPanel.Create(Self);
  LHeapTop.Parent := LHeap;
  LHeapTop.Align := alTop;
  LHeapTop.Height := 36;

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
  FSummaryPanel.OnActivate := SummaryPanelActivated;
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
      SetSummary(LLines);
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
    SetSummary(LLines);
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

/// The window is up: from here on the user can see that something is happening.
procedure TMainForm.ShowPendingDialog(Sender: TObject);
var
  LWhich: string;
begin
  LWhich := FPendingDialog;
  FPendingDialog := '';
  // --dialog=crash raises on purpose: it is how the crash report itself is tested, and
  // the only way to know the call stack still resolves after a change to the build.
  if SameText(LWhich, 'crash') then
    raise EProgrammerNotFound.Create('deliberate crash: --dialog=crash')
  else if SameText(LWhich, 'settings') then
    SettingsClick(nil)
  else if SameText(LWhich, 'layouts') then
    LayoutsClick(nil)
  // The Setup dialog builds itself from the sources and the device, so opening it is
  // the only way to find out that it still builds at all. It starts the service, which
  // is what it does on the New session button too.
  else if SameText(LWhich, 'setup') or SameText(LWhich, 'callspec') then
    // callspec goes through Setup: the picker needs the project the dialog has scanned.
    StartButtonClick(nil);
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

/// The grid the user is working in, so Export means "this table" and not "the one the
/// code happens to know about". Walks up from the focused control, which is where the
/// answer actually lives.
function TMainForm.FocusedGrid: TcxGrid;
var
  LControl: TWinControl;
begin
  LControl := Screen.ActiveControl;
  while LControl <> nil do
  begin
    if LControl is TcxGrid then
      Exit(TcxGrid(LControl));
    LControl := LControl.Parent;
  end;
  Result := FGrid;
end;

procedure TMainForm.ExportClick(Sender: TObject);
var
  LDialog: TSaveDialog;
  LGrid: TcxGrid;
begin
  LGrid := FocusedGrid;
  if LGrid = nil then
  begin
    dxMessageDlg('There is no table to export here.', mtInformation, [mbOK], 0);
    Exit;
  end;
  LDialog := TSaveDialog.Create(Self);
  try
    LDialog.Title := 'Export the table';
    LDialog.Filter := 'Excel workbook (*.xlsx)|*.xlsx|Comma separated (*.csv)|*.csv|' +
      'Web page (*.html)|*.html|Plain text (*.txt)|*.txt';
    LDialog.DefaultExt := 'xlsx';
    LDialog.Options := LDialog.Options + [ofOverwritePrompt];
    LDialog.FileName := 'profile';
    if not LDialog.Execute then
      Exit;
    ExportGrid(LGrid, LDialog.FileName);
    SetStatus('exported to ' + LDialog.FileName);
  finally
    LDialog.Free;
  end;
end;

/// Grouping, sorting and the find filter are part of what the user is looking at, so the
/// export follows the view rather than the underlying table. The format comes from the
/// extension, which is what both the dialog and --export already carry.
procedure TMainForm.ExportGrid(AGrid: TcxGrid; const AFileName: string);
var
  LExtension: string;
begin
  LExtension := LowerCase(TPath.GetExtension(AFileName));
  if LExtension = '.csv' then
    ExportGridToCSV(AFileName, AGrid)
  else if LExtension = '.html' then
    ExportGridToHTML(AFileName, AGrid)
  else if LExtension = '.txt' then
    ExportGridToText(AFileName, AGrid)
  else
    ExportGridToXLSX(AFileName, AGrid);
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
  // The dialog can rename, delete or set the default: the menu is built again either way.
  UpdateLayoutMenu(nil);
  if LName = '' then
    Exit;
  ApplyNamedLayout(LName);
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

/// A session is a folder with a session.db in it; both are accepted, because both are
/// what people have at hand - a path from a tool, or the folder they were shown.
function TMainForm.ResolveSessionPath(const APath: string): string;
begin
  Result := APath;
  if TDirectory.Exists(Result) then
    Result := TPath.Combine(Result, 'session.db');
  if not TFile.Exists(Result) then
    raise ESessionStore.CreateFmt(
      'No session database at %s. Point at a session.db, or at the folder that holds one.', [APath]);
end;

procedure TMainForm.LoadSession(const APath: string);
var
  LPath: string;
begin
  LPath := ResolveSessionPath(APath);
  FStore.Open(LPath);
  // A session opened from somewhere else joins the folders the Explorer browses; it does
  // not become the folder sessions are kept in - that is a setting, and one open file
  // should not silently change it.
  RememberSessionFolder(TDirectory.GetParent(TDirectory.GetParent(LPath)));
  ReloadExplorer;
  LoadMemory;
  UpdateSummary;
  LoadReport;
  LoadTreeRoots;
  UpdateInfo;
end;

{ The control channel's window API. Nothing here is new behaviour: every one of these is
  what a click already does, given a name so that it can be asked for from outside. }

function TMainForm.PanelByName(const AName: string): TdxDockPanel;
var
  LNames: TArray<string>;
  LPanels: TArray<TdxDockPanel>;
  LIndex: Integer;
begin
  LNames := ['report', 'explorer', 'details', 'tree', 'graph', 'source', 'memory', 'monitor', 'summary', 'log'];
  LPanels := [FReportPanel, FExplorerPanel, FDetailsDock, FTreePanel, FGraphPanel, FSourcePanel,
              FMemoryPanel, FMonitorPanel, FSummaryPanel, FLogPanel];
  for LIndex := 0 to High(LNames) do
    if SameText(AName, LNames[LIndex]) then
      Exit(LPanels[LIndex]);
  Result := nil;
end;

function TMainForm.PanelNames: string;
begin
  Result := 'report, explorer, details, tree, graph, source, memory, monitor, summary, log';
end;

function TMainForm.ActivatePanel(const AName: string): Boolean;
var
  LPanel: TdxDockPanel;
begin
  LPanel := PanelByName(AName);
  Result := LPanel <> nil;
  if not Result then
    Exit;
  LPanel.Activate;
  FActivePanel := LowerCase(AName);
  GiveRoomTo(FActivePanel);
  UpdateEditorScrollBars;
end;

function TMainForm.ActivePanelName: string;
begin
  if FActivePanel = '' then
    Result := 'report'
  else
    Result := FActivePanel;
end;

function TMainForm.PanelControl(const AName: string): TControl;
var
  LPanel: TdxDockPanel;
begin
  if AName = '' then
    LPanel := PanelByName(ActivePanelName)
  else
    LPanel := PanelByName(AName);
  Result := LPanel;
end;

procedure TMainForm.OpenSessionPath(const APath: string);
begin
  LoadSession(APath);
end;

function TMainForm.CurrentSessionPath: string;
begin
  if FStore.IsOpen then
    Result := FStore.Path
  else
    Result := '';
end;

function TMainForm.CurrentMethodName: string;
begin
  Result := '';
  if (FReportQuery = nil) or not FReportQuery.Active then
    Exit;
  if FReportQuery.FindField('full_name') = nil then
    Exit;
  Result := FReportQuery.FieldByName('full_name').AsString;
end;

/// The report row for a method, and with it the Details, Call graph and Source panels,
/// which follow the focused row exactly as they do when it is clicked. An exact name
/// wins; failing that the first row that starts with what was asked for.
function TMainForm.FocusMethodByName(const AName: string): Boolean;
begin
  Result := False;
  if (FReportQuery = nil) or not FReportQuery.Active or (FReportQuery.FindField('full_name') = nil) then
    Exit;
  Result := FReportQuery.Locate('full_name', AName, [loCaseInsensitive]);
  if not Result then
    Result := FReportQuery.Locate('full_name', AName, [loCaseInsensitive, loPartialKey]);
  if not Result then
  begin
    // Locate matches from the start; a method is far more often remembered by its own
    // name than by the namespace in front of it, so the last resort is a plain search.
    FReportQuery.First;
    while not FReportQuery.Eof do
    begin
      if ContainsText(FReportQuery.FieldByName('full_name').AsString, AName) then
        Exit(FocusFoundRow);
      FReportQuery.Next;
    end;
    Exit(False);
  end;
  Result := FocusFoundRow;
end;

/// The panels that follow the focused row - Details, Call graph, Source - as they do when
/// it is clicked. Always True: the row is already there.
function TMainForm.FocusFoundRow: Boolean;
begin
  LoadDetails(FocusedMethodId);
  ShowGraphOf(FocusedMethodId);
  // A session without symbols has no source to show; that is not a failure of the focus.
  try
    ShowSourceOf(FocusedMethodId);
  except
    on E: Exception do
      LogLine('source: ' + E.Message);
  end;
  Result := True;
end;

function TMainForm.ReportColumn(const AField: string): TcxGridDBColumn;
var
  LIndex: Integer;
begin
  for LIndex := 0 to FGridView.ColumnCount - 1 do
    if SameText(FGridView.Columns[LIndex].DataBinding.FieldName, AField) then
      Exit(FGridView.Columns[LIndex]);
  Result := nil;
end;

/// Show only the methods whose name contains the text, the way typing in the column
/// filter does. An empty text puts every row back.
procedure TMainForm.FilterReport(const AText: string);
var
  LColumn: TcxGridDBColumn;
begin
  LColumn := ReportColumn('full_name');
  if LColumn = nil then
    Exit;
  FGridView.DataController.Filter.Root.Clear;
  if AText = '' then
  begin
    FGridView.DataController.Filter.Active := False;
    Exit;
  end;
  FGridView.DataController.Filter.Root.AddItem(LColumn, foLike, '%' + AText + '%', AText);
  FGridView.DataController.Filter.Active := True;
end;

procedure TMainForm.SortReportBy(const AField: string; ADescending: Boolean);
var
  LColumn: TcxGridDBColumn;
begin
  LColumn := ReportColumn(AField);
  if LColumn = nil then
    raise ESessionStore.CreateFmt('This session has no column called "%s".', [AField]);
  if ADescending then
    LColumn.SortOrder := soDescending
  else
    LColumn.SortOrder := soAscending;
end;

/// Put the window on the screen, or take it off it. The channel renders with the window
/// hidden; showing it is a request of its own, because somebody has to be looking.
procedure TMainForm.SetWindowVisible(AVisible: Boolean);
begin
  if AVisible then
  begin
    Application.ShowMainForm := True;
    Show;
    if WindowState = wsMinimized then
      WindowState := wsNormal;
    BringToFront;
  end
  else
    Hide;
end;

/// Rendering happens at the size the window has, so this is how a caller asks for a
/// larger picture. The docking layout re-lays itself out, hidden or not.
procedure TMainForm.ResizeClient(AWidth, AHeight: Integer);
begin
  if (AWidth < 200) or (AHeight < 200) then
    raise EArgumentException.Create('A window smaller than 200x200 has nothing readable on it.');
  ClientWidth := AWidth;
  ClientHeight := AHeight;
  FDockSite.Realign;
  Application.ProcessMessages;
end;

/// A picture is worth having only if the panel has room. The Report panel is the client
/// zone and the rest are tabs at the bottom, so making one large means shrinking the
/// other: the layout is restored from its file when the window is next opened by a person,
/// and a driven window never saves it.
procedure TMainForm.GiveRoomTo(const APanel: string);
var
  LDetails: TdxCustomDockControl;
  LHeight: Integer;
begin
  // While the constructor is still running the panels have no parent window yet, and
  // realigning them there is what "PanelReport has no parent window" means.
  if not FBuilt then
    Exit;
  if FDetailsDock.ParentDockControl <> nil then
    LDetails := FDetailsDock.ParentDockControl
  else
    LDetails := FDetailsDock;
  FExplorerPanel.Width := 260;
  if SameText(APanel, 'report') or SameText(APanel, 'explorer') then
    LHeight := 120
  else
    LHeight := ClientHeight - 180;
  // Asked for more than once on purpose: a zone that has to grow by hundreds of pixels
  // lands part of the way on the first pass, because the docking library recomputes the
  // siblings from the size it had. Asking again, after its messages have run, arrives.
  for var LPass := 1 to 5 do
  begin
    LDetails.Height := Max(120, LHeight);
    // The client zone keeps whatever size it was built with until it is told: a window
    // that was never shown has never laid itself out.
    FReportPanel.Height := Max(140, ClientHeight - LDetails.Height - 70);
    FReportPanel.Width := Max(200, ClientWidth - FExplorerPanel.Width - 20);
    FDockSite.Realign;
    Application.ProcessMessages;
    if Abs(LDetails.Height - Max(120, LHeight)) <= 4 then
      Break;
  end;
end;

/// --render=<panel>:<file.png>: one picture from the command line, no channel. What
/// smoke tests and scripts use, and the same rendering the channel performs.
procedure TMainForm.RenderPanelToFile(const APanelAndFile: string);
var
  LSeparator: Integer;
  LPanel, LFile: string;
  LControl: TControl;
begin
  LSeparator := Pos(':', APanelAndFile);
  // A drive letter is not the separator we are looking for.
  if (LSeparator = 0) or (LSeparator = 2) then
    raise EArgumentException.Create('--render wants <panel>:<file.png>, e.g. --render=graph:C:\out\graph.png');
  LPanel := Copy(APanelAndFile, 1, LSeparator - 1);
  LFile := Copy(APanelAndFile, LSeparator + 1, MaxInt);
  if not ActivatePanel(LPanel) then
    raise EArgumentException.CreateFmt('Unknown panel "%s". One of: %s.', [LPanel, PanelNames]);
  LControl := PanelControl(LPanel);
  TFile.WriteAllBytes(LFile, ControlToPng(LControl, Color));
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

/// The GUI owns the control service. Where nap.exe is depends on how this copy was
/// installed: next to the window during development, one directory up in bin\ when
/// the package put the GUI in gui\, or wherever the settings say.
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
    // The package: gui\NapGui.exe with the tools in bin\ beside it.
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..'+PathDelim+'bin'+PathDelim+'nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Debug\net10.0\nap.exe'),
    TPath.Combine(TPath.GetDirectoryName(ParamStr(0)), '..\src\NetAndroidProfiler.Cli\bin\Release\net10.0\nap.exe')];
  for LPath in LCandidates do
    if (LPath <> '') and TFile.Exists(LPath) then
    begin
      try
        // The sessions folder from the settings, so the Explorer and the service agree
        // on where results live.
        FClient.StartService(TPath.GetFullPath(LPath), GSettings.SessionsRoot);
        LogLine('control service: ' + FClient.BaseUrl);
        CheckPrerequisites;
        Exit(True);
      except
        on E: Exception do
        begin
          dxMessageDlg('Cannot start the control service:' + sLineBreak + E.Message, mtError, [mbOK], 0);
          Exit(False);
        end;
      end;
    end;
  dxMessageDlg('nap.exe was not found next to this application, nor in a bin folder ' +
    'beside it.' + sLineBreak +
    'Point Settings at it, or keep the package together.', mtError, [mbOK], 0);
  Result := False;
end;

/// The tools a session needs are installed outside this application, and the one that is
/// usually missing - dsrouter - is a single command away. Asking here, the moment the
/// service comes up, is the difference between one click and a session that dies later
/// with a message about an executable nobody remembers having to install.
procedure TMainForm.CheckPrerequisites;
var
  LTools: TArray<TToolStatus>;
  LTool: TToolStatus;
  LJob: TJobStatus;
  LMissing: Integer;
begin
  try
    LTools := FClient.Prerequisites;
  except
    on E: Exception do
    begin
      LogLine('prerequisites: ' + E.Message);
      Exit;
    end;
  end;

  LMissing := 0;
  for LTool in LTools do
  begin
    if LTool.Found then
    begin
      LogLine(Format('%s: %s', [LTool.Name, LTool.Path]));
      Continue;
    end;
    // Something that is only needed to build from here is worth a line in the log, not a
    // dialog in front of someone who came to read a session.
    if not LTool.Required then
    begin
      LogLine(Format('%s: not found. %s', [LTool.Name, LTool.Fix]));
      Continue;
    end;

    Inc(LMissing);
    LogLine(Format('%s: NOT FOUND. %s', [LTool.Name, LTool.Fix]));
    if LTool.InstallCommand = '' then
    begin
      dxMessageDlg(Format('%s was not found.'#13#10#13#10'%s'#13#10#13#10'%s',
        [LTool.Name, LTool.Purpose, LTool.Fix]), mtWarning, [mbOK], 0);
      Continue;
    end;

    if dxMessageDlg(Format('%s was not found, and every profiling session needs it.'#13#10#13#10 +
      '%s'#13#10#13#10'Install it now?'#13#10#13#10'    %s',
      [LTool.Name, LTool.Purpose, LTool.InstallCommand]), mtConfirmation, [mbYes, mbNo], 0) <> mrYes then
      Continue;
    try
      LJob := FClient.InstallTool(LTool.Name);
    except
      on E: Exception do
      begin
        dxMessageDlg(E.Message, mtError, [mbOK], 0);
        Continue;
      end;
    end;
    if TJobDialog.Run(Self, FClient, LJob, 'Installing ' + LTool.Name, LTool.InstallCommand) then
      Dec(LMissing);
  end;

  if LMissing > 0 then
    SetStatus('A tool that profiling needs is missing - see the log.');
end;

procedure TMainForm.StartButtonClick(Sender: TObject);
begin
  StartSessionFrom(Default(TSessionRequest), False);
end;

/// The session whose results are on screen: where the toolbar's Run again reads its setup.
function TMainForm.OpenSessionDirectory: string;
begin
  Result := '';
  if FStore.IsOpen and (FStore.Path <> '') then
    Result := TPath.GetDirectoryName(FStore.Path);
end;

procedure TMainForm.RunAgainClick(Sender: TObject);
begin
  RunAgain(OpenSessionDirectory);
end;

procedure TMainForm.ExplorerRunAgainClick(Sender: TObject);
var
  LRef: TExplorerRef;
begin
  if FocusedSession(LRef) and (LRef.Kind = ekSession) then
    RunAgain(TPath.GetDirectoryName(LRef.DatabasePath));
end;

{ The same measurement once more. A session records everything it was started with, so
  this is the setup dialog opened on what that session recorded - the point being that
  nobody retypes a solution, a callspec and eleven assembly names to ask the same question
  a second time. }
procedure TMainForm.RunAgain(const ASessionDirectory: string);
var
  LRequest: TSessionRequest;
begin
  if ASessionDirectory = '' then
    Exit;
  if not TryReadSessionSpec(ASessionDirectory, LRequest) then
  begin
    dxMessageDlg('That session did not keep what it was started with, so it cannot be run again.'
      + sLineBreak + 'Use New session... and set it up once; from then on Run again works.',
      mtInformation, [mbOK], 0);
    Exit;
  end;
  StartSessionFrom(LRequest, True);
end;

function TMainForm.StartSessionFrom(const ARequest: TSessionRequest; APrefilled: Boolean): Boolean;
var
  LDialog: TSetupDialog;
  LSetup: TSetupResult;
  LStatus: TSessionStatus;
begin
  Result := False;
  if not EnsureService then
    Exit;
  LDialog := TSetupDialog.Create(Self, FClient);
  try
    if APrefilled then
      LDialog.PrefillFrom(ARequest);
    if not LDialog.Execute(LSetup) then
      Exit;
  finally
    LDialog.Free;
  end;
  try
    LStatus := FClient.StartSession(LSetup);
  except
    on E: Exception do
    begin
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
      Exit;
    end;
  end;
  Result := True;
  FSessionId := LStatus.Id;
  // Which database belongs to the session this window is running: what tells a live
  // recording apart from a stored one that happens to be open.
  FLiveDatabasePath := LStatus.DatabasePath;
  FPaused := False;
  FLiveExplained := False;
  // Get Results, Pause and Clear read and rewrite files the collector flushes as it goes,
  // which only the weaver engine produces. A runtime-provider trace becomes readable
  // when the session ends, so the buttons stay off rather than failing on the click.
  FLiveControllable := SameText(LSetup.Mode, 'instrumenting') and
    not SameText(LSetup.Engine, 'provider');
  // With auto the engine is decided on the device, so the buttons follow the session's
  // state rather than a guess made here.
  if SameText(LSetup.Engine, 'auto') then
    FLiveControllable := SameText(LSetup.Mode, 'instrumenting');
  // A session kept somewhere else must not disappear from the Explorer the moment it is
  // created: the folder joins the ones the tree browses.
  RememberSessionFolder(LSetup.SessionsRoot);
  SetLength(FMonitorSamples, 0);
  LogLine('session ' + FSessionId + ' started');
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
      LogLine('status failed: ' + E.Message);
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
    // It is a recording now, not a session in progress: what can still be done to it is
    // what can be done to any stored result.
    FLiveDatabasePath := '';
    if LStatus.Error <> '' then
      dxMessageDlg(LStatus.Error, mtError, [mbOK], 0);
    if (LStatus.DatabasePath <> '') and TFile.Exists(LStatus.DatabasePath) then
      LoadSession(LStatus.DatabasePath);
  end;
end;

/// AQTime's habit: collect a Get Results and never lose it. The name is asked for
/// because "snapshot 3" tells you nothing in three days and "the customers screen" does.
procedure TMainForm.ArchiveButtonClick(Sender: TObject);
var
  LName, LPath: string;
begin
  if FSessionId = '' then
    Exit;
  LName := Format('snapshot %s', [FormatDateTime('hh:nn:ss', Now)]);
  if not dxInputQuery('Archive results', 'Keep the results collected so far as:', LName) then
    Exit;
  try
    LPath := FClient.Archive(FSessionId, Trim(LName));
    LogLine(Format('archived "%s" to %s', [Trim(LName), LPath]));
    // The archive was taken from a fresh snapshot, so the panels are behind by one.
    LoadSession(FStore.Path);
    ReloadExplorer;
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

/// The Log panel is dockable, which means it can be closed, auto-hidden, or simply not
/// realised yet while the window is still coming up - and writing into a control whose
/// parent has no window handle raises "has no parent window" and takes the application
/// down with it. Lines written in those moments wait here instead of being lost.
/// The Summary lives in a dock panel, which can be a background tab, closed, or not yet
/// realised while the window is coming up. Writing into a control whose parent has no
/// window raises "has no parent window" - the text waits instead, and goes in when the
/// panel is next shown.
procedure TMainForm.SetSummary(ALines: TStrings);
begin
  if FSummaryPending = nil then
    FSummaryPending := TStringList.Create;
  FSummaryPending.Assign(ALines);
  FlushSummary;
end;

procedure TMainForm.FlushSummary;
begin
  if (FSummary = nil) or (FSummaryPending = nil) or (FSummary.Parent = nil)
    or not FSummary.Parent.HandleAllocated then
    Exit;
  try
    FSummary.Lines.Assign(FSummaryPending);
  except
    // The panel was not ready after all: the text stays where it is and goes in later.
  end;
end;

procedure TMainForm.SummaryPanelActivated(Sender: TdxCustomDockControl; AActive: Boolean);
begin
  if AActive then
    FlushSummary;
end;

procedure TMainForm.LogLine(const AText: string);
var
  I: Integer;
begin
  if FPendingLog = nil then
    FPendingLog := TStringList.Create;
  if (FLog = nil) or (FLog.Parent = nil) or not FLog.Parent.HandleAllocated then
  begin
    FPendingLog.Add(AText);
    Exit;
  end;
  try
    for I := 0 to FPendingLog.Count - 1 do
      FLog.Lines.Add(FPendingLog[I]);
    FPendingLog.Clear;
    FLog.Lines.Add(AText);
  except
    on E: Exception do
      // Never let logging be the thing that fails: the line waits for a better moment.
      FPendingLog.Add(AText);
  end;
end;

/// A disabled button that does not say why is a bug report waiting to happen. Sampling
/// and the runtime-provider engine cannot produce partial results at all: a .nettrace
/// resolves its method names only when the session ends.
procedure TMainForm.ExplainLiveButtons(ARunning: Boolean);
const
  CWhy = 'Only an instrumenting session on a weaver engine can do this while the app runs: '
    + 'the collector writes files it flushes as it goes. Sampling and the provider engine '
    + 'produce a trace that only becomes readable when the session ends.';
  CIdle = 'Available while an instrumenting session is running.';
var
  LHint: string;
begin
  if ARunning and not FLiveControllable then
    LHint := CWhy
  else if not ARunning then
    LHint := CIdle
  else
    LHint := '';
  if LHint <> '' then
  begin
    FSnapshotButton.Hint := LHint;
    FArchiveButton.Hint := LHint;
    FPauseButton.Hint := LHint;
    FClearButton.Hint := LHint;
  end;
  // Say it once, where the user is already looking, rather than only in a tooltip.
  if ARunning and not FLiveControllable and not FLiveExplained then
  begin
    FLiveExplained := True;
    LogLine('Get Results, Archive, Pause and Clear are off for this session: ' + CWhy);
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
    LogLine(Format('snapshot %d', [LSegment]));
    // Results are cumulative: the tables were rewritten with everything collected so
    // far, so this is a plain reload rather than a merge.
    if (LStatus.DatabasePath <> '') and TFile.Exists(LStatus.DatabasePath) then
      LoadSession(LStatus.DatabasePath);
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

/// AQTime's Clear Results: throw away what has been collected and carry on. What it
/// cannot do is remove the instrumentation - woven IL stays woven - so the overhead
/// remains, and the message says so.
procedure TMainForm.ClearButtonClick(Sender: TObject);
var
  LStatus: TSessionStatus;
begin
  if FSessionId = '' then
    Exit;
  if dxMessageDlg('Throw away everything collected so far in this session?' + sLineBreak +
    'The app keeps running and stays instrumented, so collection continues from zero.',
    mtConfirmation, [mbYes, mbNo], 0) <> mrYes then
    Exit;
  try
    LStatus := FClient.Clear(FSessionId);
    LogLine('results cleared');
    if (LStatus.DatabasePath <> '') and TFile.Exists(LStatus.DatabasePath) then
      LoadSession(LStatus.DatabasePath);
    SetStatus(Format('session %s: %s, results cleared', [FSessionId, LStatus.State]));
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
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
      LogLine('resumed');
    end
    else
    begin
      FClient.Pause(FSessionId);
      FPaused := True;
      LogLine('paused - the methods stay instrumented, so the overhead remains');
    end;
    FPauseButton.Caption := IfThen(FPaused, 'Resume', 'Pause');
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

{ A session started paused is set up, the app is running, and nothing is being measured:
  this is the moment somebody has reached the screen worth looking at. }
procedure TMainForm.RecordButtonClick(Sender: TObject);
begin
  if FSessionId = '' then
    Exit;
  try
    FClient.Resume(FSessionId);
    LogLine('recording started');
    SetStatus('Recording.');
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.StopButtonClick(Sender: TObject);
begin
  if FSessionId = '' then
    Exit;
  try
    FClient.Stop(FSessionId);
    LogLine('stopping...');
    FPoll.Enabled := True;         // the analysis runs after collection ends
  except
    on E: Exception do
      dxMessageDlg(E.Message, mtError, [mbOK], 0);
  end;
end;

procedure TMainForm.UpdateButtons(const AState: string);
var
  LRunning: Boolean;
begin
  LRunning := (FSessionId <> '') and
    ((AState = 'Collecting') or (AState = 'WaitingForApp') or (AState = 'Preparing')
     or (AState = 'WaitingToRecord'));
  // The one button that matters while a paused session waits, and the only time it is on.
  FRecordButton.Enabled := (FSessionId <> '') and (AState = 'WaitingToRecord');
  // Running it again makes sense while looking at results and only while nothing is
  // running: two sessions on one device would fight over the diagnostic port.
  FRunAgainButton.Enabled := (not LRunning) and (OpenSessionDirectory <> '');
  if AState = 'WaitingToRecord' then
    SetStatus('The app is running and nothing is being measured. Press Record when you are where you want to look.');
  FSnapshotButton.Enabled := LRunning and FLiveControllable;
  // Archiving a finished session would only copy what the Explorer already shows.
  FArchiveButton.Enabled := LRunning and FLiveControllable;
  ExplainLiveButtons(LRunning);
  FPauseButton.Enabled := LRunning and FLiveControllable;
  FClearButton.Enabled := LRunning and FLiveControllable;
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
      LogLine(ALines[I]);
  while FLog.Lines.Count > 500 do
    FLog.Lines.Delete(0);
end;

end.
