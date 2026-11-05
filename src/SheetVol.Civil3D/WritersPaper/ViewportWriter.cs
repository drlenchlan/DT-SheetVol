// A3 work package. Owner: A3.
//
// Plan viewport view, annotation scale and clip (HANDOFF.md 6.6, 6.7, P12, P17).
//
// The mapping S2 measured (docs/api-findings.md P12, 8 of 8 against acedTrans):
//   paper = vp.CenterPoint + CustomScale * (R(+TwistAngle) * (w - ViewTarget) - ViewCenter)
// with ViewCenter in display coordinates. C3's convention (Core/Views/ViewMath.cs) is
//   paper - A = Rotate(w - ModelCenter, twist) / ModelUnitsPerPaperUnit
// where A is the paper point the model centre lands on. With ViewTarget = ModelCenter and
// CustomScale = 1 / ModelUnitsPerPaperUnit the two agree exactly when TwistAngle = twist
// (mod 2 pi): the signs are the same, nothing to absorb. AutoCAD reports TwistAngle in
// [0, 2 pi), so a C3 twist of -90 degrees is written as 270 degrees and every comparison
// is made modulo 2 pi.
//
// The anchor A. A fresh viewport (the new viewport that replaces the template's clone in a
// new layout, or one recreated after the user erased it) puts the model centre at its own
// centre, so A = CenterPoint and ViewCenter = (0, 0) as 6.6 says.
// An existing viewport keeps the paper point its current view centre lands on:
//   A = CenterPoint - CustomScale * ViewCenter
// The anchor moves with the viewport when the user moves it (MOVE takes the viewport and its
// clip), and stays put when a clip is assigned (P12: AutoCAD moved CenterPoint to the clip's
// box and shifted ViewCenter by the paper offset / scale). The new view is then written with
//   ViewCenter = (CenterPoint - A) / CustomScale
// so a regen never re-applies ViewCenter = (0, 0) to a moved or clipped viewport (E608). When
// the writer creates or edits a clip it also sets the viewport to the clip's box itself, in
// the same transaction (FitToClip): an in-place edit of an existing clip polyline is refitted
// by AutoCAD without the ViewCenter shift (MEASURED), which moved the model centre 0.5 paper
// units off A before this was added.
// NOTE: docs/api-findings.md B.2 rule 4 prints this as (A - CenterPoint) / CustomScale; the
// sign is reversed there. P12A's own numbers (CenterPoint 9.1, A 8.5, ViewCenter +30 at
// 1:50) and the mapping above give (CenterPoint - A) / CustomScale.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>The P12 mapping and angle helpers, public so A4's self-test can check a written viewport.</summary>
public static class PaperViewMath
{
    /// <summary>An angle in [0, 2 pi), radians.</summary>
    public static double Mod2Pi(double radians)
    {
        double r = radians % Math.Tau;
        if (r < 0) r += Math.Tau;
        return r >= Math.Tau ? 0 : r;
    }

    /// <summary>Smallest absolute difference between two angles, radians, in [0, pi].</summary>
    public static double AngleDifference(double a, double b)
    {
        double d = Mod2Pi(a - b);
        return d > Math.PI ? Math.Tau - d : d;
    }

    /// <summary>
    /// Where model point <paramref name="w"/> lands on paper through viewport <paramref name="vp"/>,
    /// from its stored values: CenterPoint + CustomScale (R(+TwistAngle)(w - ViewTarget) - ViewCenter).
    /// </summary>
    public static Pt ModelToPaper(Viewport vp, Pt w)
    {
        ArgumentNullException.ThrowIfNull(vp);
        double s = vp.CustomScale, t = vp.TwistAngle;
        double dx = w.X - vp.ViewTarget.X, dy = w.Y - vp.ViewTarget.Y;
        double c = Math.Cos(t), sn = Math.Sin(t);
        double ex = c * dx - sn * dy, ey = sn * dx + c * dy;
        return new Pt(vp.CenterPoint.X + s * (ex - vp.ViewCenter.X), vp.CenterPoint.Y + s * (ey - vp.ViewCenter.Y));
    }

    /// <summary>The paper point the viewport's view target lands on: CenterPoint - CustomScale * ViewCenter.</summary>
    public static Pt Anchor(Viewport vp)
    {
        ArgumentNullException.ThrowIfNull(vp);
        return new Pt(vp.CenterPoint.X - vp.CustomScale * vp.ViewCenter.X, vp.CenterPoint.Y - vp.CustomScale * vp.ViewCenter.Y);
    }

    /// <summary>Ratio of an annotation scale, model (drawing) units per paper unit, or NaN.</summary>
    internal static double Ratio(AnnotationScale? a)
        => a is null || !(a.PaperUnits > 0) ? double.NaN : a.DrawingUnits / a.PaperUnits;

