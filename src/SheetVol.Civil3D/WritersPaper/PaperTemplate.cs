// A3 work package. Owner: A3.
//
// The sheet template (HANDOFF.md 6.7, P11): read with ReadDwgFile into a side database,
// never opened as a document. Everything E601 asks for is checked here, before any
// write: the file, the layout, the plan viewport and the table anchor. Every missing
// piece is reported at once, by name.
//
// Plan viewport: the floating viewport on PaperConfig.PlanViewportLayer; when none is on
// it, the largest floating viewport. The overall (paper-space) viewport is
// Layout.GetViewports()[0] on an activated layout (S2, P11); when that list is empty, the
// first viewport in the layout block, which is the one AutoCAD adopts as overall on first
// activation (FX, S2 measured). It is never cloned.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>
/// A loaded, validated sheet template. Holds the side database the layouts are cloned from,
/// so keep it alive for the whole regen and dispose it afterwards.
/// </summary>
public sealed class PaperTemplate : IDisposable
{
    private PaperTemplate(Database source, string path, string layoutName, ObjectId layoutId,
        ObjectId overallViewportId, ObjectId planViewportId, ObjectId anchorReferenceId, IReadOnlyList<ObjectId> cloneIds,
        IReadOnlyList<ObjectId> otherViewportsOn,
        double planWidth, double planHeight, Pt planCenter, string planLayer, bool planChosenByLayer,
        Pt tableAnchor, bool anchorFromBlock, IReadOnlyList<string> otherViewportHandles, IReadOnlyList<Problem> notes)
    {
        Source = source; Path = path; LayoutName = layoutName; LayoutId = layoutId;
        OverallViewportId = overallViewportId; PlanViewportId = planViewportId; AnchorReferenceId = anchorReferenceId; CloneIds = cloneIds;
        OtherViewportsOn = otherViewportsOn;
        PlanViewportWidth = planWidth; PlanViewportHeight = planHeight; PlanViewportCenter = planCenter; PlanViewportLayer = planLayer;
        PlanChosenByLayer = planChosenByLayer; TableAnchor = tableAnchor; AnchorFromBlock = anchorFromBlock;
        OtherViewportHandles = otherViewportHandles; Notes = notes;
    }

    internal Database Source { get; }
    internal ObjectId LayoutId { get; }
    internal ObjectId OverallViewportId { get; }
    internal ObjectId PlanViewportId { get; }
    internal ObjectId AnchorReferenceId { get; }
    internal IReadOnlyList<ObjectId> CloneIds { get; }

    /// <summary>Floating viewports other than the plan one that are on in the template. Their clones are replaced by new viewports so they display on first activation, as the plan viewport is.</summary>
    internal IReadOnlyList<ObjectId> OtherViewportsOn { get; }

    /// <summary>Full path of the template file.</summary>
    public string Path { get; }

    /// <summary>The template layout's name as stored.</summary>
    public string LayoutName { get; }

    /// <summary>Plan viewport width, paper units. A4 passes it to C3's ViewRequest as TemplatePaperWidth.</summary>
    public double PlanViewportWidth { get; }

    /// <summary>Plan viewport height, paper units. A4 passes it to C3's ViewRequest as TemplatePaperHeight.</summary>
    public double PlanViewportHeight { get; }

    /// <summary>Plan viewport centre on paper, paper units.</summary>
    public Pt PlanViewportCenter { get; }

    /// <summary>The plan viewport's layer.</summary>
    public string PlanViewportLayer { get; }

    /// <summary>True when the plan viewport was found by layer; false when it is the largest floating viewport.</summary>
    public bool PlanChosenByLayer { get; }

    /// <summary>Table top-left corner on paper: the anchor block's insertion point, else the config's TablePaperPoint.</summary>
    public Pt TableAnchor { get; }

    /// <summary>True when <see cref="TableAnchor"/> came from an anchor block reference.</summary>
    public bool AnchorFromBlock { get; }

    /// <summary>Handles (in the template) of floating viewports that are not the plan viewport. Cloned, never driven (E602).</summary>
    public IReadOnlyList<string> OtherViewportHandles { get; }

    /// <summary>Info notes from loading (E602).</summary>
    public IReadOnlyList<Problem> Notes { get; }

    /// <summary>Disposes the side database.</summary>
    public void Dispose() => Source.Dispose();

