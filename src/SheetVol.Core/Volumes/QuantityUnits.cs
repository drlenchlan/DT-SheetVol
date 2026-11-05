using System.Globalization;

namespace SheetVol.Core.Volumes;

/// <summary>
/// The drawing's linear unit as supplied by the adapter's unit detection. Core never infers it
/// (HANDOFF.md 6.2, E606). <paramref name="System"/> is the contract's <see cref="SheetVol.Core.UnitSystem"/>
/// (rev 2): Imperial means the base length is the foot (CY; SF or AC), Metric the metre (m³; m² or ha).
/// <paramref name="BaseUnitsPerDrawingUnit"/> is feet per drawing unit
/// for <see cref="UnitSystem.Imperial"/> and metres per drawing unit for
/// <see cref="UnitSystem.Metric"/>: 1 for a drawing in feet or metres.
/// </summary>
public readonly record struct DrawingUnits(UnitSystem System, double BaseUnitsPerDrawingUnit);

/// <summary>
/// Converts raw drawing-unit quantities to the run's display units. Built only through
/// <see cref="Create"/>, which refuses a bad scale. Every conversion is a pure multiply, so
/// raw values are never altered and conversion happens exactly once per output value.
/// </summary>
public sealed class QuantityUnits
{
    private readonly VolumeConfig _config;
    private readonly double _scale;

    private QuantityUnits(DrawingUnits units, VolumeConfig config)
    {
        Units = units;
        _config = config;
        _scale = units.BaseUnitsPerDrawingUnit;
    }

    /// <summary>The drawing units this converter was built for.</summary>
    public DrawingUnits Units { get; }

    /// <summary>Validates the scale and builds the converter.</summary>
    public static Outcome<QuantityUnits> Create(DrawingUnits units, VolumeConfig config)
    {
        double s = units.BaseUnitsPerDrawingUnit;
        if (!double.IsFinite(s) || s <= 0)
            return Outcome<QuantityUnits>.Fail(new Problem(
                VolumeCodes.InvalidDrawingUnits, Severity.Error,
                string.Create(CultureInfo.InvariantCulture, $"Drawing unit scale {s:R} is not a positive number."),
                Fix: "Check the drawing's INSUNITS and the adapter's unit detection."));
        if (!Enum.IsDefined(units.System))
            return Outcome<QuantityUnits>.Fail(new Problem(
                VolumeCodes.InvalidDrawingUnits, Severity.Error, $"Unknown unit system {units.System}."));
        return Outcome<QuantityUnits>.Ok(new QuantityUnits(units, config));
    }

    /// <summary>Label of the run's volume unit: CY (imperial) or m³ (metric).</summary>
    public string VolumeLabel => Units.System == UnitSystem.Imperial ? _config.Labels.CubicYards : _config.Labels.CubicMetres;

    /// <summary>Label of the base length unit: LF (imperial) or m (metric).</summary>
    public string LengthLabel => Units.System == UnitSystem.Imperial ? _config.Labels.LinearFeet : _config.Labels.Metres;

    /// <summary>Label of volume per length: CY/LF (imperial) or m³/m (metric).</summary>
    public string AverageLabel => Units.System == UnitSystem.Imperial ? _config.Labels.CubicYardsPerFoot : _config.Labels.CubicMetresPerMetre;

    /// <summary>Cubic drawing units to the run's volume unit: ft³ ÷ 27 = CY for imperial, m³ for metric.</summary>
    public double ToVolumeUnit(double cubicDrawingUnits)
    {
        double cubicBase = cubicDrawingUnits * _scale * _scale * _scale;
        return Units.System == UnitSystem.Imperial ? cubicBase / _config.Constants.CubicFeetPerCubicYard : cubicBase;
    }

    /// <summary>Drawing length units to the base length unit (feet or metres).</summary>
    public double ToLength(double drawingUnits) => drawingUnits * _scale;

