// A1 adapters. Owner: A1.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using DialogUnitSystem = SheetVol.Core.UnitSystem;   // contract revision 2: one UnitSystem in SheetVol.Core
using VolumeUnitSystem = SheetVol.Core.UnitSystem;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// The drawing's linear unit in every shape Core asks for (HANDOFF.md 6.2, E216, P2).
/// </summary>
/// <param name="CivilUnit">Civil 3D drawing-settings unit: <c>Feet</c> or <c>Meters</c>.</param>
/// <param name="DrawingUnitsPerFoot">Scale for C1's <c>PlanningOptions.ForUnitScale</c> and <c>GeometryOptions.ForUnitScale</c>: 1 for feet, 0.3048 for metres.</param>
/// <param name="VolumeUnits">C2's <see cref="DrawingUnits"/>: imperial or metric, one base unit per drawing unit.</param>
/// <param name="DialogUnits">C4's unit system for <c>DrawingInfo</c>.</param>
/// <param name="InsunitsIgnored">The drawing's <c>INSUNITS</c>, reported for evidence only. It is never used: probe P2 found it says Inches in a drawing whose volumes are in feet.</param>
public sealed record DrawingUnitInfo(
    DrawingUnitType CivilUnit,
    double DrawingUnitsPerFoot,
    DrawingUnits VolumeUnits,
    DialogUnitSystem DialogUnits,
    UnitsValue InsunitsIgnored);

/// <summary>
/// Unit detection (HANDOFF.md section 10, A1): the Civil 3D drawing unit from
/// <c>CivilDocument.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits</c>, never
/// <c>INSUNITS</c> (P2). Bounded volumes come back in cubic drawing units of this unit.
/// </summary>
public static class DrawingUnitsReader
{
    /// <summary>Reads the drawing unit of <paramref name="db"/>.</summary>
    /// <param name="db">Any database with a Civil document, including side databases (P10).</param>
    /// <param name="constants">C2's physical constants; <see cref="UnitConstants.MetresPerFoot"/> gives the metric scale.</param>
    public static Outcome<DrawingUnitInfo> Read(Database db, UnitConstants constants)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(constants);
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null)
            return Outcome<DrawingUnitInfo>.Fail(new Problem(AdapterCodes.DrawingUnits, Severity.Error,
                "The drawing has no Civil 3D document, so its drawing unit cannot be read.",
                Fix: "Open the drawing in Civil 3D (or the Core Console with Civil 3D loaded)."));

        var unit = civ.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits;
        return unit switch
        {
            DrawingUnitType.Feet => Outcome<DrawingUnitInfo>.Ok(new DrawingUnitInfo(
                unit, 1.0, new DrawingUnits(VolumeUnitSystem.Imperial, 1.0), DialogUnitSystem.Imperial, db.Insunits)),
            DrawingUnitType.Meters => Outcome<DrawingUnitInfo>.Ok(new DrawingUnitInfo(
                unit, constants.MetresPerFoot, new DrawingUnits(VolumeUnitSystem.Metric, 1.0), DialogUnitSystem.Metric, db.Insunits)),
            _ => Outcome<DrawingUnitInfo>.Fail(new Problem(AdapterCodes.DrawingUnits, Severity.Error,
                $"Civil 3D drawing unit '{unit}' is neither Feet nor Meters.",
                Fix: "Set the drawing unit in Drawing Settings, Units and Zone, to Feet or Meters.")),
        };
    }
}