    /// <summary>
    /// Loads and validates the template named by <paramref name="config"/>. Fails with SV-E691
    /// on bad config values and SV-E601 for each missing or ambiguous piece. Writes nothing to
    /// any drawing.
    /// </summary>
    /// <param name="config">Paper config.</param>
    /// <param name="anchorBlockName">Table anchor block name, from ReportConfig.Placement.TableAnchorBlock (C2).</param>
    public static Core.Volumes.Outcome<PaperTemplate> Load(PaperConfig config, string anchorBlockName)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configProblems = config.Validate().ToList();
        if (string.IsNullOrWhiteSpace(anchorBlockName))
            configProblems.Add(new Problem(PaperCodes.ConfigInvalid, Severity.Error, "Report config placement.tableAnchorBlock: must not be empty."));
        if (configProblems.Count > 0) return Core.Volumes.Outcome<PaperTemplate>.Fail(configProblems);

        var missing = new List<Problem>();
        void Missing(string what, string fix) => missing.Add(new Problem(PaperCodes.TemplateMissing, Severity.Error, what, Fix: fix));

        if (string.IsNullOrWhiteSpace(config.TemplatePath)) Missing("Paper config templatePath is not set: no sheet template is named.", "Set templatePath to the sheet template DWT.");
        if (string.IsNullOrWhiteSpace(config.TemplateLayout)) Missing("Paper config templateLayout is not set: no template layout is named.", "Set templateLayout to the layout in the template to clone.");
        if (missing.Count > 0) return Core.Volumes.Outcome<PaperTemplate>.Fail(missing);

        string path;
        try { path = System.IO.Path.GetFullPath(config.TemplatePath); }
        catch (Exception ex) { return Fail($"Sheet template path '{config.TemplatePath}' is not a valid path: {ex.Message}", "Correct templatePath."); }
        if (!File.Exists(path)) return Fail($"Sheet template '{path}' was not found.", "Correct templatePath or restore the template file.");

        var source = new Database(false, true);
        try
        {
            try
            {
                source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                source.CloseInput(true);
            }
            catch (Exception ex)
            {
                source.Dispose();
                return Fail($"Sheet template '{path}' could not be read: {ex.Message}", "Check that the file is a drawing or template AutoCAD can open.");
            }

            var result = Inspect(source, path, config, anchorBlockName, missing);
            if (result is null) { source.Dispose(); return Core.Volumes.Outcome<PaperTemplate>.Fail(missing); }
            return Core.Volumes.Outcome<PaperTemplate>.Ok(result, result.Notes);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static PaperTemplate? Inspect(Database source, string path, PaperConfig config, string anchorBlockName, List<Problem> missing)
    {
        void Missing(string what, string fix) => missing.Add(new Problem(PaperCodes.TemplateMissing, Severity.Error, what, Fix: fix));
        var notes = new List<Problem>();

        using var tr = source.TransactionManager.StartTransaction();
        var dict = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
        Layout? layout = null;
        var names = new List<string>();
        foreach (DBDictionaryEntry e in dict)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType) continue;
            names.Add(lay.LayoutName);
            if (string.Equals(lay.LayoutName, config.TemplateLayout, StringComparison.OrdinalIgnoreCase)) layout = lay;
        }
        if (layout is null)
        {
            Missing($"Sheet template '{path}' has no layout '{config.TemplateLayout}'. Its layouts: {(names.Count == 0 ? "none" : string.Join(", ", names))}.",
                "Correct templateLayout, or add the layout to the template.");
            tr.Commit();
            return null;
        }

        var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var viewports = new List<Viewport>();
        var anchors = new List<BlockReference>();
        var all = new List<ObjectId>();
        foreach (ObjectId id in block)
        {
            if (id.IsErased) continue;
            var obj = tr.GetObject(id, OpenMode.ForRead);
            all.Add(id);
            if (obj is Viewport vp) viewports.Add(vp);
            else if (obj is BlockReference br and not Table && string.Equals(EffectiveName(tr, br), anchorBlockName, StringComparison.OrdinalIgnoreCase)) anchors.Add(br);
        }

        var listed = layout.GetViewports();
        ObjectId overall = listed is { Count: > 0 } ? listed[0] : viewports.Count > 0 ? viewports[0].ObjectId : ObjectId.Null;
        var floating = viewports.Where(v => v.ObjectId != overall).ToList();