    /// <summary>The viewport's annotation scale, or null when AutoCAD cannot report one.</summary>
    internal static AnnotationScale? SafeAnnotationScale(Viewport vp)
    {
        try { return vp.AnnotationScale; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }
}

/// <summary>Applies a <see cref="ViewSpec"/> to a plan viewport and its clip. Used by <see cref="PaperWriter"/>.</summary>
internal sealed class ViewportWriter(PaperConfig config)
{
    private const string AnnotationScales = "ACDB_ANNOTATIONSCALES";

    /// <summary>The paper point the model centre must land on. See the file header.</summary>
    internal static Pt AnchorFor(Viewport vp, bool fresh)
        => fresh ? new Pt(vp.CenterPoint.X, vp.CenterPoint.Y) : PaperViewMath.Anchor(vp);

    /// <summary>The annotation scale whose ratio is <paramref name="modelUnitsPerPaperUnit"/>, looked up by ratio, never by name (P17), or null.</summary>
    internal AnnotationScale? FindScale(Database db, double modelUnitsPerPaperUnit)
    {
        var occ = db.ObjectContextManager.GetContextCollection(AnnotationScales);
        foreach (ObjectContext oc in occ)
            if (oc is AnnotationScale a && RelativeDifference(PaperViewMath.Ratio(a), modelUnitsPerPaperUnit) <= config.ScaleRelativeTolerance)
                return a;
        return null;
    }

    /// <summary>The problem when a scale of this ratio would have to be added under a name another ratio already uses (E605), else null.</summary>
    internal Problem? ScaleNameCollision(Database db, double modelUnitsPerPaperUnit, Guid segmentId)
    {
        if (FindScale(db, modelUnitsPerPaperUnit) is not null) return null;
        string name = config.AnnotationScaleName(modelUnitsPerPaperUnit);
        var occ = db.ObjectContextManager.GetContextCollection(AnnotationScales);
        if (!occ.HasContext(name)) return null;
        return new Problem(PaperCodes.AnnotationScale, Severity.Error,
            $"Annotation scale 1:{modelUnitsPerPaperUnit:R} is missing and must be added, but the name '{name}' is already used by a scale of another ratio.",
            segmentId, "Rename that scale in the drawing, or change annotationScaleNameFormat.");
    }

    /// <summary>True when the live view already realizes <paramref name="spec"/>: plan direction, target, twist (mod 2 pi), CustomScale and annotation scale ratio.</summary>
    internal bool ViewMatches(Viewport vp, ViewSpec spec)
    {
        var dir = vp.ViewDirection;
        if (dir.Length == 0 || !dir.GetNormal().IsEqualTo(Vector3d.ZAxis, new Tolerance(1e-12, 1e-12))) return false;
        var t = vp.ViewTarget;
        double dx = t.X - spec.ModelCenter.X, dy = t.Y - spec.ModelCenter.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > config.ModelTolerance || Math.Abs(t.Z) > config.ModelTolerance) return false;
        if (PaperViewMath.AngleDifference(vp.TwistAngle, spec.TwistRadians) > config.AngleTolerance) return false;
        double k = spec.ModelUnitsPerPaperUnit;
        if (RelativeDifference(vp.CustomScale, 1.0 / k) > config.ScaleRelativeTolerance) return false;
        if (RelativeDifference(PaperViewMath.Ratio(PaperViewMath.SafeAnnotationScale(vp)), k) > config.ScaleRelativeTolerance) return false;
        return true;
    }

