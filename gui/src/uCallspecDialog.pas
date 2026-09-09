unit uCallspecDialog;

{
  Choosing what to instrument, which is the decision that makes or breaks an instrumenting
  session: too little and the answer is not in the results, too much and the app is too
  slow to reach the part you wanted to measure.

  A combo box could not carry that. What the app declares is a tree - namespaces inside
  namespaces, types inside them - and the choice is a set, not one item: several
  namespaces, minus a type, plus a type from somewhere else. So: a tree with a box per
  node, a filter that matches anywhere in the name (not only the start), the method count
  next to each node because that is what the choice costs, and a second column for the
  namespaces that deserve per-line detail.

  The result is an ordinary callspec, the same string the engine and the MCP tools take:
  a checked namespace becomes N:Full.Name and swallows everything under it, a checked type
  becomes T:Full.Name.
}

interface

uses
  System.SysUtils, System.Classes, System.Generics.Collections, System.Generics.Defaults,
  Vcl.Controls, Vcl.Forms, Vcl.Graphics, Vcl.ExtCtrls,
  cxGraphics, cxControls, cxLookAndFeels, cxLookAndFeelPainters, cxStyles, cxEdit,
  cxTL, cxTLdxBarBuiltInMenu, cxInplaceContainer, cxTextEdit, cxCheckBox, cxLabel,
  cxButtons, cxMaskEdit, cxDropDownEdit,
  uControlClient;

type
  /// What the dialog answers: the methods to instrument, and those to measure line by line.
  TCallspecSelection = record
    Callspec: string;
    /// Same grammar, for the per-line detail. Kept even though the engine does not
    /// collect it yet: the choice is the user's, and it survives until it can be used.
    LineCallspec: string;
  end;

  TCallspecDialog = class(TForm)
  private
    FTree: TcxTreeList;
    FName: TcxTreeListColumn;
    FLines: TcxTreeListColumn;
    FMethods: TcxTreeListColumn;
    FFilter: TcxTextEdit;
    FSummary: TcxLabel;
    FOk: TcxButton;
    FCandidates: TArray<TCallspecCandidate>;
    /// Checked state by callspec, so it survives filtering and rebuilding the tree.
    FChecked: TDictionary<string, Boolean>;
    FLineChecked: TDictionary<string, Boolean>;
    /// What each node stands for, indexed by the node's Data.
    FNodes: TArray<TCallspecCandidate>;
    /// Methods per node index, containers included: what the summary adds up.
    FTotals: TDictionary<Integer, Integer>;
    FBuilding: Boolean;
    /// Ticking a namespace raises one event per node under it - hundreds of them - and the
    /// summary costs a walk of every candidate. It is recomputed once the storm is over.
    FSummaryDue: TTimer;
    procedure Build;
    procedure Fill;
    procedure FilterChanged(Sender: TObject);
    procedure NodeChecked(Sender: TcxCustomTreeList; ANode: TcxTreeListNode;
      AState: TcxCheckBoxState);
    function TotalOf(ANode: TcxTreeListNode): Integer;
    procedure RestoreChecks(ANode: TcxTreeListNode);
    procedure LinesEdited(Sender: TObject);
    procedure ExpandClick(Sender: TObject);
    procedure CollapseClick(Sender: TObject);
    procedure NoneClick(Sender: TObject);
    function CandidateOf(ANode: TcxTreeListNode; out ACandidate: TCallspecCandidate): Boolean;
    procedure SetChecked(const ACallspec: string; AValue: Boolean);
    procedure UpdateSummary;
    procedure SummaryDue(Sender: TObject);
    function Selection: TCallspecSelection;
    procedure Preselect(const ASelection: TCallspecSelection);
    procedure Capture;
  public
    constructor Create(AOwner: TComponent); override;
    destructor Destroy; override;
    /// Shows the picker over the candidates the app declares. False when nothing changed.
    class function Execute(AOwner: TComponent; const ACandidates: TArray<TCallspecCandidate>;
      var ASelection: TCallspecSelection): Boolean;
  end;

