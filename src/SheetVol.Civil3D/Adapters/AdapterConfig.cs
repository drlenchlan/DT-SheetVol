// A1 adapters. Owner: A1.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SheetVol.Core;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// Every setting the adapters use that the analysis does not decide (HANDOFF.md 6.13: no
/// style, layer or tolerance literals outside config defaults). A4 embeds this record in the
/// product config file; it reads from and writes to JSON in the same shape as C2's
/// <c>ReportConfig</c> (camelCase, unknown keys rejected).
/// </summary>
public sealed record AdapterConfig
{
    /// <summary>
    /// Surface style for owned volume surfaces (6.5: a no-display style). PLACEHOLDER until LAN
    /// names arrive (A7). When the drawing has no style of this name the surface keeps the style
    /// Civil 3D gives it and SV-E443 is reported; the adapter never creates or guesses a style.
    /// Null means: do not set a style and do not report.
    /// </summary>
    public string? OwnedSurfaceStyle { get; init; } = "SV-No Display";

    /// <summary>
    /// Layer for owned volume surfaces. PLACEHOLDER (A7). A missing layer is reported (SV-E444)
    /// and the surface stays on the layer Civil 3D gave it; the adapter never creates a layer.
    /// Null means: do not set a layer and do not report.
    /// </summary>
    public string? OwnedSurfaceLayer { get; init; } = "SV-VOLUME";

    /// <summary>
    /// E404: rebuild an out-of-date native source surface before use (true), or fail naming it
    /// (false). Reference surfaces are never rebuilt; a stale reference always fails.
    /// ASSUMED default (the handoff names the setting but gives no default).
    /// </summary>
    public bool RebuildSources { get; init; } = true;

    /// <summary>
    /// A volume or area from Civil 3D whose magnitude is below this, cubic or square drawing
    /// units, is returned as exactly 0. Probe P4 measured 5.4e-11 of fill on a polygon with no
    /// fill. A larger negative value is passed through so C2 rejects it (SV-E432).
    /// </summary>
    public double SignNoiseClamp { get; init; } = 1e-6;

    /// <summary>First datum of the 2D-area method (6.5, P22), drawing units.</summary>
    public double AreaDatumLow { get; init; } = 0.0;

    /// <summary>Second datum of the 2D-area method; must differ from <see cref="AreaDatumLow"/>.</summary>
    public double AreaDatumHigh { get; init; } = 1.0;

    /// <summary>Largest allowed distance, drawing units, between an analytic offset point and <c>Alignment.PointLocation</c> (P7, E204).</summary>
    public double CrossCheckTolerance { get; init; } = 1e-6;

    /// <summary>Largest allowed difference, radians, between an arc's converted azimuth and the tangent of its geometry (E204).</summary>
    public double DirectionTolerance { get; init; } = 1e-9;

    /// <summary>Offset, feet (scaled to drawing units), at which the construction cross-check samples each side; capped at half the smallest radius.</summary>
    public double CrossCheckOffsetFeet { get; init; } = 100.0;

    /// <summary>Key of the ownership Xrecord in an owned surface's extension dictionary. Changing it orphans every owned surface already in drawings.</summary>
    public string OwnershipKey { get; init; } = "SHEETVOL_OWNED";

    /// <summary>First string of the ownership Xrecord. Changing it orphans every owned surface already in drawings.</summary>
    public string OwnershipMarker { get; init; } = "SheetVol";

    /// <summary>Composite format appended to an owned-surface name taken by a surface the tool does not own (E412); <c>{0}</c> is 2, 3, ...</summary>
    public string CollisionSuffixFormat { get; init; } = "-{0}";

    /// <summary>Highest suffix number tried before the run fails.</summary>
    public int MaxCollisionSuffix { get; init; } = 99;

    /// <summary>Upper bound on grid cells in a triangle source's window index (memory guard).</summary>
    public int TriangleIndexMaxCells { get; init; } = 1 << 20;

    /// <summary>Checks every setting; returns one SV-E446 error per bad value, or none.</summary>
    public IReadOnlyList<Problem> Validate()
    {
        var p = new List<Problem>();
        void Bad(string key, string what) => p.Add(new Problem(AdapterCodes.ConfigInvalid, Severity.Error, $"Adapter setting '{key}' {what}", Fix: "Correct the setting in the SheetVol config file."));
        static bool Pos(double v) => double.IsFinite(v) && v > 0;

        if (OwnedSurfaceStyle is not null && string.IsNullOrWhiteSpace(OwnedSurfaceStyle)) Bad("ownedSurfaceStyle", "is blank; use null to leave the style alone.");
        if (OwnedSurfaceLayer is not null && string.IsNullOrWhiteSpace(OwnedSurfaceLayer)) Bad("ownedSurfaceLayer", "is blank; use null to leave the layer alone.");
        if (!Pos(SignNoiseClamp)) Bad("signNoiseClamp", "must be a positive number.");
        if (!double.IsFinite(AreaDatumLow) || !double.IsFinite(AreaDatumHigh) || AreaDatumLow == AreaDatumHigh) Bad("areaDatumLow/areaDatumHigh", "must be two different finite numbers.");
        if (!Pos(CrossCheckTolerance)) Bad("crossCheckTolerance", "must be a positive number.");
        if (!Pos(DirectionTolerance)) Bad("directionTolerance", "must be a positive number.");
        if (!Pos(CrossCheckOffsetFeet)) Bad("crossCheckOffsetFeet", "must be a positive number.");
        if (string.IsNullOrWhiteSpace(OwnershipKey)) Bad("ownershipKey", "must not be empty.");
        if (string.IsNullOrWhiteSpace(OwnershipMarker)) Bad("ownershipMarker", "must not be empty.");
        try
        {
            string a = string.Format(CultureInfo.InvariantCulture, CollisionSuffixFormat ?? "", 2);
            string b = string.Format(CultureInfo.InvariantCulture, CollisionSuffixFormat ?? "", 3);
            if (a == b) Bad("collisionSuffixFormat", "must contain {0} so each suffix differs.");
        }
        catch (FormatException) { Bad("collisionSuffixFormat", "is not a valid format with one argument."); }
        if (MaxCollisionSuffix < 2) Bad("maxCollisionSuffix", "must be at least 2.");
        if (TriangleIndexMaxCells < 1) Bad("triangleIndexMaxCells", "must be at least 1.");
        return p;
    }

    /// <summary>Reads settings from JSON; missing keys keep their defaults, unknown keys are errors. The result is validated.</summary>
    public static Outcome<AdapterConfig> FromJson(string json)
    {
        AdapterConfig? cfg;
        try { cfg = JsonSerializer.Deserialize<AdapterConfig>(json, JsonOptions); }
        catch (JsonException ex)
        {
            return Outcome<AdapterConfig>.Fail(new Problem(AdapterCodes.ConfigInvalid, Severity.Error, $"Adapter settings JSON is invalid: {ex.Message}"));
        }
        if (cfg is null) return Outcome<AdapterConfig>.Fail(new Problem(AdapterCodes.ConfigInvalid, Severity.Error, "Adapter settings JSON is empty."));
        var problems = cfg.Validate();
        return problems.Count > 0 ? Outcome<AdapterConfig>.Fail(problems) : Outcome<AdapterConfig>.Ok(cfg);
    }

    /// <summary>Writes the settings as indented JSON in the shape <see cref="FromJson"/> reads.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
