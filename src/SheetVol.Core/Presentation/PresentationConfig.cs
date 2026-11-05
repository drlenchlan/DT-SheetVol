using System.Globalization;

namespace SheetVol.Core.Presentation;

/// <summary>
/// How one quantity is rounded and displayed. The format pattern is built here and
/// nowhere else, so no format literal lives in the view models (HANDOFF.md 6.13).
/// </summary>
/// <param name="Decimals">Decimal places shown and used for rounding.</param>
/// <param name="Grouped">Thousands separators on.</param>
/// <param name="TrimZeros">Trailing zeros after the decimal point dropped.</param>
public sealed record NumberDisplay(int Decimals, bool Grouped = true, bool TrimZeros = false)
{
    /// <summary>Rounds half away from zero to <see cref="Decimals"/>. Negative zero becomes zero.</summary>
    public double Round(double value)
    {
        var rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    /// <summary>The .NET custom numeric format pattern, for example <c>#,##0.00</c>.</summary>
    public string Pattern =>
        (Grouped ? "#,##0" : "0")
        + (Decimals > 0 ? "." + new string(TrimZeros ? '#' : '0', Decimals) : string.Empty);

    /// <summary>Rounds, then formats with the culture's separators.</summary>
    public string Format(double value, IFormatProvider culture) => Round(value).ToString(Pattern, culture);
}

/// <summary>
/// Every display format, unit factor and dialog default the presentation layer uses.
/// Built-in values are placeholders per A7 and A12 until Dan supplies LAN values; the
/// host may load its own instance from config and pass it to <see cref="DialogViewModel"/>.
/// </summary>
public sealed record PresentationConfig
{
    /// <summary>Built-in defaults.</summary>
    public static PresentationConfig Default { get; } = new();

    /// <summary>Culture for displayed numbers and typed input. Null means the current culture at the time of use.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>Segment length, drawing length units.</summary>
    public NumberDisplay Length { get; init; } = new(2);

    /// <summary>Cut, fill and net, in the run's volume unit (CY or m³).</summary>
    public NumberDisplay Volume { get; init; } = new(0);

    /// <summary>Area in square feet (6.5: 0 decimals).</summary>
    public NumberDisplay SquareFeet { get; init; } = new(0);

    /// <summary>Area in acres (6.5: 2 decimals).</summary>
    public NumberDisplay Acres { get; init; } = new(2);

    /// <summary>Area in square metres (6.5: 0 decimals).</summary>
    public NumberDisplay SquareMetres { get; init; } = new(0);

    /// <summary>Area in hectares (6.5: 2 decimals).</summary>
    public NumberDisplay Hectares { get; init; } = new(2);

    /// <summary>Coverage, shown as a percentage of the boundary area.</summary>
    public NumberDisplay CoveragePercent { get; init; } = new(0, Grouped: false);

    /// <summary>HCFCD impacted length (A12: whole feet).</summary>
    public NumberDisplay ImpactedLength { get; init; } = new(0);

    /// <summary>HCFCD average volume per unit length (A12: 2 decimals).</summary>
    public NumberDisplay ImpactAverage { get; init; } = new(2, Grouped: false);

    /// <summary>OHWM elevations.</summary>
    public NumberDisplay Elevation { get; init; } = new(2, Grouped: false);

    /// <summary>Offsets in the left panel.</summary>
    public NumberDisplay Offset { get; init; } = new(2, Grouped: false, TrimZeros: true);

    /// <summary>Cut and fill factors.</summary>
    public NumberDisplay Factor { get; init; } = new(4, Grouped: false, TrimZeros: true);

    /// <summary>Elapsed regen time in the status line.</summary>
    public NumberDisplay Seconds { get; init; } = new(1, Grouped: false);

    /// <summary>Pattern for raw stations written into a break spec (invariant culture, full precision).</summary>
    public string SpecNumberPattern { get; init; } = "0.##########";

    /// <summary>6.5: square feet per acre.</summary>
    public double SquareFeetPerAcre { get; init; } = 43_560;

    /// <summary>6.5: square metres per hectare.</summary>
    public double SquareMetresPerHectare { get; init; } = 10_000;

    /// <summary>Cubic drawing units per cubic yard in an imperial (feet) drawing.</summary>
    public double CubicFeetPerCubicYard { get; init; } = 27;

    /// <summary>Two stations closer than this are the same break (E206 merges at 1e-6).</summary>
    public double StationTolerance { get; init; } = 1e-6;

    /// <summary>Coverage at or above this counts as full; below it the row is flagged (6.5).</summary>
    public double FullCoverage { get; init; } = 0.9995;

    /// <summary>The run the dialog edits when the host names none.</summary>
    public string DefaultRunName { get; init; } = "Run1";

    /// <summary>Break spec of a new run in an imperial drawing.</summary>
    public string DefaultBreakSpecImperial { get; init; } = "START..END@500";

    /// <summary>Break spec of a new run in a metric drawing (E216).</summary>
    public string DefaultBreakSpecMetric { get; init; } = "START..END@100";

    /// <summary>Offset distance each side for a new run in an imperial drawing.</summary>
    public double DefaultOffsetImperial { get; init; } = 100;

    /// <summary>Offset distance each side for a new run in a metric drawing (E216).</summary>
    public double DefaultOffsetMetric { get; init; } = 30;

    /// <summary>
    /// Auto mode set when "Regenerate when the alignment or stations change" is checked
    /// and the run has no earlier non-Off mode to restore. A4: Views.
    /// </summary>
    public AutoMode AutoModeWhenChecked { get; init; } = AutoMode.Views;

    /// <summary>Draw options for a new dialog session.</summary>
    public DrawOptions DefaultDrawOptions { get; init; } = new(BoundaryPolylines: true, TableAtEachSegment: true, SummaryTable: true);

    /// <summary>Name given to an analysis once both surfaces are picked: {0} base, {1} comparison (6.8 shows "EG vs FG").</summary>
    public string AnalysisNameFormat { get; init; } = "{0} vs {1}";

    /// <summary>Name of a new analysis before surfaces are picked: {0} is its 1-based position.</summary>
    public string NewAnalysisNameFormat { get; init; } = "Comparison {0}";

    /// <summary>The culture actually used: <see cref="Culture"/> or the current culture.</summary>
    public CultureInfo EffectiveCulture => Culture ?? CultureInfo.CurrentCulture;
}
