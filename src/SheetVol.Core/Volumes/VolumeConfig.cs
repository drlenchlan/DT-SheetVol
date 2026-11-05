namespace SheetVol.Core.Volumes;

/// <summary>
/// Unit conversion constants. These are physical definitions, not LAN preferences; they live
/// in config only so that no numeric literal sits in the code (HANDOFF.md 6.13). Do not change them.
/// </summary>
public sealed record UnitConstants
{
    /// <summary>Cubic feet in one cubic yard (exact).</summary>
    public double CubicFeetPerCubicYard { get; init; } = 27.0;

    /// <summary>Square feet in one acre (exact, international acre).</summary>
    public double SquareFeetPerAcre { get; init; } = 43_560.0;

    /// <summary>Square metres in one hectare (exact).</summary>
    public double SquareMetresPerHectare { get; init; } = 10_000.0;

    /// <summary>Metres in one international foot (exact). Used only to scale the partition tolerance for metric drawings (E216).</summary>
    public double MetresPerFoot { get; init; } = 0.3048;
}

/// <summary>Unit labels written into tables and exports. Defaults are placeholders until LAN names arrive (A7).</summary>
public sealed record UnitLabels
{
    /// <summary>Volume unit label for imperial drawings (cubic yards).</summary>
    public string CubicYards { get; init; } = "CY";

    /// <summary>Volume unit label for metric drawings (cubic metres).</summary>
    public string CubicMetres { get; init; } = "m³";

    /// <summary>Area unit label: square feet.</summary>
    public string SquareFeet { get; init; } = "SF";

    /// <summary>Area unit label: acres.</summary>
    public string Acres { get; init; } = "AC";

    /// <summary>Area unit label: square metres.</summary>
    public string SquareMetres { get; init; } = "m²";

    /// <summary>Area unit label: hectares.</summary>
    public string Hectares { get; init; } = "ha";

    /// <summary>Length unit label for imperial drawings (linear feet).</summary>
    public string LinearFeet { get; init; } = "LF";

    /// <summary>Length unit label for metric drawings (metres).</summary>
    public string Metres { get; init; } = "m";

    /// <summary>Average (volume per length) label for imperial drawings.</summary>
    public string CubicYardsPerFoot { get; init; } = "CY/LF";

    /// <summary>Average (volume per length) label for metric drawings.</summary>
    public string CubicMetresPerMetre { get; init; } = "m³/m";
}

/// <summary>Numeric tolerances for reconciliation and display.</summary>
public sealed record VolumeTolerances
{
    /// <summary>
    /// E409. A cut, fill or net value whose magnitude is below this, in the run's volume unit
    /// (CY or m³), displays as 0. Exports keep the raw value. ASSUMED default, not from the handoff.
    /// </summary>
    public double VolumeZeroTolerance { get; init; } = 0.01;

    /// <summary>6.5 partition check, absolute part: 0.5 ft³, scaled to cubic metres for metric drawings.</summary>
    public double PartitionAbsoluteCubicFeet { get; init; } = 0.5;

    /// <summary>6.5 partition check, relative part: 1e-9 of the gross (cut + fill) total.</summary>
    public double PartitionRelative { get; init; } = 1e-9;

    /// <summary>
    /// Coverage below <c>1 − CoverageFlagTolerance</c> is flagged (E405, E419). Absorbs
    /// floating-point noise in the 2D area. ASSUMED default.
    /// </summary>
    public double CoverageFlagTolerance { get; init; } = 1e-6;

    /// <summary>
    /// Coverage above <c>1 + CoverageExcessTolerance</c> is an error: surface data cannot cover
    /// more than the boundary. ASSUMED default.
    /// </summary>
    public double CoverageExcessTolerance { get; init; } = 1e-4;
}

/// <summary>Everything Core.Volumes needs from config. Part of <c>SheetVol.Core.Output.ReportConfig</c>.</summary>
public sealed record VolumeConfig
{
    /// <summary>Physical unit constants.</summary>
    public UnitConstants Constants { get; init; } = new();

    /// <summary>Unit labels for tables and exports.</summary>
    public UnitLabels Labels { get; init; } = new();

    /// <summary>Reconciliation and display tolerances.</summary>
    public VolumeTolerances Tolerances { get; init; } = new();

    /// <summary>Prefix of the owned volume surface name, <c>SV-&lt;base&gt;-&lt;comparison&gt;</c> (6.5).</summary>
    public string OwnedSurfacePrefix { get; init; } = "SV-";

    /// <summary>Separator between base and comparison in the owned volume surface name (6.5).</summary>
    public string OwnedSurfaceSeparator { get; init; } = "-";
}