implementation

uses
  System.UITypes, System.StrUtils;

const
  CNamespace = 'namespace';

{ ------------------------------------------------------------------ helpers }

/// The callspec without its two-letter prefix.
function NameOf(const ACallspec: string): string;
begin
  if (Length(ACallspec) > 2) and (ACallspec[2] = ':') then
    Result := Copy(ACallspec, 3, MaxInt)
  else
    Result := ACallspec;
end;

/// True when AName is ANamespace itself or lives inside it.
function InNamespace(const AName, ANamespace: string): Boolean;
begin
  Result := SameText(AName, ANamespace)
    or AName.StartsWith(ANamespace + '.', True)
    or AName.StartsWith(ANamespace + '+', True);
end;

{ ------------------------------------------------------------------ TCallspecDialog }

constructor TCallspecDialog.Create(AOwner: TComponent);
begin
  inherited CreateNew(AOwner);
  FChecked := TDictionary<string, Boolean>.Create;
  FLineChecked := TDictionary<string, Boolean>.Create;
  FTotals := TDictionary<Integer, Integer>.Create;
  FSummaryDue := TTimer.Create(Self);
  FSummaryDue.Interval := 80;
  FSummaryDue.Enabled := False;
  FSummaryDue.OnTimer := SummaryDue;
  Build;
end;

destructor TCallspecDialog.Destroy;
begin
  FChecked.Free;
  FLineChecked.Free;
  FTotals.Free;
  inherited Destroy;
end;

class function TCallspecDialog.Execute(AOwner: TComponent;
  const ACandidates: TArray<TCallspecCandidate>; var ASelection: TCallspecSelection): Boolean;
var
  LDialog: TCallspecDialog;
begin
  LDialog := TCallspecDialog.Create(AOwner);
  try
    LDialog.FCandidates := ACandidates;
    LDialog.Preselect(ASelection);
    LDialog.Fill;
    Result := LDialog.ShowModal = mrOk;
    if Result then
      ASelection := LDialog.Selection;
  finally
    LDialog.Free;
  end;
end;

procedure TCallspecDialog.Build;

  function Button(const ACaption: string; ALeft, ATop, AWidth: Integer;
    AOnClick: TNotifyEvent): TcxButton;
  begin
    Result := TcxButton.Create(Self);
    Result.Parent := Self;
    Result.SetBounds(ALeft, ATop, AWidth, 26);
    Result.Caption := ACaption;
    Result.OnClick := AOnClick;
  end;

var
  LLabel: TcxLabel;
  LCancel: TcxButton;
