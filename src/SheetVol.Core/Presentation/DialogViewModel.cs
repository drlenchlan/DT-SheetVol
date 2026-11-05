using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SheetVol.Core.Presentation;

/// <summary>
/// View model of the SVDIALOG window (HANDOFF.md 6.14). It edits the same
/// <see cref="RunDefinition"/> as SVDEFINE and reaches the engine only through
/// <see cref="ISheetVolSession"/> plus the <see cref="IDialogHost"/> port.
/// <para>
/// Every edit re-runs <see cref="ISheetVolSession.Validate"/> and nothing else (live
/// validation). Volumes come only from Regenerate, or from a regen the host reports
/// through <see cref="IDialogHost.GetRunStatus"/> (E912). Edits not yet regenerated are
/// kept per drawing in <see cref="PendingEdits"/> and restored on reopen (E907).
/// </para>
/// </summary>
public sealed class DialogViewModel : ObservableObject, IGridOwner, IDisposable
{
    private readonly ISheetVolSession _session;
    private readonly IDialogHost _host;
    private readonly PendingEdits _pending;
    private readonly PresentationConfig _config;
    private readonly string _runName;

    private DrawingInfo? _drawing;
    private DisplayFormatter _fmt;
    private RunDefinition _run;                 // what the dialog edits
    private RunDefinition _baseline;            // as stored in the drawing, or as last regenerated
    private DrawOptions _draw;
    private DrawOptions _baselineDraw;
    private ValidationResult? _validation;      // last accepted validation of _run
    private RunDefinition? _regenRun;           // definition _lastResult was computed from
    private RegenResult? _lastResult;           // last successful regen, ours or external
    private RegenResult? _failedAttempt;        // last failed regen; its row problems show until the next edit
    private bool _externalStale;
    private bool _isDirty;
    private AutoMode _lastAutoMode;
    private IReadOnlyList<AreaUnitOption> _areaUnits = [];
    private Guid? _selectedId;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private double _progress;
    private string? _statusMessage;
    private string? _intervalError;
    private bool _sameOnBothSides;
    private bool _sourcesChangedWhileBusy;
    private bool _disposed;

