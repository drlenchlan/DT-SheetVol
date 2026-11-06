using System.Collections.ObjectModel;

namespace SheetVol.Core.Presentation;

/// <summary>
/// One comparison in the "Surfaces to compare" card (6.5, 6.14): name, and its source. The
/// source is either a base/comparison pair with cut and fill factors, or (rev 3, X6, E918) an
/// existing user TIN volume surface whose own pair and factors are shown read-only and govern
/// (NOW.md rev 3 rule 1: the stored factors are ignored, never rewritten). Every change goes
/// back to the dialog, which re-runs Validate (live validation) and never regenerates.
/// </summary>
public sealed class AnalysisEditorViewModel : ObservableObject
{
    private readonly DisplayFormatter _fmt;
    private readonly Action<AnalysisEditorViewModel> _changed;
    private readonly Func<bool> _canRemove;
    private AnalysisDef _def;
    private bool _nameIsAuto;
    private bool _isVolumeSource;
    private string _pairBase;                   // the editable pair's last values, restored on switching back (E918)
    private string _pairComparison;
    private string? _lastVolumeSurface;         // offered again when the switch goes back to Volume surface

    internal AnalysisEditorViewModel(
        AnalysisDef definition,
        ObservableCollection<string> surfaces,
        ObservableCollection<VolumeSurfaceOption> volumeSurfaces,
        DisplayFormatter formatter,
        Action<AnalysisEditorViewModel> changed,
        Action<AnalysisEditorViewModel> remove,
        Func<bool> canRemove,
        bool nameIsAuto,
        bool volumeSourcePending,
        Action fieldErrorChanged)
    {
        _def = definition;
        _fmt = formatter;
        _changed = changed;
        _canRemove = canRemove;
        _nameIsAuto = nameIsAuto;
        _isVolumeSource = definition.VolumeSurface is not null || volumeSourcePending;
        _pairBase = definition.BaseSurface;
        _pairComparison = definition.ComparisonSurface;
        _lastVolumeSurface = definition.VolumeSurface;
        Surfaces = surfaces;
        VolumeSurfaces = volumeSurfaces;
        Name = new TextFieldViewModel(DialogText.AnalysisName, CommitName, fieldErrorChanged);
        CutFactor = new TextFieldViewModel(DialogText.CutFactor, t => CommitFactor(t, cut: true), fieldErrorChanged);
        FillFactor = new TextFieldViewModel(DialogText.FillFactor, t => CommitFactor(t, cut: false), fieldErrorChanged);
        RemoveCommand = new RelayCommand(() => remove(this), () => _canRemove());
        Name.SetFromModel(definition.Name);
        CutFactor.SetFromModel(_fmt.Factor(definition.CutFactor));
        FillFactor.SetFromModel(_fmt.Factor(definition.FillFactor));
    }

    /// <summary>The analysis as currently edited (last valid values).</summary>
    public AnalysisDef Definition => _def;

    /// <summary>Analysis GUID.</summary>
    public Guid Id => _def.Id;

    /// <summary>TIN surface names of the drawing, shared with the dialog.</summary>
    public ObservableCollection<string> Surfaces { get; }

    /// <summary>User TIN volume surfaces of the drawing (rev 3, X6), shared with the dialog; re-read on SourcesChanged.</summary>
    public ObservableCollection<VolumeSurfaceOption> VolumeSurfaces { get; }

    /// <summary>Analysis name (HCFCD: the material row label, 6.16).</summary>
    public TextFieldViewModel Name { get; }

    /// <summary>Cut factor of the pair source, greater than zero. Hidden while the source is a volume surface.</summary>
    public TextFieldViewModel CutFactor { get; }

    /// <summary>Fill factor of the pair source, greater than zero. Hidden while the source is a volume surface.</summary>
    public TextFieldViewModel FillFactor { get; }

    /// <summary>Removes this comparison. Disabled when it is the only one.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Base surface of the pair source (HCFCD: OHWM). Null or empty until picked.</summary>
    public string? BaseSurface
    {
        get => NullIfEmpty(_isVolumeSource ? _pairBase : _def.BaseSurface);
        set
        {
            if (!_isVolumeSource) SetSurfaces(value ?? string.Empty, _def.ComparisonSurface);
        }
    }