    /// <summary>
    /// Writes the view (target, twist, CustomScale, annotation scale, ViewCenter from the
    /// anchor) unless it already matches. A locked viewport is unlocked, updated and relocked
    /// (E604). Never touches CenterPoint, Width, Height, On or layer freezes (E608, E609, E614).
    /// </summary>
    internal PaperChange ApplyView(Transaction tr, Database db, ObjectId vpId, ViewSpec spec, Pt anchor, bool fresh,
                                   WriteCounter writes, List<Problem> problems, string layoutName)
    {
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        if (!fresh && ViewMatches(vp, spec)) return PaperChange.Unchanged;

        double k = spec.ModelUnitsPerPaperUnit;
        var scale = FindScale(db, k);
        if (scale is null)
        {
            string name = config.AnnotationScaleName(k);
            var occ = db.ObjectContextManager.GetContextCollection(AnnotationScales);
            if (occ.HasContext(name))
                throw new PaperWriterException(PaperCodes.AnnotationScale, $"Annotation scale name '{name}' is taken by a scale of another ratio (validation should have refused this run).");
            occ.AddContext(new AnnotationScale { Name = name, PaperUnits = 1.0, DrawingUnits = k });
            writes.Add();
            scale = FindScale(db, k) ?? throw new PaperWriterException(PaperCodes.AnnotationScale, $"Annotation scale '{name}' was added but cannot be found by its ratio {k:R}.");
            problems.Add(new Problem(PaperCodes.AnnotationScale, Severity.Info, $"Annotation scale '{name}' (1:{k:R}) was missing from the drawing and was added.", spec.SegmentId));
        }

        vp = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
        writes.Add();
        bool wasLocked = vp.Locked;
        if (wasLocked) vp.Locked = false;

        vp.ViewDirection = Vector3d.ZAxis;
        vp.ViewTarget = new Point3d(spec.ModelCenter.X, spec.ModelCenter.Y, 0.0);
        vp.TwistAngle = PaperViewMath.Mod2Pi(spec.TwistRadians);
        vp.CustomScale = 1.0 / k;
        vp.AnnotationScale = scale;                      // independent of CustomScale (P17): set both
        var cp = vp.CenterPoint;
        vp.ViewCenter = new Point2d((cp.X - anchor.X) * k, (cp.Y - anchor.Y) * k);   // (CenterPoint - A) / CustomScale

        if (wasLocked)
        {
            vp.Locked = true;
            problems.Add(new Problem(PaperCodes.ViewportLocked, Severity.Info,
                $"Viewport {vp.Handle} on layout '{layoutName}' is locked; it was unlocked, updated and relocked.", spec.SegmentId));
        }
        return fresh ? PaperChange.Created : PaperChange.Updated;
    }

    /// <summary>The clip in absolute paper coordinates: A + ClipInPaper, with an explicit closing vertex dropped.</summary>
    internal IReadOnlyList<PolyVertex>? DesiredClip(ViewSpec spec, Pt anchor)
    {
        if (spec.ClipInPaper is null) return null;
        var v = spec.ClipInPaper.Select(p => new PolyVertex(new Pt(anchor.X + p.Point.X, anchor.Y + p.Point.Y), p.Bulge)).ToList();
        if (v.Count >= 2 && Distance(v[0].Point, v[^1].Point) <= config.PaperTolerance) v.RemoveAt(v.Count - 1);
        return v;
    }

    /// <summary>
    /// Creates, updates in place or leaves the viewport's non-rectangular clip so it is the
    /// planned polygon around <paramref name="anchor"/>. A clip refits the viewport to its
    /// bounding box at commit and AutoCAD shifts ViewCenter to keep the view fixed (P12).
    /// </summary>
    internal PaperChange ApplyClip(Transaction tr, Database db, ObjectId vpId, ViewSpec spec, Pt anchor, bool fresh,
                                   Guid runId, WriteCounter writes, List<Problem> problems, string layoutName, out ObjectId clipId)
    {
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        clipId = ObjectId.Null;
        var current = vp.NonRectClipEntityId;
        Polyline? pl = null;
        if (!current.IsNull && current.IsValid && !current.IsErased && tr.GetObject(current, OpenMode.ForRead) is Polyline p) { pl = p; clipId = current; }

        var desired = DesiredClip(spec, anchor);
        if (desired is null)
        {
            if (pl is null || !vp.NonRectClipOn) return PaperChange.None;
            var tag = OwnerTags.Read(tr, pl, config.OwnerTagKey);
            if (tag is null || tag.RunId != runId) { clipId = ObjectId.Null; return PaperChange.None; }   // not ours: left alone
            vp = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
            writes.Add();
            bool locked = vp.Locked;
            if (locked) vp.Locked = false;
            vp.NonRectClipOn = false;
            if (locked) vp.Locked = true;
            return PaperChange.Updated;
        }

        var ownerTag = new OwnerTag(runId, spec.SegmentId, OwnedRole.Clip, "", ContentSignatures.OfPolyline(spec.ClipInPaper!), []);
        PaperChange change;
        if (pl is not null && ClipMatches(pl, desired) && vp.NonRectClipOn)
        {
            change = PaperChange.Unchanged;
        }
        else if (pl is not null)
        {
            pl = OwnerTags.OpenForWrite<Polyline>(tr, clipId);
            writes.Add();
            SetVertices(pl, desired);
            if (!vp.NonRectClipOn)
            {
                vp = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
                bool locked = vp.Locked;
                if (locked) vp.Locked = false;
                vp.NonRectClipOn = true;
                if (locked) vp.Locked = true;
            }
            change = PaperChange.Updated;
        }
        else
        {
            var owner = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForWrite);
            pl = new Polyline();
            pl.SetDatabaseDefaults(db);
            pl.Layer = config.ClipLayer ?? vp.Layer;
            SetVertices(pl, desired);
            clipId = owner.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            writes.Add();
            vp = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
            bool locked = vp.Locked;
            if (locked) vp.Locked = false;
            vp.NonRectClipEntityId = clipId;
            vp.NonRectClipOn = true;
            if (locked) vp.Locked = true;
            change = PaperChange.Created;
            if (!fresh)
                problems.Add(new Problem(PaperCodes.OwnedObjectRecreated, Severity.Info,
                    $"Viewport {vp.Handle} on layout '{layoutName}' had no clip polyline; a new one was created.", spec.SegmentId));
        }

