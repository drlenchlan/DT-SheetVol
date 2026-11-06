using System.Globalization;

namespace SheetVol.Core.Presentation;

/// <summary>One entry of the area unit dropdown (6.5, E917).</summary>
/// <param name="Unit">The unit.</param>
/// <param name="Label">Display label, for example SF or ha.</param>
public sealed record AreaUnitOption(AreaUnit Unit, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>One entry of the report preset selector (6.16).</summary>
/// <param name="Preset">The preset.</param>
/// <param name="Label">Display label.</param>
public sealed record PresetOption(ReportPreset Preset, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>One entry of the comparison selector above the grid (E916).</summary>
/// <param name="AnalysisId">The analysis.</param>
/// <param name="Name">Its name.</param>
public sealed record ComparisonOption(Guid AnalysisId, string Name)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>A run-level problem shown in the problem list (validation or regen).</summary>
/// <param name="Code">Stable SV-Exxx code.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Message">Plain-words message.</param>
/// <param name="Fix">Plain-words remedy, or null.</param>
public sealed record ProblemItem(string Code, Severity Severity, string Message, string? Fix);

/// <summary>Unit labels, conversions and number formatting for one drawing's unit system.</summary>
internal sealed class DisplayFormatter
{
    public DisplayFormatter(PresentationConfig config, UnitSystem units)
    {
        Config = config;
        Units = units;
    }

    public PresentationConfig Config { get; }
    public UnitSystem Units { get; }
    public CultureInfo Culture => Config.EffectiveCulture;

    public string LengthUnit => Units == UnitSystem.Metric ? DialogText.UnitMetres : DialogText.UnitFeet;
    public string VolumeUnit => Units == UnitSystem.Metric ? DialogText.UnitCubicMetres : DialogText.UnitCubicYards;
    public string AverageUnit => Units == UnitSystem.Metric ? DialogText.UnitCubicMetresPerMetre : DialogText.UnitCubicYardsPerFoot;

    public static string AreaUnitLabel(AreaUnit unit) => unit switch
    {
        AreaUnit.SquareFeet => DialogText.UnitSquareFeet,
        AreaUnit.Acres => DialogText.UnitAcres,
        AreaUnit.SquareMetres => DialogText.UnitSquareMetres,
        AreaUnit.Hectares => DialogText.UnitHectares,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>E917: offered units follow the drawing. Small unit first.</summary>
    public static IReadOnlyList<AreaUnit> OfferedAreaUnits(UnitSystem units) => units == UnitSystem.Metric
        ? [AreaUnit.SquareMetres, AreaUnit.Hectares]
        : [AreaUnit.SquareFeet, AreaUnit.Acres];

    /// <summary>6.5 and 6.16: SF or m² for Standard, acres or hectares for HCFCD.</summary>
    public static AreaUnit DefaultAreaUnit(UnitSystem units, ReportPreset preset) => (units, preset) switch
    {
        (UnitSystem.Metric, ReportPreset.HcfcdSegment) => AreaUnit.Hectares,
        (UnitSystem.Metric, _) => AreaUnit.SquareMetres,
        (_, ReportPreset.HcfcdSegment) => AreaUnit.Acres,
        _ => AreaUnit.SquareFeet,
    };

    /// <summary>The same-sized unit in the other system: SF and m², AC and ha.</summary>
    public static AreaUnit Counterpart(AreaUnit unit) => unit switch
    {
        AreaUnit.SquareFeet => AreaUnit.SquareMetres,
        AreaUnit.SquareMetres => AreaUnit.SquareFeet,
        AreaUnit.Acres => AreaUnit.Hectares,
        AreaUnit.Hectares => AreaUnit.Acres,
        _ => unit,
    };

    public NumberDisplay AreaDisplay(AreaUnit unit) => unit switch
    {
        AreaUnit.SquareFeet => Config.SquareFeet,
        AreaUnit.Acres => Config.Acres,
        AreaUnit.SquareMetres => Config.SquareMetres,
        AreaUnit.Hectares => Config.Hectares,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>Square drawing units to the display unit (6.5). Raw values are never altered.</summary>
    public double AreaValue(double squareDrawingUnits, AreaUnit unit) => unit switch
    {
        AreaUnit.Acres => squareDrawingUnits / Config.SquareFeetPerAcre,
        AreaUnit.Hectares => squareDrawingUnits / Config.SquareMetresPerHectare,
        _ => squareDrawingUnits,
    };

    /// <summary>Cubic drawing units to the run's volume unit (CY in feet drawings, m³ in metre drawings).</summary>
    public double VolumeFromCubicDrawingUnits(double cubicDrawingUnits)
        => Units == UnitSystem.Metric ? cubicDrawingUnits : cubicDrawingUnits / Config.CubicFeetPerCubicYard;

    public string Area(double squareDrawingUnits, AreaUnit unit) => AreaDisplay(unit).Format(AreaValue(squareDrawingUnits, unit), Culture);
    public string Volume(double value) => Config.Volume.Format(value, Culture);
    public string Length(double value) => Config.Length.Format(value, Culture);
    public string Coverage(double fraction) => Config.CoveragePercent.Format(fraction * 100, Culture) + DialogText.PercentSuffix;
    public string Offset(double value) => Config.Offset.Format(value, Culture);
    public string Factor(double value) => Config.Factor.Format(value, Culture);

    public string Header(string header, string unit) => string.Format(Culture, DialogText.HeaderWithUnitFormat, header, unit);

    /// <summary>Raw station text for a break spec: invariant culture, full precision.</summary>
    public string SpecNumber(double value) => value.ToString(Config.SpecNumberPattern, CultureInfo.InvariantCulture);

    /// <summary>Parses typed input in the configured culture. Rejects NaN and infinities.</summary>
    public bool TryParse(string? text, out double value)
    {
        if (double.TryParse(text?.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, Culture, out value) && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }
}