    /// <summary>Square drawing units to <paramref name="unit"/>. Call <see cref="CheckAreaUnit"/> first; a unit of the other system throws.</summary>
    public double ToArea(double squareDrawingUnits, AreaUnit unit)
    {
        if (CheckAreaUnit(unit) is { } problem)
            throw new ArgumentException(problem.Message, nameof(unit));
        double squareBase = squareDrawingUnits * _scale * _scale;
        return unit switch
        {
            AreaUnit.SquareFeet or AreaUnit.SquareMetres => squareBase,
            AreaUnit.Acres => squareBase / _config.Constants.SquareFeetPerAcre,
            AreaUnit.Hectares => squareBase / _config.Constants.SquareMetresPerHectare,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown area unit."),
        };
    }

    /// <summary>Label of an area unit: SF, AC, m² or ha.</summary>
    public string AreaLabel(AreaUnit unit) => unit switch
    {
        AreaUnit.SquareFeet => _config.Labels.SquareFeet,
        AreaUnit.Acres => _config.Labels.Acres,
        AreaUnit.SquareMetres => _config.Labels.SquareMetres,
        AreaUnit.Hectares => _config.Labels.Hectares,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown area unit."),
    };

    /// <summary>
    /// Cubic drawing units equal to the 6.5 absolute partition tolerance (0.5 ft³ by default,
    /// the same volume expressed in m³ for metric drawings, E216).
    /// </summary>
    public double PartitionAbsoluteCubicDrawingUnits()
    {
        double cubicFeet = _config.Tolerances.PartitionAbsoluteCubicFeet;
        double cubicBase = Units.System == UnitSystem.Imperial
            ? cubicFeet
            : cubicFeet * Math.Pow(_config.Constants.MetresPerFoot, 3);
        return cubicBase / (_scale * _scale * _scale);
    }

    /// <summary>
    /// The 6.5 tolerance for comparing two volumes, in cubic drawing units:
    /// <c>max(0.5 ft³, PartitionRelative × grossTotal)</c>.
    /// </summary>
    public double PartitionTolerance(double grossTotalCubicDrawingUnits)
        => Math.Max(PartitionAbsoluteCubicDrawingUnits(), _config.Tolerances.PartitionRelative * Math.Abs(grossTotalCubicDrawingUnits));

    /// <summary>Null when <paramref name="unit"/> belongs to this drawing's unit system; otherwise the SV-E433 problem.</summary>
    public Problem? CheckAreaUnit(AreaUnit unit) => CheckAreaUnit(Units.System, unit);

    /// <summary>Null when <paramref name="unit"/> belongs to <paramref name="system"/>; otherwise the SV-E433 problem.</summary>
    public static Problem? CheckAreaUnit(UnitSystem system, AreaUnit unit)
    {
        if (AreaUnitsFor(system).Contains(unit)) return null;
        return new Problem(
            VolumeCodes.AreaUnitMismatch, Severity.Error,
            $"Area unit {unit} does not belong to a {system} drawing.",
            Fix: system == UnitSystem.Imperial ? "Choose SF or AC." : "Choose m² or ha.");
    }

    /// <summary>The area units offered for a unit system (E917): SF and AC, or m² and ha.</summary>
    public static IReadOnlyList<AreaUnit> AreaUnitsFor(UnitSystem system) => system == UnitSystem.Imperial
        ? [AreaUnit.SquareFeet, AreaUnit.Acres]
        : [AreaUnit.SquareMetres, AreaUnit.Hectares];

    /// <summary>Default area unit: SF or m² for the Standard preset, AC or ha for HcfcdSegment (6.5, 6.16).</summary>
    public static AreaUnit DefaultAreaUnit(UnitSystem system, ReportPreset preset) => (system, preset) switch
    {
        (UnitSystem.Imperial, ReportPreset.HcfcdSegment) => AreaUnit.Acres,
        (UnitSystem.Imperial, _) => AreaUnit.SquareFeet,
        (UnitSystem.Metric, ReportPreset.HcfcdSegment) => AreaUnit.Hectares,
        _ => AreaUnit.SquareMetres,
    };
}