        Viewport? plan = null;
        bool byLayer = false;
        var onLayer = floating.Where(v => string.Equals(v.Layer, config.PlanViewportLayer, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onLayer.Count == 1) { plan = onLayer[0]; byLayer = true; }
        else if (onLayer.Count > 1)
            Missing($"Template layout '{layout.LayoutName}' has {onLayer.Count} floating viewports on layer '{config.PlanViewportLayer}' ({string.Join(", ", onLayer.Select(v => v.Handle.ToString()))}); exactly one may be the plan viewport.",
                $"Leave one viewport on '{config.PlanViewportLayer}' in the template.");
        else if (floating.Count == 0)
            Missing($"Template layout '{layout.LayoutName}' has no plan viewport: no floating viewport on layer '{config.PlanViewportLayer}' and no floating viewport at all.",
                $"Add a floating viewport on layer '{config.PlanViewportLayer}' to the template layout.");
        else
        {
            var bySize = floating.OrderByDescending(v => v.Width * v.Height).ToList();
            double a0 = bySize[0].Width * bySize[0].Height;
            if (bySize.Count > 1 && Math.Abs(bySize[1].Width * bySize[1].Height - a0) <= 1e-12 * Math.Max(1.0, a0))
                Missing($"Template layout '{layout.LayoutName}' has no viewport on layer '{config.PlanViewportLayer}' and its two largest floating viewports ({bySize[0].Handle}, {bySize[1].Handle}) are the same size; the plan viewport cannot be chosen.",
                    $"Put the plan viewport on layer '{config.PlanViewportLayer}'.");
            else
            {
                plan = bySize[0];
                notes.Add(new Problem(PaperCodes.OtherViewports, Severity.Info,
                    $"Template layout '{layout.LayoutName}' has no viewport on layer '{config.PlanViewportLayer}'; the largest floating viewport ({plan.Handle}, {F(plan.Width)} x {F(plan.Height)}) is the plan viewport."));
            }
        }

        Pt anchor = default;
        bool anchorFromBlock = false;
        ObjectId anchorRef = ObjectId.Null;
        if (anchors.Count == 1) { anchor = new Pt(anchors[0].Position.X, anchors[0].Position.Y); anchorFromBlock = true; anchorRef = anchors[0].ObjectId; }
        else if (anchors.Count > 1)
            Missing($"Template layout '{layout.LayoutName}' has {anchors.Count} '{anchorBlockName}' references; one table anchor is expected.",
                $"Leave one '{anchorBlockName}' reference in the template layout.");
        else if (config.TablePaperPoint is Pt tp) anchor = tp;
        else
            Missing($"Template layout '{layout.LayoutName}' has no '{anchorBlockName}' block reference and paper config tablePaperPoint is not set: the table anchor is missing.",
                $"Insert '{anchorBlockName}' at the table's top-left corner in the template, or set tablePaperPoint.");

        if (missing.Count > 0 || plan is null) { tr.Commit(); return null; }

        var others = floating.Where(v => v.ObjectId != plan.ObjectId).Select(v => v.Handle.ToString()).ToList();
        if (others.Count > 0)
            notes.Add(new Problem(PaperCodes.OtherViewports, Severity.Info,
                $"Template layout '{layout.LayoutName}' has {others.Count} other floating viewport(s) ({string.Join(", ", others)}); they are cloned and never driven."));

        var cloneIds = all.Where(id => id != overall).ToList();
        var othersOn = floating.Where(v => v.ObjectId != plan.ObjectId && v.On).Select(v => v.ObjectId).ToList();
        var result = new PaperTemplate(source, path, layout.LayoutName, layout.ObjectId, overall, plan.ObjectId, anchorRef, cloneIds, othersOn,
            plan.Width, plan.Height, new Pt(plan.CenterPoint.X, plan.CenterPoint.Y), plan.Layer, byLayer,
            anchor, anchorFromBlock, others, notes);
        tr.Commit();
        return result;

        static string F(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static Core.Volumes.Outcome<PaperTemplate> Fail(string message, string fix)
        => Core.Volumes.Outcome<PaperTemplate>.Fail(new Problem(PaperCodes.TemplateMissing, Severity.Error, message, Fix: fix));

    /// <summary>Block name of a reference, through the dynamic block definition when there is one.</summary>
    internal static string EffectiveName(Transaction tr, BlockReference br)
    {
        var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
    }
}
