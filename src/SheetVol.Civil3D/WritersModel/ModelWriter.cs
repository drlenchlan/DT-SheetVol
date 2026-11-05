// A2 model writers: the one entry point A4 calls inside its 6.10 step 5 transaction.
// Owner: A2.

using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>
/// Applies a run's model-space outputs: boundary polylines, impacted-area outlines, match
/// marks, per-sheet model-space tables, the master table and HCFCD tables. Every writer plans
/// first (read-only); if any plan raises an error nothing is written. Then each writes only
/// what changed. The caller owns the transaction and commits it after the paper writers and
/// the persistence record (6.10 step 5); aborting it undoes everything written here (T20).
/// </summary>
public static class ModelWriter
{
    /// <summary>
    /// Applies <paramref name="plan"/> against the objects recorded in <paramref name="stored"/>.
    /// Refuses (no writes) when the stored run's schema is newer than this plugin's (E105), when
    /// config is invalid, when an input is malformed, or when a style, layer, layout or anchor is
    /// missing (E501, E601).
    /// </summary>
    public static ModelWriteResult Apply(ModelWriteContext ctx, ModelWritePlan plan, RunStoreReadResult stored)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stored);
        var problems = new List<Problem>();

        if (!stored.CanModify)
            return Failed(stored.Problems.Count > 0 ? stored.Problems
                : [new Problem(OutputCodes.StoredSchemaNewer, Severity.Error, $"Run '{ctx.RunName}' cannot be modified by this plugin.")]);
        problems.AddRange(ctx.Config.Validate());
        problems.AddRange(ctx.Report.Validate());
        if (string.IsNullOrWhiteSpace(ctx.RunName))
            problems.Add(new Problem(ModelWriterCodes.InvalidInput, Severity.Error, "The run has no name."));
        if (problems.Count > 0) return Failed(problems);

        var all = stored.Record?.Objects ?? [];
        var mine = all.Where(s => ModelWriteResult.Roles.Contains(s.Role)).ToList();
        foreach (var dup in mine.GroupBy(s => s.Key).Where(g => g.Count() > 1))
            problems.Add(new Problem(OutputCodes.PersistedJsonInvalid, Severity.Error,
                $"The stored record of run '{ctx.RunName}' lists {dup.Key} {dup.Count()} times; it cannot be trusted.",
                Fix: "Clear the run (SVCLEAR) and regenerate."));
        if (problems.Count > 0) return Failed(problems);

        var cfg = ctx.Config;
        var boundaries = new PolylineWriter<Boundary>(ctx, OwnedRole.Boundary, "boundary", cfg.BoundaryLayer, "modelWriters.boundaryLayer", b => b.Drawn);
        var outlines = new PolylineWriter<ImpactOutlineSpec>(ctx, OwnedRole.ImpactOutline, "impacted-area outline", cfg.ImpactOutlineLayer, "modelWriters.impactOutlineLayer", o => o.Outline);
        var lines = new MatchLineWriter(ctx);
        var texts = new MatchTextWriter(ctx);
        var tables = new ModelTableWriter(ctx);
        var hcfcd = new HcfcdTableWriter(ctx);

        var marks = cfg.MatchMarks.Enabled ? plan.MatchMarks : [];
        boundaries.Plan(plan.Boundaries.Select(b => (new OwnedKey(b.SegmentId, OwnedRole.Boundary), b)), mine, problems);
        outlines.Plan((cfg.DrawImpactOutline ? plan.ImpactOutlines : []).Select(o => (o.Key, o)), mine, problems);
        lines.Plan(marks.Select(m => (m.LineKey, m)), mine, problems);
        texts.Plan(marks.Select(m => (m.TextKey, m)), mine, problems);
        tables.Plan(plan.Tables.Select(t => (t.Key, t)), mine, problems);
        hcfcd.Plan(plan.HcfcdTables.Select(h => (h.Key, h)), mine, problems);
        if (problems.Any(p => p.Severity == Severity.Error)) return Failed(problems);

        var changes = new List<ObjectChange>();
        var objects = new List<StoredObjectRef>();
        var guard = new LayerGuard(ctx.Transaction);
        boundaries.Execute(guard, changes, objects, problems);
        outlines.Execute(guard, changes, objects, problems);
        lines.Execute(guard, changes, objects, problems);
        texts.Execute(guard, changes, objects, problems);
        tables.Execute(guard, changes, objects, problems);
        hcfcd.Execute(guard, changes, objects, problems);
        guard.Relock();
        problems.AddRange(guard.Notes);

        // Stored keys of these roles that no writer recognises (none today) are carried over untouched.
        var owners = new Func<OwnedKey, bool>[] { boundaries.Owns, outlines.Owns, lines.Owns, texts.Owns, tables.Owns, hcfcd.Owns };
        objects.AddRange(mine.Where(s => !owners.Any(o => o(s.Key))));
        return new ModelWriteResult(true, changes, objects, problems);
    }

    private static ModelWriteResult Failed(IReadOnlyList<Problem> problems) => new(false, [], [], problems);
}