    /// <summary>Creates the view model and binds it to the host's active drawing.</summary>
    /// <param name="session">The engine door (frozen contract).</param>
    /// <param name="host">What the session does not carry: drawing identity and units, station text, tracker status, export.</param>
    /// <param name="pendingEdits">Session-lifetime store of edits not yet regenerated (E907).</param>
    /// <param name="config">Formats and defaults; <see cref="PresentationConfig.Default"/> when null.</param>
    /// <param name="runName">Run to edit; <see cref="PresentationConfig.DefaultRunName"/> when null.</param>
    public DialogViewModel(
        ISheetVolSession session,
        IDialogHost host,
        PendingEdits pendingEdits,
        PresentationConfig? config = null,
        string? runName = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pending = pendingEdits ?? throw new ArgumentNullException(nameof(pendingEdits));
        _config = config ?? PresentationConfig.Default;
        _runName = runName ?? _config.DefaultRunName;

        Presets =
        [
            new PresetOption(ReportPreset.Standard, DialogText.PresetStandard),
            new PresetOption(ReportPreset.HcfcdSegment, DialogText.PresetHcfcd),
        ];
        Grid = new SegmentsGridViewModel(this);
        Chart = new StripChartViewModel(id => Grid.SelectSegment(id));

        OffsetLeft = new TextFieldViewModel(DialogText.OffsetLeft, t => CommitOffset(left: true, t), RefreshCommandState);
        OffsetRight = new TextFieldViewModel(DialogText.OffsetRight, t => CommitOffset(left: false, t), RefreshCommandState);
        Ohwm = new TextFieldViewModel(DialogText.OhwmElevation, CommitOhwm, RefreshCommandState);
        IntervalEvery = new TextFieldViewModel(DialogText.IntervalEvery, CommitIntervalEvery);
        IntervalFrom = new TextFieldViewModel(DialogText.IntervalFrom, CommitIntervalBound);
        IntervalTo = new TextFieldViewModel(DialogText.IntervalTo, CommitIntervalBound);

        PickAlignmentCommand = new AsyncRelayCommand(PickAlignmentAsync, () => CanEdit && Alignments.Count > 0);
        AddAnalysisCommand = new RelayCommand(AddAnalysis, () => CanEdit);
        FillTableCommand = new RelayCommand(FillTable, () => CanEditStations && IntervalLength() is > 0);
        RegenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => CanRegenerate);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ExportCsvCommand = new RelayCommand(() => _host.RequestExport(_runName),
            () => IsDrawingOpen && !IsBusy && _lastResult is not null && !_isDirty);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);

        _session.SourcesChanged += OnSourcesChanged;
        Bind();
    }

    // ---- Events and sub view models ------------------------------------------------

    /// <summary>Raised by <see cref="Close"/>; the view closes the window.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The segments grid.</summary>
    public SegmentsGridViewModel Grid { get; }

    /// <summary>The strip chart above the grid.</summary>
    public StripChartViewModel Chart { get; }

    // ---- State -------------------------------------------------------------------

    /// <summary>The run as currently edited (last valid values).</summary>
    public RunDefinition Run => _run;

    /// <summary>The run name the dialog edits.</summary>
    public string RunName => _runName;

    /// <summary>Last accepted validation of <see cref="Run"/>; null when there is nothing to plan (no drawing or no alignment).</summary>
    public ValidationResult? Validation => _validation;

    /// <summary>The regen result the values come from; null before the first regen.</summary>
    public RegenResult? LastResult => _lastResult;

    /// <summary>The bound drawing, or null.</summary>
    public DrawingInfo? Drawing => _drawing;

    /// <summary>Unit system of the bound drawing (E914).</summary>
    public UnitSystem Units => _fmt.Units;

    /// <summary>False when no drawing is open (E901).</summary>
    public bool IsDrawingOpen => _drawing is not null;

    /// <summary>One-line reason the controls are disabled, or null (E901).</summary>
    public string? DisabledReason => _drawing is null ? DialogText.NoDrawingOpen : null;

    /// <summary>True when the drawing has no alignment or fewer than two TIN surfaces (E902).</summary>
    public bool IsEmptyState => _drawing is not null && (Alignments.Count == 0 || Surfaces.Count < 2);

    /// <summary>Names what is missing in the empty state (E902); null otherwise.</summary>
    public string? EmptyStateText
    {
        get
        {
            if (!IsEmptyState) return null;
            var parts = new List<string>();
            if (Alignments.Count == 0) parts.Add(DialogText.EmptyNoAlignment);
            if (Surfaces.Count < 2) parts.Add(string.Format(_fmt.Culture, DialogText.EmptyTooFewSurfacesFormat, Surfaces.Count));
            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <summary>True when the left panel and grid accept edits.</summary>
    public bool CanEdit => IsDrawingOpen && !IsBusy;

    /// <summary>True when the run differs from the drawing (definition or draw options).</summary>
    public bool IsDirty => _isDirty;

    /// <summary>True when the drawing does not reflect the dialog: edits pending or the tracker says stale.</summary>
    public bool IsStale => _isDirty || _externalStale;

    /// <summary>The one banner above the chart, or null (6.14).</summary>
    public string? StaleBannerText
    {
        get
        {
            if (_drawing is null || IsEmptyState || _validation is null) return null;
            if (_externalStale) return DialogText.StaleExternal;
            if (_isDirty) return DialogText.StaleEdits;
            if (_lastResult is null && _validation.Segments.Count > 0) return DialogText.StaleNoResult;
            return null;
        }
    }

    /// <summary>True when <see cref="StaleBannerText"/> is set.</summary>
    public bool ShowStaleBanner => StaleBannerText is not null;

    /// <summary>Run-level problems from validation and the last regen.</summary>
    public ObservableCollection<ProblemItem> RunProblems { get; } = [];

    /// <summary>Status line: regen outcome, pick failures, toolbar messages.</summary>
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    // ---- Alignment -------------------------------------------------------------------

    /// <summary>Alignment names in the drawing.</summary>
    public ObservableCollection<string> Alignments { get; } = [];

    /// <summary>The run's alignment, or null before one is picked.</summary>
    public string? SelectedAlignment
    {
        get => string.IsNullOrEmpty(_run.AlignmentName) ? null : _run.AlignmentName;
        set
        {
            var name = value ?? string.Empty;
            if (name != _run.AlignmentName) TryApply(_run with { AlignmentName = name });
            OnPropertiesChanged(nameof(SelectedAlignment), nameof(StationRangeText));
        }
    }

    /// <summary>Station range readout of the selected alignment.</summary>
    public string StationRangeText
    {
        get
        {
            if (_drawing is null || string.IsNullOrEmpty(_run.AlignmentName)) return string.Empty;
            return _host.StationRange(_run.AlignmentName) is (double start, double end)
                ? string.Format(_fmt.Culture, DialogText.StationRangeFormat, FormatStation(start), FormatStation(end))
                : string.Empty;
        }
    }

    /// <summary>Picks the alignment in the drawing.</summary>
    public AsyncRelayCommand PickAlignmentCommand { get; }

    // ---- Offsets -------------------------------------------------------------------

    /// <summary>Offsets card header with the length unit.</summary>
    public string OffsetsHeader => _fmt.Header(DialogText.OffsetsHeader, _fmt.LengthUnit);

    /// <summary>Left offset as a distance; positive is left of the alignment (stored as <c>OffsetLeft = −value</c>).</summary>
    public TextFieldViewModel OffsetLeft { get; }

    /// <summary>Right offset as a distance; positive is right of the alignment (stored as <c>OffsetRight = value</c>).</summary>
    public TextFieldViewModel OffsetRight { get; }

    /// <summary>When on, editing either offset sets both to the same distance.</summary>
    public bool SameOnBothSides
    {
        get => _sameOnBothSides;
        set
        {
            if (_sameOnBothSides == value) return;
            _sameOnBothSides = value;
            OnPropertyChanged();
            if (!value) return;
            var distance = -_run.OffsetLeft;
            if (_run.OffsetRight != distance && TryApply(_run with { OffsetRight = distance }) is null)
                OffsetRight.SetFromModel(_fmt.Offset(distance));
        }
    }

    // ---- Analyses -------------------------------------------------------------------

    /// <summary>TIN surface names in the drawing.</summary>
    public ObservableCollection<string> Surfaces { get; } = [];

    /// <summary>The comparisons being edited.</summary>
    public ObservableCollection<AnalysisEditorViewModel> Analyses { get; } = [];

    /// <summary>Adds another comparison.</summary>
    public RelayCommand AddAnalysisCommand { get; }

    /// <summary>Surfaces card header.</summary>
    public string SurfacesHeader => DialogText.SurfacesHeader;

    /// <summary>Analysis name label (HCFCD: table row).</summary>
    public string AnalysisNameLabel => IsHcfcd ? DialogText.AnalysisNameHcfcd : DialogText.AnalysisName;

    /// <summary>Base surface label (HCFCD: existing).</summary>
    public string BaseSurfaceLabel => IsHcfcd ? DialogText.BaseSurfaceHcfcd : DialogText.BaseSurface;

    /// <summary>Comparison surface label (HCFCD: design).</summary>
    public string ComparisonSurfaceLabel => IsHcfcd ? DialogText.ComparisonSurfaceHcfcd : DialogText.ComparisonSurface;

    // ---- Fill stations by interval ---------------------------------------------------

    /// <summary>Interval length.</summary>
    public TextFieldViewModel IntervalEvery { get; }

    /// <summary>Interval start station text; blank means START.</summary>
    public TextFieldViewModel IntervalFrom { get; }

    /// <summary>Interval end station text; blank means END.</summary>
    public TextFieldViewModel IntervalTo { get; }

    /// <summary>Error from the last Fill table, or null.</summary>
    public string? IntervalError { get => _intervalError; private set => SetProperty(ref _intervalError, value); }

    /// <summary>Replaces the break list with FROM..TO@EVERY.</summary>
    public RelayCommand FillTableCommand { get; }

    // ---- Draw options ---------------------------------------------------------------

    /// <summary>Draw boundary polylines.</summary>
    public bool DrawBoundaryPolylines { get => _draw.BoundaryPolylines; set => SetDraw(_draw with { BoundaryPolylines = value }); }

    /// <summary>Draw a table at each segment.</summary>
    public bool DrawTableAtEachSegment { get => _draw.TableAtEachSegment; set => SetDraw(_draw with { TableAtEachSegment = value }); }

    /// <summary>Draw the summary table.</summary>
    public bool DrawSummaryTable { get => _draw.SummaryTable; set => SetDraw(_draw with { SummaryTable = value }); }

    /// <summary>The draw options as edited.</summary>
    public DrawOptions Draw => _draw;

    // ---- Report preset, OHWM, area unit ----------------------------------------------

    /// <summary>Report presets (6.16).</summary>
    public IReadOnlyList<PresetOption> Presets { get; }

    /// <summary>The run's preset. Switching sets the preset's default area unit (6.16).</summary>
    public PresetOption SelectedPreset
    {
        get => Presets.First(p => p.Preset == _run.Preset);
        set
        {
            if (value is not null && value.Preset != _run.Preset)
                TryApply(_run with { Preset = value.Preset, AreaUnit = DisplayFormatter.DefaultAreaUnit(_fmt.Units, value.Preset) });
            OnPropertyChanged();
        }
    }

    /// <summary>True for the HCFCD preset: report selector, OHWM field, HCFCD columns and chart.</summary>
    public bool IsHcfcd => _run.Preset == ReportPreset.HcfcdSegment;

    /// <summary>OHWM field label with the length unit.</summary>
    public string OhwmLabel => _fmt.Header(DialogText.OhwmElevation, _fmt.LengthUnit);

    /// <summary>Run OHWM elevation (HCFCD). Required before Regenerate with that preset.</summary>
    public TextFieldViewModel Ohwm { get; }

    /// <summary>Area units offered for this drawing: SF and AC, or m² and ha (E917).</summary>
    public IReadOnlyList<AreaUnitOption> AreaUnits => _areaUnits;

    /// <summary>The run's area unit. Display only: no recompute, raw area unchanged, saved with the run (E917, E418).</summary>
    public AreaUnitOption? SelectedAreaUnit
    {
        get => _areaUnits.FirstOrDefault(o => o.Unit == _run.AreaUnit);
        set
        {
            if (value is not null && value.Unit != _run.AreaUnit && _areaUnits.Any(o => o.Unit == value.Unit))
                TryApply(_run with { AreaUnit = value.Unit });
            OnPropertyChanged();
        }
    }

    /// <summary>Length unit label (ft or m).</summary>
    public string LengthUnitLabel => _fmt.LengthUnit;

    /// <summary>Volume unit label (CY or m³).</summary>
    public string VolumeUnitLabel => _fmt.VolumeUnit;

    // ---- Footer ---------------------------------------------------------------------

    /// <summary>
    /// "Regenerate when the alignment or stations change". Checked means
    /// <see cref="RunDefinition.AutoMode"/> is not Off. Unchecking sets Off; checking
    /// restores the run's earlier mode, or <see cref="PresentationConfig.AutoModeWhenChecked"/>.
    /// </summary>
    public bool RegenerateOnChange
    {
        get => _run.AutoMode != AutoMode.Off;
        set
        {
            if (value != RegenerateOnChange)
            {
                if (!value) _lastAutoMode = _run.AutoMode;
                TryApply(_run with { AutoMode = value ? _lastAutoMode : AutoMode.Off });
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Asks the host to export the run's CSV. Enabled after a regen when nothing is pending.</summary>
    public RelayCommand ExportCsvCommand { get; }

    /// <summary>Closes the dialog; pending edits are kept for the drawing (E907).</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>Regenerates the run through the session.</summary>
    public AsyncRelayCommand RegenerateCommand { get; }

    /// <summary>Cancels a running regen (E906).</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>True while a regen runs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanEdit));
        }
    }

    /// <summary>Regen progress, 0..1.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, Math.Clamp(value, 0, 1)); }

    /// <summary>Why Regenerate is disabled, in one line; null when it is enabled or a regen runs.</summary>
    public string? RegenerateBlockedReason
    {
        get
        {
            if (_drawing is null) return DialogText.NoDrawingOpen;
            if (IsEmptyState) return EmptyStateText;
            if (_host.IsCommandActive) return DialogText.BlockedCommandActive;
            if (string.IsNullOrEmpty(_run.AlignmentName)) return DialogText.BlockedNoAlignment;
            if (_run.Analyses.Count == 0) return DialogText.BlockedNoAnalysis;
            if (_run.Analyses.Any(a => string.IsNullOrEmpty(a.BaseSurface) || string.IsNullOrEmpty(a.ComparisonSurface)))
                return DialogText.BlockedSurfaces;
            if (IsHcfcd && _run.Ohwm is null) return DialogText.BlockedOhwm;
            if (HasFieldError) return DialogText.BlockedFieldErrors;
            if (_validation is null || !_validation.IsValid) return DialogText.BlockedValidation;
            return null;
        }
    }

    /// <summary>True when Regenerate is enabled.</summary>
    public bool CanRegenerate => !IsBusy && RegenerateBlockedReason is null;

    // ---- Public operations -------------------------------------------------------------

    /// <summary>Re-queries command state; the host calls it when an editor command starts or ends (E905).</summary>
    public void RefreshCommandState()
    {
        OnPropertiesChanged(nameof(RegenerateBlockedReason), nameof(CanRegenerate));
        RefreshCommands();
    }

    /// <summary>Keeps pending edits for the drawing, clears the highlight (E911), unsubscribes and raises <see cref="CloseRequested"/>.</summary>
    public void Close()
    {
        if (_disposed) return;
        SavePending();
        if (IsDrawingOpen) _session.Highlight(null);
        Dispose();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unsubscribes from the session and cancels a running regen. Does not save pending edits; <see cref="Close"/> does.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.SourcesChanged -= OnSourcesChanged;
        _cts?.Cancel();
    }

    // ---- Binding and refresh -------------------------------------------------------------

    [MemberNotNull(nameof(_fmt), nameof(_run), nameof(_baseline), nameof(_draw), nameof(_baselineDraw))]
    private void Bind()
    {
        _drawing = _host.ActiveDrawing;
        var units = _drawing?.Units ?? UnitSystem.Imperial;
        _fmt = new DisplayFormatter(_config, units);
        _areaUnits = DisplayFormatter.OfferedAreaUnits(units)
            .Select(u => new AreaUnitOption(u, DisplayFormatter.AreaUnitLabel(u)))
            .ToList();
        _validation = null;
        _regenRun = null;
        _lastResult = null;
        _failedAttempt = null;
        _externalStale = false;
        _selectedId = null;
        _baselineDraw = _config.DefaultDrawOptions;
        _draw = _baselineDraw;
        RefreshSourceLists();

        var stored = _drawing is null ? null : _session.LoadRun(_runName);
        _baseline = stored ?? NewRun(units);
        _run = _baseline;
        if (_drawing is not null && _pending.TryGet(_drawing.Name, out var pending) && pending.Run.Name == _runName)
        {
            _run = pending.Run;
            _draw = pending.Draw;
        }
        _run = FitAreaUnitToDrawing(_run, units);
        _lastAutoMode = _run.AutoMode != AutoMode.Off ? _run.AutoMode : _config.AutoModeWhenChecked;
        if (_drawing is not null) AdoptStatus(_host.GetRunStatus(_runName), stored);

        LoadFieldsFromRun();
        Revalidate();
        Refresh();
    }

    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (IsBusy)
        {
            _sourcesChangedWhileBusy = true;
            return;
        }

        var drawing = _host.ActiveDrawing;
        if (!Equals(drawing, _drawing))
        {
            SavePending();
            Bind();
            return;
        }
        if (drawing is null)
        {
            Refresh();
            return;
        }

        RefreshSourceLists();
        var stored = _session.LoadRun(_runName);
        if (stored is not null)
        {
            if (!_isDirty)
            {
                _run = FitAreaUnitToDrawing(stored, drawing.Units);
                LoadFieldsFromRun();
            }
            _baseline = stored;
        }
        AdoptStatus(_host.GetRunStatus(_runName), stored);
        Revalidate();
        Refresh();
    }

    private void AdoptStatus(RunStatus status, RunDefinition? stored)
    {
        _externalStale = status.IsStale;
        if (status.LastResult is { Succeeded: true } result
            && result.RunName == _runName
            && !ReferenceEquals(result, _lastResult))
        {
            _lastResult = result;
            _regenRun = stored ?? _baseline;
            _failedAttempt = null;
        }
    }

    private void Revalidate()
    {
        _validation = ValidateRun(_run);
        _run = WithKnownSegments(_run, _validation);
    }

    private ValidationResult? ValidateRun(RunDefinition run)
        => _drawing is null || string.IsNullOrEmpty(run.AlignmentName) ? null : _session.Validate(run);

    private static RunDefinition WithKnownSegments(RunDefinition run, ValidationResult? result)
        => result is { Segments.Count: > 0 } ? run with { KnownSegments = result.Segments } : run;

    /// <summary>
    /// Validates a candidate run and makes it current. With <paramref name="rejectNewRunLevelErrors"/>
    /// a candidate that brings a run-level error the current run does not have (a station
    /// the session cannot parse, a break outside the alignment) is refused: the current run
    /// and validation stay, and the messages come back for the field that caused them (E903).
    /// </summary>
    private string? TryApply(RunDefinition candidate, bool rejectNewRunLevelErrors = false)
    {
        if (IsBusy) return DialogText.BusyRefused;
        var result = ValidateRun(candidate);
        if (rejectNewRunLevelErrors && result is not null)
        {
            var before = RunLevelErrors(_validation).Select(p => (p.Code, p.Message)).ToHashSet();
            var fresh = RunLevelErrors(result).Where(p => !before.Contains((p.Code, p.Message))).ToList();
            if (fresh.Count > 0) return string.Join(Environment.NewLine, fresh.Select(p => p.Message));
        }
        _run = WithKnownSegments(candidate, result);
        _validation = result;
        _failedAttempt = null;
        Refresh();
        return null;
    }

    private static IEnumerable<Problem> RunLevelErrors(ValidationResult? result)
        => result?.Problems.Where(p => p.SegmentId is null && p.Severity == Severity.Error) ?? [];

    private void Refresh()
    {
        _isDirty = _drawing is not null && (!RunComparison.SameDefinition(_run, _baseline) || _draw != _baselineDraw);
        Grid.SyncComparisons(_run.Analyses);
        var rows = BuildRowModels();
        var ctx = new GridContext(_fmt, FormatStation, _run.Preset, _run.AreaUnit, _lastResult, _selectedId, IsBusy);
        Grid.Update(rows, ctx);
        Chart.Update(rows, ctx);
        if (_selectedId is Guid selected && !rows.Any(r => r.Segment.Id == selected))
        {
            _selectedId = null;
            if (IsDrawingOpen) _session.Highlight(null);
        }
        SyncRunProblems();

        OnPropertiesChanged(
            nameof(Run), nameof(Validation), nameof(LastResult), nameof(Drawing), nameof(Units),
            nameof(IsDrawingOpen), nameof(DisabledReason), nameof(IsEmptyState), nameof(EmptyStateText), nameof(CanEdit),
            nameof(IsDirty), nameof(IsStale), nameof(StaleBannerText), nameof(ShowStaleBanner),
            nameof(SelectedAlignment), nameof(StationRangeText), nameof(OffsetsHeader), nameof(SameOnBothSides),
            nameof(AnalysisNameLabel), nameof(BaseSurfaceLabel), nameof(ComparisonSurfaceLabel),
            nameof(DrawBoundaryPolylines), nameof(DrawTableAtEachSegment), nameof(DrawSummaryTable), nameof(Draw),
            nameof(SelectedPreset), nameof(IsHcfcd), nameof(OhwmLabel), nameof(AreaUnits), nameof(SelectedAreaUnit),
            nameof(LengthUnitLabel), nameof(VolumeUnitLabel), nameof(RegenerateOnChange),
            nameof(RegenerateBlockedReason), nameof(CanRegenerate));
        RefreshCommands();
    }

    private List<RowModel> BuildRowModels()
    {
        var rows = new List<RowModel>();
        if (_validation is null) return rows;
        var manual = _run.ManualSegments.Select(m => m.Id).ToHashSet();
        var analysisId = Grid.SelectedAnalysisId;
        var stale = IsStale;
        var number = 0;
        foreach (var segment in _validation.Segments)
        {
            number++;
            var problems = _validation.Problems.Where(p => p.SegmentId == segment.Id).ToList();
            if (_failedAttempt is not null)
            {
                problems.AddRange(_failedAttempt.Problems.Where(p => p.SegmentId == segment.Id));
                problems.AddRange(_failedAttempt.Segments.Where(r => r.SegmentId == segment.Id).SelectMany(r => r.Problems));
            }
            var worst = MostSevere(problems);
            var values = ApplicableResult(segment, analysisId);
            SegmentStatus status;
            if (worst?.Severity == Severity.Error)
            {
                status = SegmentStatus.Error;
                values = null;
            }
            else if (values is null)
            {
                status = SegmentStatus.OutOfDate;
            }
            else if (values.Status == SegmentStatus.Error)
            {
                status = SegmentStatus.Error;
                worst ??= MostSevere(values.Problems);
                values = null;
            }
            else
            {
                status = stale ? SegmentStatus.OutOfDate : values.Status;
                worst ??= MostSevere(values.Problems);
            }

            var sheet = _run.Overrides.TryGetValue(segment.Id, out var overrides) && overrides.TargetLayout is not null
                ? overrides.TargetLayout
                : segment.TargetLayout;
            rows.Add(new RowModel(
                segment, number,
                StationsEditable: CanEditStations && !manual.Contains(segment.Id),
                values, status, worst, sheet,
                RunComparison.EffectiveOhwm(_run, segment.Id),
                _run.Ohwm?.BySegment.ContainsKey(segment.Id) == true));
        }
        return rows;
    }

    private SegmentResult? ApplicableResult(SegmentDef segment, Guid? analysisId)
    {
        if (_lastResult is null || analysisId is not Guid id) return null;
        var result = _lastResult.Segments.FirstOrDefault(r => r.SegmentId == segment.Id && r.AnalysisId == id);
        if (result is null) return null;
        var tolerance = _config.StationTolerance;
        if (Math.Abs(result.StartStation - segment.StartStation) > tolerance
            || Math.Abs(result.EndStation - segment.EndStation) > tolerance) return null;
        if (_regenRun is not null && !RunComparison.VolumeInputsEqual(_regenRun, _run, id, segment.Id)) return null;
        if (_run.Preset == ReportPreset.HcfcdSegment && result.Impact is null) return null;
        return result;
    }

    private static Problem? MostSevere(IEnumerable<Problem> problems)
        => problems.OrderByDescending(p => p.Severity).FirstOrDefault();

    private void SyncRunProblems()
    {
        var ids = _validation?.Segments.Select(s => s.Id).ToHashSet() ?? [];
        bool RunLevel(Problem p) => p.SegmentId is not Guid id || !ids.Contains(id);
        var problems = (_validation?.Problems ?? []).Where(RunLevel);
        if ((_failedAttempt ?? _lastResult) is { } regen) problems = problems.Concat(regen.Problems.Where(RunLevel));
        var items = problems.Select(p => new ProblemItem(p.Code, p.Severity, p.Message, p.Fix)).ToList();
        if (items.SequenceEqual(RunProblems)) return;
        RunProblems.Clear();
        foreach (var item in items) RunProblems.Add(item);
    }

    private void RefreshCommands()
    {
        PickAlignmentCommand.RaiseCanExecuteChanged();
        AddAnalysisCommand.RaiseCanExecuteChanged();
        FillTableCommand.RaiseCanExecuteChanged();
        RegenerateCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportCsvCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        foreach (var editor in Analyses) editor.RefreshCommands();
        Grid.RefreshCommands();
    }

    private void LoadFieldsFromRun()
    {
        OffsetLeft.SetFromModel(_fmt.Offset(-_run.OffsetLeft));
        OffsetRight.SetFromModel(_fmt.Offset(_run.OffsetRight));
        _sameOnBothSides = Math.Abs(_run.OffsetLeft + _run.OffsetRight) <= _config.StationTolerance;
        Ohwm.SetFromModel(_run.Ohwm is { } ohwm ? _fmt.Elevation(ohwm.DefaultElevation) : string.Empty);
        IntervalError = null;
        Analyses.Clear();
        foreach (var analysis in _run.Analyses) Analyses.Add(CreateEditor(analysis, IsAutoName(analysis)));
    }

    private void RefreshSourceLists()
    {
        SyncInPlace(Alignments, _drawing is null ? [] : _session.AlignmentNames);
        SyncInPlace(Surfaces, _drawing is null ? [] : _session.TinSurfaceNames);
    }

    /// <summary>Updates a bound list without clearing it, so a combo box keeps a selection that still exists.</summary>
    private static void SyncInPlace(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!source.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count && target[i] == source[i]) continue;
            var existing = target.IndexOf(source[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, source[i]);
        }
    }

    private void SavePending()
    {
        if (_drawing is null) return;
        if (_isDirty) _pending.Save(_drawing.Name, new PendingEdit(_run, _draw));
        else _pending.Clear(_drawing.Name);
    }

    private RunDefinition NewRun(UnitSystem units)
    {
        var offset = units == UnitSystem.Metric ? _config.DefaultOffsetMetric : _config.DefaultOffsetImperial;
        return new RunDefinition(
            _runName,
            Alignments.Count == 1 ? Alignments[0] : string.Empty,
            SegmentDriver.Stations,
            units == UnitSystem.Metric ? _config.DefaultBreakSpecMetric : _config.DefaultBreakSpecImperial,
            null,
            -offset, offset,
            [new AnalysisDef(Guid.NewGuid(), NewAnalysisName(1), string.Empty, string.Empty)],
            [],
            [],
            new Dictionary<Guid, SegmentOverrides>(),
            new ViewOptions(),
            ReportPreset.Standard,
            DisplayFormatter.DefaultAreaUnit(units, ReportPreset.Standard));
    }

    /// <summary>A stored unit from the other system is mapped to its counterpart (SF and m², AC and ha) so the dropdown always shows a choice the drawing offers (E917).</summary>
    private static RunDefinition FitAreaUnitToDrawing(RunDefinition run, UnitSystem units)
        => DisplayFormatter.OfferedAreaUnits(units).Contains(run.AreaUnit)
            ? run
            : run with { AreaUnit = DisplayFormatter.Counterpart(run.AreaUnit) };

    private string FormatStation(double raw)
        => _drawing is null || string.IsNullOrEmpty(_run.AlignmentName)
            ? _fmt.SpecNumber(raw)
            : _host.FormatStation(_run.AlignmentName, raw);

    private bool HasFieldError
        => OffsetLeft.HasError || OffsetRight.HasError || (IsHcfcd && Ohwm.HasError)
           || Analyses.Any(a => a.HasFieldError) || Grid.HasFieldError;

    // ---- Edits from the left panel -------------------------------------------------------

    private string? CommitOffset(bool left, string text)
    {
        if (!_fmt.TryParse(text, out var distance)) return DialogText.NotANumber;
        double l, r;
        if (_sameOnBothSides) (l, r) = (-distance, distance);
        else if (left) (l, r) = (-distance, _run.OffsetRight);
        else (l, r) = (_run.OffsetLeft, distance);
        var error = TryApply(_run with { OffsetLeft = l, OffsetRight = r });
        if (error is not null) return error;
        OffsetLeft.SetFromModel(_fmt.Offset(-l));
        OffsetRight.SetFromModel(_fmt.Offset(r));
        return null;
    }

    private string? CommitOhwm(string text)
    {
        if (!_fmt.TryParse(text, out var elevation)) return DialogText.NotANumber;
        var bySegment = _run.Ohwm?.BySegment ?? new Dictionary<Guid, double>();
        return TryApply(_run with { Ohwm = new OhwmSpec(elevation, bySegment) });
    }

    private string? CommitIntervalEvery(string text)
    {
        FillTableCommand.RaiseCanExecuteChanged();
        IntervalError = null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!_fmt.TryParse(text, out var every)) return DialogText.NotANumber;
        return every > 0 ? null : DialogText.IntervalMustBePositive;
    }

    private string? CommitIntervalBound(string text)
    {
        IntervalError = null;
        return text.Contains(BreakList.SeparatorChar) ? DialogText.StationHasComma : null;
    }

    private double? IntervalLength() => _fmt.TryParse(IntervalEvery.Text, out var every) ? every : null;

    private void FillTable()
    {
        if (IntervalLength() is not double every || every <= 0)
        {
            IntervalError = DialogText.IntervalMustBePositive;
            return;
        }
        var from = string.IsNullOrWhiteSpace(IntervalFrom.Text) ? BreakList.StartToken : IntervalFrom.Text.Trim();
        var to = string.IsNullOrWhiteSpace(IntervalTo.Text) ? BreakList.EndToken : IntervalTo.Text.Trim();
        if (from.Contains(BreakList.SeparatorChar) || to.Contains(BreakList.SeparatorChar))
        {
            IntervalError = DialogText.StationHasComma;
            return;
        }
        var spec = BreakList.IntervalSpec(from, to, _fmt.SpecNumber(every));
        IntervalError = TryApply(_run with { BreakSpec = spec }, rejectNewRunLevelErrors: true);
    }

    private void SetDraw(DrawOptions draw)
    {
        if (draw == _draw || IsBusy) return;
        _draw = draw;
        Refresh();
    }

    private AnalysisEditorViewModel CreateEditor(AnalysisDef definition, bool nameIsAuto)
        => new(definition, Surfaces, _fmt, OnAnalysisEdited, RemoveAnalysis, () => CanEdit && Analyses.Count > 1, nameIsAuto, RefreshCommandState);

    private bool IsAutoName(AnalysisDef a)
        => string.IsNullOrEmpty(a.Name)
           || string.IsNullOrEmpty(a.BaseSurface)
           || string.IsNullOrEmpty(a.ComparisonSurface)
           || a.Name == string.Format(_fmt.Culture, _config.AnalysisNameFormat, a.BaseSurface, a.ComparisonSurface);

    private string NewAnalysisName(int position) => string.Format(_config.EffectiveCulture, _config.NewAnalysisNameFormat, position);

    private void OnAnalysisEdited(AnalysisEditorViewModel editor)
        => TryApply(_run with { Analyses = Analyses.Select(e => e.Definition).ToList() });

    private void AddAnalysis()
    {
        var definition = new AnalysisDef(Guid.NewGuid(), NewAnalysisName(Analyses.Count + 1), string.Empty, string.Empty);
        Analyses.Add(CreateEditor(definition, nameIsAuto: true));
        OnAnalysisEdited(Analyses[^1]);
    }

    private void RemoveAnalysis(AnalysisEditorViewModel editor)
    {
        if (Analyses.Count <= 1 || !Analyses.Remove(editor)) return;
        TryApply(_run with { Analyses = Analyses.Select(e => e.Definition).ToList() });
    }

    private async Task PickAlignmentAsync()
    {
        string? name;
        try
        {
            name = await _session.PickAlignmentAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_fmt.Culture, DialogText.PickFailedFormat, ex.Message);
            return;
        }
        if (name is null || _disposed) return;
        RefreshSourceLists();
        SelectedAlignment = name;
    }

    // ---- Regenerate -------------------------------------------------------------------------

    private async Task RegenerateAsync()
    {
        if (!CanRegenerate) return;
        var run = _run;
        var draw = _draw;
        // Not disposed: Cancel can run this method's continuation synchronously inside
        // its own callback, and a source with no timer or linked token holds nothing
        // that needs disposal.
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusMessage = null;
        Progress = 0;
        IsBusy = true;
        Refresh();
        try
        {
            var result = await _session.RegenerateAsync(run, new InlineProgress(p => Progress = p), cts.Token);
            if (result.Succeeded)
            {
                _lastResult = result;
                _regenRun = run;
                _baseline = run;
                _baselineDraw = draw;
                _externalStale = false;
                _failedAttempt = null;
                StatusMessage = string.Format(_fmt.Culture, DialogText.RegenSucceededFormat,
                    _config.Seconds.Format(result.Elapsed.TotalSeconds, _fmt.Culture));
            }
            else
            {
                _failedAttempt = result;
                StatusMessage = DialogText.RegenFailed;
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = DialogText.RegenCancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_fmt.Culture, DialogText.RegenErrorFormat, ex.Message);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }

        if (_disposed) return;
        Refresh();
        if (_drawing is not null && !_isDirty) _pending.Clear(_drawing.Name);
        if (_sourcesChangedWhileBusy)
        {
            _sourcesChangedWhileBusy = false;
            OnSourcesChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>Reports progress on the caller's thread; Progress&lt;T&gt; would post through a synchronization context.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // ---- Grid owner (toolbar and cell edits) ---------------------------------------------------

    private bool CanEditStations => IsDrawingOpen && !IsBusy && _run.Driver == SegmentDriver.Stations;

    private IEnumerable<SegmentDef> StationSegments()
    {
        if (_validation is null) return [];
        var manual = _run.ManualSegments.Select(m => m.Id).ToHashSet();
        return _validation.Segments.Where(s => !manual.Contains(s.Id));
    }

    private BreakList CurrentBreakList()
        => BreakList.From(StationSegments(), _run.BreakSpec, _fmt.SpecNumber, _config.StationTolerance);

    string? IGridOwner.EditStation(Guid segmentId, bool isEnd, string text)
    {
        if (IsBusy) return DialogText.BusyRefused;
        if (!CanEditStations) return DialogText.StationsNotEditable;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return DialogText.StationRequired;
        if (trimmed.Contains(BreakList.SeparatorChar)) return DialogText.StationHasComma;
        var list = CurrentBreakList();
        if (!list.TryGetRow(segmentId, out var begin, out var end)) return DialogText.StationsNotEditable;
        list.SetText(isEnd ? end : begin, trimmed);
        return TryApply(_run with { BreakSpec = list.ToSpec() }, rejectNewRunLevelErrors: true);
    }

    string? IGridOwner.EditOhwm(Guid segmentId, string text)
    {
        if (IsBusy) return DialogText.BusyRefused;
        if (_run.Ohwm is null) return DialogText.OhwmDefaultFirst;
        var bySegment = new Dictionary<Guid, double>(_run.Ohwm.BySegment);
        if (string.IsNullOrWhiteSpace(text))
        {
            bySegment.Remove(segmentId);
        }
        else
        {
            if (!_fmt.TryParse(text, out var elevation)) return DialogText.NotANumber;
            if (Math.Abs(elevation - _run.Ohwm.DefaultElevation) <= _config.StationTolerance) bySegment.Remove(segmentId);
            else bySegment[segmentId] = elevation;
        }
        return TryApply(_run with { Ohwm = _run.Ohwm with { BySegment = bySegment } });
    }

    void IGridOwner.AddStation(Guid segmentId)
    {
        var segment = StationSegments().FirstOrDefault(s => s.Id == segmentId);
        if (segment is null || !CanEditStations) return;
        var list = CurrentBreakList();
        StatusMessage = list.Insert((segment.StartStation + segment.EndStation) / 2)
                        ?? TryApply(_run with { BreakSpec = list.ToSpec() }, rejectNewRunLevelErrors: true);
    }

    async Task IGridOwner.PickStationAsync()
    {
        double? station;
        try
        {
            station = await _session.PickStationAsync(_run.AlignmentName);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_fmt.Culture, DialogText.PickFailedFormat, ex.Message);
            return;
        }
        if (station is not double raw || _disposed || !CanEditStations) return;
        var list = CurrentBreakList();
        StatusMessage = list.Insert(raw) ?? TryApply(_run with { BreakSpec = list.ToSpec() }, rejectNewRunLevelErrors: true);
    }

    void IGridOwner.RemoveSegment(Guid segmentId)
    {
        if (!((IGridOwner)this).CanRemove(segmentId)) return;
        var list = CurrentBreakList();
        list.RemoveRow(segmentId);
        StatusMessage = TryApply(_run with { BreakSpec = list.ToSpec() }, rejectNewRunLevelErrors: true);
    }

    void IGridOwner.ZoomTo(Guid segmentId)
    {
        if (IsDrawingOpen) _session.ZoomTo(segmentId);
    }

    void IGridOwner.SelectionChanged(Guid? segmentId)
    {
        _selectedId = segmentId;
        if (IsDrawingOpen) _session.Highlight(segmentId);
        Chart.SetSelected(segmentId);
    }

    void IGridOwner.ComparisonChanged() => Refresh();

    void IGridOwner.FieldErrorChanged() => RefreshCommandState();

    bool IGridOwner.CanEditStations => CanEditStations;

    bool IGridOwner.CanPickStation => CanEditStations && !string.IsNullOrEmpty(_run.AlignmentName);

    bool IGridOwner.CanRemove(Guid segmentId)
    {
        if (!CanEditStations) return false;
        var segments = StationSegments().ToList();
        return segments.Count > 1 && segments.Any(s => s.Id == segmentId);
    }

    bool IGridOwner.CanZoom => IsDrawingOpen;
}