begin
  Caption := 'What to instrument';
  BorderStyle := bsSizeable;
  Position := poOwnerFormCenter;
  ClientWidth := 720;
  ClientHeight := 560;
  // Long namespaces and deep trees: it grows, and never shrinks below what the columns need.
  Constraints.MinWidth := 560;
  Constraints.MinHeight := 380;

  LLabel := TcxLabel.Create(Self);
  LLabel.Transparent := True;
  LLabel.Parent := Self;
  LLabel.SetBounds(16, 18, 40, 20);
  LLabel.Caption := 'Find';

  // Matches anywhere in the name, not only at the start: the interesting part of
  // App.Core.Reporting.Invoices is rarely its first word.
  FFilter := TcxTextEdit.Create(Self);
  FFilter.Parent := Self;
  FFilter.SetBounds(60, 16, 380, 24);
  FFilter.TextHint := 'part of a namespace or type name';
  FFilter.Properties.OnChange := FilterChanged;

  Button('Expand', 452, 15, 80, ExpandClick);
  Button('Collapse', 540, 15, 80, CollapseClick);
  Button('None', 628, 15, 76, NoneClick);

  FTree := TcxTreeList.Create(Self);
  FTree.Parent := Self;
  FTree.SetBounds(16, 52, 688, 430);
  FTree.Anchors := [akLeft, akTop, akRight, akBottom];
  FTree.OptionsData.Editing := True;
  FTree.OptionsSelection.CellSelect := False;
  FTree.OptionsView.ColumnAutoWidth := False;
  FTree.OptionsView.CheckGroups := True;
  // A node gets its box from its parent being a check group, and the top-level ones hang
  // from the invisible root - which therefore needs to be one too.
  FTree.Root.CheckGroupType := ncgCheckGroup;
  FTree.OnNodeCheckChanged := NodeChecked;

  FName := FTree.CreateColumn;
  FName.Caption.Text := 'Namespace / type';
  FName.Width := 430;
  FName.Options.Editing := False;

  FMethods := FTree.CreateColumn;
  FMethods.Caption.Text := 'Methods';
  FMethods.Width := 90;
  FMethods.Options.Editing := False;

  // The engine does not collect per-line figures yet; the column is here because the
  // choice is the user's to make, and it is remembered until the collection catches up.
  FLines := FTree.CreateColumn;
  FLines.Caption.Text := 'Per line (planned)';
  FLines.Width := 80;
  FLines.PropertiesClass := TcxCheckBoxProperties;
  (FLines.Properties as TcxCheckBoxProperties).NullStyle := nssUnchecked;
  (FLines.Properties as TcxCheckBoxProperties).OnChange := LinesEdited;

  FSummary := TcxLabel.Create(Self);
  FSummary.Transparent := True;
  FSummary.Parent := Self;
  FSummary.AutoSize := False;
  FSummary.SetBounds(16, 492, 688, 32);
  FSummary.Anchors := [akLeft, akRight, akBottom];
  FSummary.Properties.WordWrap := True;
  FSummary.Properties.ShowAccelChar := False;

  FOk := TcxButton.Create(Self);
  FOk.Parent := Self;
  FOk.SetBounds(508, 526, 90, 28);
  FOk.Anchors := [akRight, akBottom];
  FOk.Caption := 'OK';
  FOk.ModalResult := mrOk;
  FOk.Default := True;

  LCancel := TcxButton.Create(Self);
  LCancel.Parent := Self;
  LCancel.SetBounds(608, 526, 96, 28);
  LCancel.Anchors := [akRight, akBottom];
  LCancel.Caption := 'Cancel';
  LCancel.ModalResult := mrCancel;
  LCancel.Cancel := True;
end;

/// Reads a callspec back into checked nodes, so reopening the dialog shows what is there.
procedure TCallspecDialog.Preselect(const ASelection: TCallspecSelection);

  procedure Apply(const AText: string; ATarget: TDictionary<string, Boolean>);
  var
    LPart: string;
  begin
    for LPart in AText.Split([','], TStringSplitOptions.ExcludeEmpty) do
      if Trim(LPart) <> '' then
        ATarget.AddOrSetValue(Trim(LPart), True);
  end;

begin
  Apply(ASelection.Callspec, FChecked);
  Apply(ASelection.LineCallspec, FLineChecked);
end;

/// Takes what the tree says back into the dictionaries. Filtering rebuilds the tree from
/// them, so this is what makes a choice survive a search.
procedure TCallspecDialog.Capture;
var
  LSelection: TCallspecSelection;
begin
  if FTree.Root.Count = 0 then
    Exit;
  LSelection := Selection;
  FChecked.Clear;
  FLineChecked.Clear;
  Preselect(LSelection);
end;

