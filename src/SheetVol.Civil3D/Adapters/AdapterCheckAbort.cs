// A1 adapters: SVADAPTERS_ABORT, which isolates what crashed the Core Console when a
// transaction that created Civil 3D objects was aborted (first SVADAPTERS run, 2026-11-05).
// One case per process: a crash ends the run, so each case has its own script
// (tools/scripts/adapters-abort-*.scr). Evidence for AnalysisResolver's "aborting the caller's
// transaction removes anything created here" and for A4's rollback (HANDOFF.md 6.10).

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using N = SheetVol.Civil3D.SelfTest.FixtureSpec.Names;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.Civil3D.Adapters;

public static partial class AdapterCheckCommands
{
    /// <summary>Creates Civil 3D objects in a transaction, aborts it, and reports what is left. Prompts for the case.</summary>
    [CommandMethod("SVADAPTERS_ABORT", CommandFlags.Modal)]
    public static void CheckAbort()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        if (doc is null) return;
        _db = doc.Database;
        _cfg = new AdapterConfig();
        var pr = doc.Editor.GetString(new PromptStringOptions("\nCase [OWNED/ALIGN/ALIGNERASE]: "));
        string what = pr.Status == PromptStatus.OK ? pr.StringResult.Trim().ToUpperInvariant() : "";
        Say($"===== SVADAPTERS_ABORT {what} begin =====");
        Step("abort " + what, () =>
        {
            var civ = CivilDocument.GetCivilDocument(_db);
            int surfacesBefore = civ.GetSurfaceIds().Count, alignmentsBefore = civ.GetAlignmentIds().Count;
            if (what == "OWNED") Fixtures.CreateA(_db);
            surfacesBefore = civ.GetSurfaceIds().Count;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                switch (what)
                {
                    case "OWNED":
                    {
                        var u = DrawingUnitsReader.Read(_db, VolCfg.Constants).Value;
                        _units = u;
                        var b = Ensure(tr, [new AnalysisDef(Guid.NewGuid(), "A", N.EG, N.FgA)])[0];
                        Say($"created owned surface '{b.Owned.Name}' (created={b.Owned.Created}) through AnalysisResolver, with its ownership tag; aborting");
                        break;
                    }
                    case "ALIGN":
                    case "ALIGNERASE":
                    {
                        var al = NewAlignment(tr, civ, "SV-A1-ABORT");
                        al.Entities.AddFixedLine(new Point3d(0, 4000, 0), new Point3d(100, 4000, 0));
                        if (what == "ALIGNERASE") al.Erase();
                        Say($"created alignment 'SV-A1-ABORT'{(what == "ALIGNERASE" ? " and erased it" : "")}; aborting");
                        break;
                    }
                    default:
                        throw new ArgumentException($"unknown case '{what}'");
                }
                tr.Abort();
            }
            Say($"abort returned; surfaces {surfacesBefore} -> {civ.GetSurfaceIds().Count}, alignments {alignmentsBefore} -> {civ.GetAlignmentIds().Count}");
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                int live = 0;
                foreach (ObjectId id in civ.GetSurfaceIds()) if (!id.IsErased) live++;
                foreach (ObjectId id in civ.GetAlignmentIds()) if (!id.IsErased) live++;
                Say($"a new transaction opens after the abort; {live} live surfaces + alignments");
                tr.Commit();
            }
        });
        Say($"===== SVADAPTERS_ABORT {what} end =====");
    }
}