    /// <summary>Comparison surface of the pair source (HCFCD: proposed). Null or empty until picked.</summary>
    public string? ComparisonSurface
    {
        get => NullIfEmpty(_isVolumeSource ? _pairComparison : _def.ComparisonSurface);
        set
        {
            if (!_isVolumeSource) SetSurfaces(_def.BaseSurface, value ?? string.Empty);
        }
    }

    // ---- Source switch (rev 3, X6, E918) ---------------------------------------------------

    /// <summary>Source switch on "Base and comparison" (the default). Setting it true switches back and restores the pair's last values.</summary>
    public bool IsPairSource
    {
        get => !_isVolumeSource;
        set => SetSource(volume: !value);
    }

    /// <summary>Source switch on "Volume surface". The analysis is complete once a volume surface is chosen.</summary>
    public bool IsVolumeSurfaceSource
    {
        get => _isVolumeSource;
        set => SetSource(volume: value);
    }

    /// <summary>
    /// Name of the chosen volume surface (<see cref="AnalysisDef.VolumeSurface"/>), or null while none is
    /// chosen. Choosing one switches the source to it. A name that is not listed or is listed disabled is
    /// refused, and so is null: the dropdown offers no "none", so a null from the view is list churn,
    /// not a choice. Switching back to the pair clears it.
    /// </summary>
    public string? VolumeSurface
    {
        get => _def.VolumeSurface;
        set
        {
            if (string.IsNullOrEmpty(value) || !ChooseVolumeSurface(value)) OnPropertyChanged();
        }
    }

    /// <summary>Base of the chosen volume surface, read-only (as last resolved when it is not listed); empty while none is chosen.</summary>
    public string VolumeSurfaceBaseText
        => _def.VolumeSurface is null ? string.Empty : ChosenInfo?.BaseSurface ?? _def.BaseSurface;

    /// <summary>Comparison of the chosen volume surface, read-only; empty while none is chosen.</summary>
    public string VolumeSurfaceComparisonText
        => _def.VolumeSurface is null ? string.Empty : ChosenInfo?.ComparisonSurface ?? _def.ComparisonSurface;

    /// <summary>The volume surface's own cut factor, read-only (rule 1: this one governs); empty when unknown.</summary>
    public string VolumeSurfaceCutFactorText
        => _def.VolumeSurface is not null && ChosenInfo is { } info ? _fmt.Factor(info.CutFactor) : string.Empty;

    /// <summary>The volume surface's own fill factor, read-only; empty when unknown.</summary>
    public string VolumeSurfaceFillFactorText
        => _def.VolumeSurface is not null && ChosenInfo is { } info ? _fmt.Factor(info.FillFactor) : string.Empty;

    /// <summary>Why the chosen volume surface cannot be used (not listed any more, or its sources erased); null when it can or none is chosen.</summary>
    public string? VolumeSurfaceProblem
    {
        get
        {
            if (!_isVolumeSource || _def.VolumeSurface is not string name) return null;
            var option = Find(name);
            if (option is null) return string.Format(_fmt.Culture, DialogText.VolumeSurfaceNotFoundFormat, name);
            return option.IsSelectable ? null : string.Format(_fmt.Culture, DialogText.VolumeSurfaceUnusableFormat, name, option.DisabledReason);
        }
    }

    /// <summary>True while the switch is on Volume surface and none is chosen: the analysis is incomplete.</summary>
    internal bool NeedsVolumeSurface => _isVolumeSource && _def.VolumeSurface is null;

    /// <summary>True when the pair source lacks a base or a comparison surface.</summary>
    internal bool NeedsPairSurfaces
        => !_isVolumeSource && (string.IsNullOrEmpty(_def.BaseSurface) || string.IsNullOrEmpty(_def.ComparisonSurface));

    internal void RefreshCommands() => RemoveCommand.RaiseCanExecuteChanged();

    internal bool HasFieldError => Name.HasError || CutFactor.HasError || FillFactor.HasError;

    /// <summary>The dialog re-read <see cref="VolumeSurfaces"/>: the read-only values and the problem follow the new list.</summary>
    internal void SourcesRefreshed() => RaiseVolumeSurfaceProperties();

    private VolumeSurfaceInfo? ChosenInfo => _def.VolumeSurface is string name ? Find(name)?.Info : null;

    private VolumeSurfaceOption? Find(string name)
        => VolumeSurfaces.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