procedure TCallspecDialog.Fill;
var
  LFilter: string;
  LFolders: TDictionary<string, TcxTreeListNode>;

  /// The node for a namespace, creating the chain of parents it needs.
  function NamespaceNode(const ANamespace: string): TcxTreeListNode;
  var
    LParent: TcxTreeListNode;
    LCut: Integer;
    LOwn: string;
    LSynthetic: TCallspecCandidate;
  begin
    if ANamespace = '' then
      Exit(nil);
    if LFolders.TryGetValue(ANamespace, Result) then
      Exit;
    LCut := ANamespace.LastDelimiter('.');
    if LCut < 0 then
    begin
      LParent := nil;
      LOwn := ANamespace;
    end
    else
    begin
      LParent := NamespaceNode(Copy(ANamespace, 1, LCut));
      LOwn := Copy(ANamespace, LCut + 2, MaxInt);
    end;
    if LParent = nil then
      Result := FTree.Add
    else
      Result := LParent.AddChild;
    Result.Values[0] := LOwn;
    if LParent <> nil then
      LParent.CheckGroupType := ncgCheckGroup;
    // A namespace that only holds other namespaces is still a legitimate choice - N:App
    // covers everything under the reference application - so it gets an entry of its own even when the app
    // declares no type directly in it.
    LSynthetic := Default(TCallspecCandidate);
    LSynthetic.Callspec := 'N:' + ANamespace;
    LSynthetic.Kind := CNamespace;
    FNodes := FNodes + [LSynthetic];
    Result.Data := Pointer(NativeInt(Length(FNodes)));
    Result.Values[2] := FLineChecked.ContainsKey('N:' + ANamespace);
    LFolders.Add(ANamespace, Result);
  end;

var
  I, LIndex: Integer;
  LNode: TcxTreeListNode;
  LName, LShort: string;
  LCut: Integer;
  LValue: Boolean;
begin
  Capture;
  FBuilding := True;
  FTree.BeginUpdate;
  LFolders := TDictionary<string, TcxTreeListNode>.Create;
  try
    FTree.Clear;
    FNodes := nil;
    FTotals.Clear;
    LFilter := Trim(FFilter.Text);

    // Namespaces first, so a type always finds its parent already there.
    for I := 0 to High(FCandidates) do
    begin
      if FCandidates[I].Kind <> CNamespace then
        Continue;
      LName := NameOf(FCandidates[I].Callspec);
      if (LFilter <> '') and not ContainsText(LName, LFilter) then
        Continue;
      LNode := NamespaceNode(LName);
      if LNode = nil then
        Continue;
      LIndex := Integer(NativeInt(LNode.Data));
      if (LIndex >= 1) and (LIndex <= Length(FNodes)) then
        FNodes[LIndex - 1] := FCandidates[I]
      else
      begin
        FNodes := FNodes + [FCandidates[I]];
        LNode.Data := Pointer(NativeInt(Length(FNodes)));
      end;
      LNode.Values[2] := FLineChecked.ContainsKey(FCandidates[I].Callspec);
    end;

    for I := 0 to High(FCandidates) do
    begin
      if FCandidates[I].Kind = CNamespace then
        Continue;
      LName := NameOf(FCandidates[I].Callspec);
      if (LFilter <> '') and not ContainsText(LName, LFilter) then
        Continue;
      LCut := LName.LastDelimiter('.');
      if LCut < 0 then
      begin
        LNode := FTree.Add;
        LShort := LName;
      end
      else
      begin
        LNode := NamespaceNode(Copy(LName, 1, LCut)).AddChild;
        LShort := Copy(LName, LCut + 2, MaxInt);
      end;
      LNode.Values[0] := LShort;
      LNode.Values[1] := FCandidates[I].Methods;
      if LNode.Parent <> nil then
        LNode.Parent.CheckGroupType := ncgCheckGroup;
      FNodes := FNodes + [FCandidates[I]];
      LNode.Data := Pointer(NativeInt(Length(FNodes)));
      LNode.Values[2] := FLineChecked.ContainsKey(FCandidates[I].Callspec);
    end;

    // What a container costs is the sum of what is under it: that number is the whole
    // point of showing the tree.
    for LIndex := 0 to FTree.Root.Count - 1 do
      TotalOf(FTree.Root.Items[LIndex]);

    // A container's tick is derived from its children, so a namespace read back from the
    // callspec has to tick what is under it - which is exactly what choosing it means.
    for LIndex := 0 to FTree.Root.Count - 1 do
      RestoreChecks(FTree.Root.Items[LIndex]);

    if LFilter <> '' then
      FTree.FullExpand
    else
      for LIndex := 0 to FTree.Root.Count - 1 do
        FTree.Root.Items[LIndex].Expand(False);
  finally
    LFolders.Free;
    FTree.EndUpdate;
    FBuilding := False;
  end;
  UpdateSummary;
