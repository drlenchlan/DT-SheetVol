using System.Collections.ObjectModel;

namespace SheetVol.Core.Presentation;

/// <summary>
/// One base/comparison pair in the "Surfaces to compare" card (6.5, 6.14): name, base,
/// comparison, cut factor, fill factor. Every change goes back to the dialog, which
/// re-runs Validate (live validation) and never regenerates.
/// </summary>
public sealed class AnalysisEditorViewModel : ObservableObject
{
    private readonly DisplayFormatter _fmt;
    private readonly Action<AnalysisEditorViewModel> _changed;
    private readonly Func<bool> _canRemove;
    private AnalysisDef _def;
    private bool _nameIsAuto;

    internal AnalysisEditorViewModel(
        AnalysisDef definition,
        ObservableCollection<string> surfaces,
        DisplayFormatter formatter,
        Action<AnalysisEditorViewModel> changed,
        Action<AnalysisEditorViewModel> remove,
        Func<bool> canRemove,
        bool nameIsAuto,
        Action fieldErrorChanged)
    {
        _def = definition;
        _fmt = formatter;
        _changed = changed;
        _canRemove = canRemove;
        _nameIsAuto = nameIsAuto;
        Surfaces = surfaces;
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

    /// <summary>Analysis name (HCFCD: the material row label, 6.16).</summary>
    public TextFieldViewModel Name { get; }

    /// <summary>Cut factor, greater than zero.</summary>
    public TextFieldViewModel CutFactor { get; }

    /// <summary>Fill factor, greater than zero.</summary>
    public TextFieldViewModel FillFactor { get; }

    /// <summary>Removes this comparison. Disabled when it is the only one.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Base surface (HCFCD: existing). Null or empty until picked.</summary>
    public string? BaseSurface
    {
        get => NullIfEmpty(_def.BaseSurface);
        set => SetSurfaces(value ?? string.Empty, _def.ComparisonSurface);
    }

    /// <summary>Comparison surface (HCFCD: design). Null or empty until picked.</summary>
    public string? ComparisonSurface
    {
        get => NullIfEmpty(_def.ComparisonSurface);
        set => SetSurfaces(_def.BaseSurface, value ?? string.Empty);
    }

    internal void RefreshCommands() => RemoveCommand.RaiseCanExecuteChanged();

    internal bool HasFieldError => Name.HasError || CutFactor.HasError || FillFactor.HasError;

    private void SetSurfaces(string baseSurface, string comparisonSurface)
    {
        if (baseSurface == _def.BaseSurface && comparisonSurface == _def.ComparisonSurface) return;
        _def = _def with { BaseSurface = baseSurface, ComparisonSurface = comparisonSurface };
        if (_nameIsAuto && baseSurface.Length > 0 && comparisonSurface.Length > 0)
        {
            _def = _def with { Name = string.Format(_fmt.Culture, _fmt.Config.AnalysisNameFormat, baseSurface, comparisonSurface) };
            Name.SetFromModel(_def.Name);
        }
        OnPropertiesChanged(nameof(BaseSurface), nameof(ComparisonSurface));
        _changed(this);
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