    private void SetSource(bool volume)
    {
        if (volume == _isVolumeSource) return;
        if (volume)
        {
            _pairBase = _def.BaseSurface;
            _pairComparison = _def.ComparisonSurface;
            // Hidden factor fields keep their last valid values, so switching back shows them unchanged.
            CutFactor.Revert();
            FillFactor.Revert();
            _isVolumeSource = true;
            if (_lastVolumeSurface is string last && Find(last) is { IsSelectable: true } option)
            {
                ApplyVolumeSurface(option.Info);
                return;
            }
        }
        else
        {
            _lastVolumeSurface = _def.VolumeSurface ?? _lastVolumeSurface;
            _isVolumeSource = false;
            _def = _def with { VolumeSurface = null, BaseSurface = _pairBase, ComparisonSurface = _pairComparison };
            ApplyAutoName();
        }
        OnPropertiesChanged(nameof(IsPairSource), nameof(IsVolumeSurfaceSource), nameof(BaseSurface), nameof(ComparisonSurface));
        RaiseVolumeSurfaceProperties();
        _changed(this);
    }

    private bool ChooseVolumeSurface(string name)
    {
        if (Find(name) is not { IsSelectable: true } option) return false;
        if (_isVolumeSource && _def.VolumeSurface == name) return true;
        if (!_isVolumeSource)
        {
            _pairBase = _def.BaseSurface;
            _pairComparison = _def.ComparisonSurface;
            CutFactor.Revert();
            FillFactor.Revert();
            _isVolumeSource = true;
        }
        ApplyVolumeSurface(option.Info);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="info"/> the source. BaseSurface and ComparisonSurface take its pair as now
    /// resolved (the contract's display-only copy); CutFactor and FillFactor keep the user's values, which
    /// the pipeline ignores for a volume surface source and which come back on switching to the pair.
    /// </summary>
    private void ApplyVolumeSurface(VolumeSurfaceInfo info)
    {
        _def = _def with { VolumeSurface = info.Name, BaseSurface = info.BaseSurface, ComparisonSurface = info.ComparisonSurface };
        _lastVolumeSurface = info.Name;
        ApplyAutoName();
        OnPropertiesChanged(nameof(IsPairSource), nameof(IsVolumeSurfaceSource), nameof(BaseSurface), nameof(ComparisonSurface));
        RaiseVolumeSurfaceProperties();
        _changed(this);
    }

    private void RaiseVolumeSurfaceProperties()
        => OnPropertiesChanged(
            nameof(VolumeSurface), nameof(VolumeSurfaceBaseText), nameof(VolumeSurfaceComparisonText),
            nameof(VolumeSurfaceCutFactorText), nameof(VolumeSurfaceFillFactorText), nameof(VolumeSurfaceProblem));

    private void SetSurfaces(string baseSurface, string comparisonSurface)
    {
        if (baseSurface == _def.BaseSurface && comparisonSurface == _def.ComparisonSurface) return;
        _def = _def with { BaseSurface = baseSurface, ComparisonSurface = comparisonSurface };
        ApplyAutoName();
        OnPropertiesChanged(nameof(BaseSurface), nameof(ComparisonSurface));
        _changed(this);
    }

    /// <summary>While the name is automatic it follows the source: the volume surface's name, or "base vs comparison" once both are picked.</summary>
    private void ApplyAutoName()
    {
        if (!_nameIsAuto) return;
        string? name = null;
        if (_isVolumeSource && _def.VolumeSurface is string volume)
            name = string.Format(_fmt.Culture, _fmt.Config.VolumeSurfaceAnalysisNameFormat, volume);
        else if (!_isVolumeSource && _def.BaseSurface.Length > 0 && _def.ComparisonSurface.Length > 0)
            name = string.Format(_fmt.Culture, _fmt.Config.AnalysisNameFormat, _def.BaseSurface, _def.ComparisonSurface);
        if (name is null) return;
        _def = _def with { Name = name };
        Name.SetFromModel(name);
    }

    private string? CommitName(string text)
    {
        _nameIsAuto = false;
        _def = _def with { Name = text.Trim() };
        _changed(this);
        return null;
    }

    private string? CommitFactor(string text, bool cut)
    {
        if (!_fmt.TryParse(text, out var value)) return DialogText.NotANumber;
        if (value <= 0) return DialogText.FactorMustBePositive;
        _def = cut ? _def with { CutFactor = value } : _def with { FillFactor = value };
        _changed(this);
        return null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