end;

/// The methods a node covers: its own plus everything under it, written into the column
/// on the way. A namespace with nothing of its own still says what choosing it costs.
function TCallspecDialog.TotalOf(ANode: TcxTreeListNode): Integer;
var
  I, LIndex: Integer;
begin
  Result := 0;
  LIndex := Integer(NativeInt(ANode.Data));
  if (LIndex >= 1) and (LIndex <= Length(FNodes)) then
    Result := FNodes[LIndex - 1].Methods;
  for I := 0 to ANode.Count - 1 do
    Inc(Result, TotalOf(ANode.Items[I]));
  // Every node says what it costs, leaves included: an empty cell reads as "free".
  ANode.Values[1] := Result;
  FTotals.AddOrSetValue(LIndex, Result);
end;

/// Puts the remembered choice back on the tree, top down: a checked namespace takes its
/// descendants with it, which is how the library shows a container as checked at all.
procedure TCallspecDialog.RestoreChecks(ANode: TcxTreeListNode);
var
  I: Integer;
  LCandidate: TCallspecCandidate;
begin
  if CandidateOf(ANode, LCandidate) and FChecked.ContainsKey(LCandidate.Callspec) then
  begin
    // Setting it ticks everything under it: the tree does that itself.
    ANode.Checked := True;
    Exit;
  end;
  for I := 0 to ANode.Count - 1 do
    RestoreChecks(ANode.Items[I]);
end;

function TCallspecDialog.CandidateOf(ANode: TcxTreeListNode;
  out ACandidate: TCallspecCandidate): Boolean;
var
  LIndex: Integer;
begin
  ACandidate := Default(TCallspecCandidate);
  if ANode = nil then
    Exit(False);
  LIndex := Integer(NativeInt(ANode.Data));
  Result := (LIndex >= 1) and (LIndex <= Length(FNodes));
  if Result then
    ACandidate := FNodes[LIndex - 1];
end;

procedure TCallspecDialog.SetChecked(const ACallspec: string; AValue: Boolean);
begin
  if AValue then
    FChecked.AddOrSetValue(ACallspec, True)
  else
    FChecked.Remove(ACallspec);
end;

/// Only records what changed. The tree cascades a parent's tick to its children and
/// derives a parent's state from them by itself, and it raises this event for every node
/// it touches - so propagating here as well fought the library: ticking a child made the
/// parent recompute, which cascaded back down and undid the tick, and two branches at once
/// turned that into a storm.
procedure TCallspecDialog.NodeChecked(Sender: TcxCustomTreeList; ANode: TcxTreeListNode;
  AState: TcxCheckBoxState);
var
  LCandidate: TCallspecCandidate;
begin
  if FBuilding then
    Exit;
  if CandidateOf(ANode, LCandidate) then
    SetChecked(LCandidate.Callspec, AState = cbsChecked);
  FSummaryDue.Enabled := False;
  FSummaryDue.Enabled := True;
end;

procedure TCallspecDialog.SummaryDue(Sender: TObject);
begin
  FSummaryDue.Enabled := False;
  UpdateSummary;
end;

procedure TCallspecDialog.LinesEdited(Sender: TObject);
var
  LCandidate: TCallspecCandidate;
begin
  if FBuilding or (FTree.FocusedNode = nil) then
    Exit;
  if not CandidateOf(FTree.FocusedNode, LCandidate) then
    Exit;
  if FTree.FocusedNode.Values[2] = True then
    FLineChecked.AddOrSetValue(LCandidate.Callspec, True)
  else
    FLineChecked.Remove(LCandidate.Callspec);
  UpdateSummary;