        if (change != PaperChange.Unchanged) FitToClip(tr, vpId, desired, anchor, spec.ModelUnitsPerPaperUnit);

        if (!OwnerTags.Matches(tr, pl, config.OwnerTagKey, ownerTag))
        {
            if (!pl.IsWriteEnabled) { pl = OwnerTags.OpenForWrite<Polyline>(tr, clipId); writes.Add(); }
            OwnerTags.Write(tr, pl, config.OwnerTagKey, ownerTag);
            if (change == PaperChange.Unchanged) change = PaperChange.Updated;
        }
        return change;
    }

    /// <summary>
    /// Sets the viewport to the clip's exact box (arcs included) and its ViewCenter so the model
    /// centre still lands on <paramref name="anchor"/>. MEASURED 2026-11-05: AutoCAD refits a
    /// clipped viewport to the clip's box at commit, but shifts ViewCenter to compensate only when
    /// the clip is assigned (P12), not when an existing clip polyline is edited in place: the
    /// model centre then drifted 0.5 paper units. Doing the refit here makes both cases exact,
    /// because AutoCAD's own refit then changes nothing. Setting Height keeps ViewHeight and so
    /// changes CustomScale (MEASURED: 20 % off), so CustomScale is set again after the size.
    /// </summary>
    private void FitToClip(Transaction tr, ObjectId vpId, IReadOnlyList<PolyVertex> clip, Pt anchor, double modelUnitsPerPaperUnit)
    {
        var box = Core.Views.BulgePolyline.Bounds(clip, new Core.Views.ViewMathConfig
        {
            StraightBulgeTolerance = config.BulgeTolerance,
            DuplicateVertexTolerance = config.PaperTolerance,
        });
        var vp = OwnerTags.OpenForWrite<Viewport>(tr, vpId);
        bool locked = vp.Locked;
        if (locked) vp.Locked = false;
        double k = modelUnitsPerPaperUnit;
        var c = box.Center;
        vp.CenterPoint = new Point3d(c.X, c.Y, 0.0);
        vp.Width = box.Width;
        vp.Height = box.Height;
        vp.CustomScale = 1.0 / k;
        vp.ViewCenter = new Point2d((c.X - anchor.X) * k, (c.Y - anchor.Y) * k);
        if (locked) vp.Locked = true;
    }

    private bool ClipMatches(Polyline pl, IReadOnlyList<PolyVertex> desired)
    {
        if (!pl.Closed || pl.NumberOfVertices != desired.Count) return false;
        if (Math.Abs(pl.Elevation) > config.PaperTolerance || !pl.Normal.IsEqualTo(Vector3d.ZAxis)) return false;
        for (int i = 0; i < desired.Count; i++)
        {
            var q = pl.GetPoint2dAt(i);
            if (Distance(new Pt(q.X, q.Y), desired[i].Point) > config.PaperTolerance) return false;
            if (Math.Abs(pl.GetBulgeAt(i) - desired[i].Bulge) > config.BulgeTolerance) return false;
        }
        return true;
    }

    private static void SetVertices(Polyline pl, IReadOnlyList<PolyVertex> v)
    {
        while (pl.NumberOfVertices > v.Count) pl.RemoveVertexAt(pl.NumberOfVertices - 1);
        for (int i = 0; i < v.Count; i++)
        {
            var pt = new Point2d(v[i].Point.X, v[i].Point.Y);
            if (i < pl.NumberOfVertices) { pl.SetPointAt(i, pt); pl.SetBulgeAt(i, v[i].Bulge); }
            else pl.AddVertexAt(i, pt, v[i].Bulge, 0, 0);
        }
        if (pl.Elevation != 0) pl.Elevation = 0;
        if (!pl.Normal.IsEqualTo(Vector3d.ZAxis)) pl.Normal = Vector3d.ZAxis;
        pl.Closed = true;
    }

    internal static double RelativeDifference(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) return double.PositiveInfinity;
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale == 0 ? 0 : Math.Abs(a - b) / scale;
    }

    internal static double Distance(Pt a, Pt b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Counts the objects a writer creates, erases or opens for write (E811 evidence).</summary>
internal sealed class WriteCounter
{
    public int Count { get; private set; }
    public void Add() => Count++;
}