end;

procedure TCallspecDialog.ExpandClick(Sender: TObject);
begin
  FTree.FullExpand;
end;

procedure TCallspecDialog.CollapseClick(Sender: TObject);
begin
  FTree.FullCollapse;
end;

procedure TCallspecDialog.NoneClick(Sender: TObject);
begin
  FChecked.Clear;
  FLineChecked.Clear;
  Fill;
end;

/// What the choice covers and what it costs: the number next to it is the reason a
/// namespace is usually the wrong unit to pick.
procedure TCallspecDialog.UpdateSummary;
var
  LSelection: TCallspecSelection;
  LTotal, LCount, LNodeTotal: Integer;
  I: Integer;
  LPart: string;
begin
  LSelection := Selection;
  // The outermost choices are the ones that count: a namespace already covers what is
  // under it, and adding its types again would double the estimate.
  LCount := 0;
  LTotal := 0;
  for LPart in LSelection.Callspec.Split([','], TStringSplitOptions.ExcludeEmpty) do
  begin
    Inc(LCount);
    for I := 0 to High(FNodes) do
      if SameText(FNodes[I].Callspec, LPart) then
      begin
        if FTotals.TryGetValue(I + 1, LNodeTotal) then
          Inc(LTotal, LNodeTotal)
        else
          Inc(LTotal, FNodes[I].Methods);
        Break;
      end;
  end;

  if LCount = 0 then
    FSummary.Caption := 'Nothing chosen yet. Tick a namespace to take everything in it, or a type '
      + 'to take that type alone.'
  else
    FSummary.Caption := Format('%d chosen, about %d methods instrumented. %s',
      [LCount, LTotal,
       IfThen(LTotal > 500, 'That is a lot: every one of them pays an enter and a leave on every '
         + 'call, from the first line of startup.', '')]);
  if LSelection.LineCallspec <> '' then
    FSummary.Caption := FSummary.Caption + ' Per-line detail is remembered but not collected yet.';
  FOk.Enabled := True;
end;

/// What the tree says, read from the tree itself. Accumulating the events the library
/// raises looked simpler and was wrong: it raises them for every node it touches, in an
/// order of its own, and some of them arrive after the update that caused them - so the
/// answer drifted from what the user could see. The nodes are the truth.
function TCallspecDialog.Selection: TCallspecSelection;

  procedure Walk(ANode: TcxTreeListNode; AChosen, ALines: TList<string>);
  var
    I: Integer;
    LCandidate: TCallspecCandidate;
    LTaken: Boolean;
  begin
    LTaken := False;
    if CandidateOf(ANode, LCandidate) then
    begin
      // A ticked namespace covers everything under it, so it is named once and its
      // subtree is not walked: that is what keeps the callspec short.
      if ANode.CheckState = cbsChecked then
      begin
        AChosen.Add(LCandidate.Callspec);
        LTaken := True;
      end;
      if ANode.Values[2] = True then
        ALines.Add(LCandidate.Callspec);
    end;
    if LTaken then
      Exit;
    for I := 0 to ANode.Count - 1 do
      Walk(ANode.Items[I], AChosen, ALines);
  end;

var
  LChosen, LLines: TList<string>;
  I: Integer;
begin
  LChosen := TList<string>.Create;
  LLines := TList<string>.Create;
  try
    for I := 0 to FTree.Root.Count - 1 do
      Walk(FTree.Root.Items[I], LChosen, LLines);
    Result.Callspec := string.Join(',', LChosen.ToArray);
    Result.LineCallspec := string.Join(',', LLines.ToArray);
  finally
    LChosen.Free;
    LLines.Free;
  end;
end;

procedure TCallspecDialog.FilterChanged(Sender: TObject);
begin
  Fill;
end;

end.
