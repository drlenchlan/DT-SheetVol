// A2 model writers: boundary polylines, impacted-area outlines and match-line marks.
// Owner: A2. All in model space, WCS (HANDOFF.md 6.13).

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>
/// Closed polylines with bulges: segment boundaries from <see cref="Boundary.Drawn"/> and the
/// impact outline rings (rev 3, D13: one polyline per ring of <see cref="ImpactResult.Outlines"/>).
/// Updated in place while the handle is live (vertex count adjusted, points and bulges
/// rewritten), recreated when erased (E802), erased when no longer planned (E308).
/// </summary>
/// <typeparam name="TSpec"><see cref="Boundary"/> or <see cref="ImpactOutlineSpec"/>.</typeparam>
public sealed class PolylineWriter<TSpec> : OwnedWriter<TSpec, Polyline>
{
    private readonly OwnedRole _role;
    private readonly string _layerName;
    private readonly string _layerSetting;
    private readonly Func<TSpec, IReadOnlyList<PolyVertex>> _vertices;
    private readonly Func<TSpec, string?>? _inputCheck;
    private readonly string _what;
    private ObjectId _layerId;
    private string _layerActualName = "";

    /// <summary>A writer for one polyline role.</summary>
    /// <param name="ctx">The caller's context.</param>
    /// <param name="role">The owned role it writes.</param>
    /// <param name="what">Short name for messages.</param>
    /// <param name="layerName">Configured layer.</param>
    /// <param name="layerSetting">Setting key of the layer, for SV-E501.</param>
    /// <param name="vertices">The vertices of a spec.</param>
    /// <param name="inputCheck">Optional role-specific input check: a message makes the spec an SV-E541 error.</param>
    public PolylineWriter(ModelWriteContext ctx, OwnedRole role, string what, string layerName, string layerSetting,
        Func<TSpec, IReadOnlyList<PolyVertex>> vertices, Func<TSpec, string?>? inputCheck = null)
        : base(ctx)
    {
        _role = role;
        _what = what;
        _layerName = layerName;
        _layerSetting = layerSetting;
        _vertices = vertices;
        _inputCheck = inputCheck;
    }

    /// <inheritdoc/>
    protected override string What => _what;

    /// <inheritdoc/>
    public override bool Owns(OwnedKey key) => key.Role == _role;

    /// <inheritdoc/>
    protected override IEnumerable<Problem> Validate(OwnedKey key, TSpec spec)
    {
        var v = Normalize(_vertices(spec));
        if (v.Count < 3 || v.Select(p => p.Point).Distinct().Count() < 3)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"The {_what} for {Describe(key)} has fewer than 3 distinct vertices.", NullIfEmpty(key.SegmentId));
        else if (v.Any(p => !double.IsFinite(p.Point.X) || !double.IsFinite(p.Point.Y) || !double.IsFinite(p.Bulge)))
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"The {_what} for {Describe(key)} has a non-finite vertex or bulge.", NullIfEmpty(key.SegmentId));
        if (_inputCheck?.Invoke(spec) is { } message)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"The {_what} for {Describe(key)} {message}", NullIfEmpty(key.SegmentId));
    }

    /// <inheritdoc/>
    protected override void ResolveResources(IReadOnlyList<(OwnedKey Key, TSpec Spec)> planned, List<Problem> problems)
    {
        var id = DrawingNames.Layer(Tr, Db, _layerName, _layerSetting, problems);
        if (id is null) return;
        _layerId = id.Value;
        _layerActualName = ((LayerTableRecord)Tr.GetObject(_layerId, OpenMode.ForRead)).Name;
    }

    /// <inheritdoc/>
    protected override (string Planned, string? Live) Signatures(OwnedKey key, TSpec spec, Polyline? live, List<Problem> notes)
    {
        string planned = Signature(Normalize(_vertices(spec)), _layerActualName, closed: true);
        if (live is null) return (planned, null);
        var verts = new List<PolyVertex>(live.NumberOfVertices);
        for (int i = 0; i < live.NumberOfVertices; i++)
        {
            Point2d p = live.GetPoint2dAt(i);
            verts.Add(new PolyVertex(new Pt(p.X, p.Y), live.GetBulgeAt(i)));
        }
        return (planned, Signature(verts, live.Layer, live.Closed));
    }

    /// <inheritdoc/>
    protected override Polyline Create(OwnedKey key, TSpec spec, LayerGuard guard, List<Problem> notes)
    {
        var v = Normalize(_vertices(spec));
        var pl = new Polyline(v.Count);
        pl.SetDatabaseDefaults(Db);
        pl.LayerId = _layerId;
        for (int i = 0; i < v.Count; i++) pl.AddVertexAt(i, new Point2d(v[i].Point.X, v[i].Point.Y), v[i].Bulge, 0, 0);
        pl.Closed = true;
        guard.Allow(_layerId);
        var ms = (BlockTableRecord)Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);
        ms.AppendEntity(pl);
        Tr.AddNewlyCreatedDBObject(pl, true);
        return pl;
    }

    /// <inheritdoc/>
    protected override void Update(Polyline pl, OwnedKey key, TSpec spec, List<Problem> notes)
    {
        var v = Normalize(_vertices(spec));
        while (pl.NumberOfVertices < v.Count) pl.AddVertexAt(pl.NumberOfVertices, new Point2d(v[pl.NumberOfVertices].Point.X, v[pl.NumberOfVertices].Point.Y), 0, 0, 0);
        while (pl.NumberOfVertices > v.Count) pl.RemoveVertexAt(pl.NumberOfVertices - 1);
        for (int i = 0; i < v.Count; i++)
        {
            pl.SetPointAt(i, new Point2d(v[i].Point.X, v[i].Point.Y));
            pl.SetBulgeAt(i, v[i].Bulge);
            pl.SetStartWidthAt(i, 0);
            pl.SetEndWidthAt(i, 0);
        }
        pl.Closed = true;
        if (pl.LayerId != _layerId) pl.LayerId = _layerId;
    }

    /// <summary>The vertices as drawn: a repeated closing vertex is dropped (the polyline is closed instead).</summary>
    public static IReadOnlyList<PolyVertex> Normalize(IReadOnlyList<PolyVertex> v)
        => v.Count > 1 && v[0].Point == v[^1].Point ? v.Take(v.Count - 1).ToList() : v;

    private static string Signature(IReadOnlyList<PolyVertex> vertices, string layer, bool closed)
        => ContentSignatures.OfTexts([ContentSignatures.OfPolyline(vertices), layer, closed ? "closed" : "open"]);
}

/// <summary>The line of a match mark: from the left to the right offset point at the break.</summary>
public sealed class MatchLineWriter : OwnedWriter<MatchMarkSpec, Line>
{
    private ObjectId _layerId;
    private string _layerName = "";

    /// <summary>A writer in the caller's context.</summary>
    public MatchLineWriter(ModelWriteContext ctx) : base(ctx) { }

    /// <inheritdoc/>
    protected override string What => "match line";

    /// <inheritdoc/>
    public override bool Owns(OwnedKey key)
        => key.Role == OwnedRole.MatchMark && key.Qualifier is OwnedQualifiers.StartLine or OwnedQualifiers.EndLine;

    /// <inheritdoc/>
    protected override IEnumerable<Problem> Validate(OwnedKey key, MatchMarkSpec spec) => MatchMarkChecks.Validate(key, spec);

    /// <inheritdoc/>
    protected override void ResolveResources(IReadOnlyList<(OwnedKey Key, MatchMarkSpec Spec)> planned, List<Problem> problems)
    {
        var id = DrawingNames.Layer(Tr, Db, Ctx.Config.MatchMarks.Layer, "modelWriters.matchMarks.layer", problems);
        if (id is null) return;
        _layerId = id.Value;
        _layerName = ((LayerTableRecord)Tr.GetObject(_layerId, OpenMode.ForRead)).Name;
    }

    /// <inheritdoc/>
    protected override (string Planned, string? Live) Signatures(OwnedKey key, MatchMarkSpec spec, Line? live, List<Problem> notes)
    {
        string planned = Signature(spec.Left, spec.Right, _layerName);
        return live is null ? (planned, null)
            : (planned, Signature(new Pt(live.StartPoint.X, live.StartPoint.Y), new Pt(live.EndPoint.X, live.EndPoint.Y), live.Layer));
    }

    /// <inheritdoc/>
    protected override Line Create(OwnedKey key, MatchMarkSpec spec, LayerGuard guard, List<Problem> notes)
    {
        var line = new Line(new Point3d(spec.Left.X, spec.Left.Y, 0), new Point3d(spec.Right.X, spec.Right.Y, 0));
        line.SetDatabaseDefaults(Db);
        line.LayerId = _layerId;
        guard.Allow(_layerId);
        var ms = (BlockTableRecord)Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);
        ms.AppendEntity(line);
        Tr.AddNewlyCreatedDBObject(line, true);
        return line;
    }

    /// <inheritdoc/>
    protected override void Update(Line line, OwnedKey key, MatchMarkSpec spec, List<Problem> notes)
    {
        line.StartPoint = new Point3d(spec.Left.X, spec.Left.Y, 0);
        line.EndPoint = new Point3d(spec.Right.X, spec.Right.Y, 0);
        if (line.LayerId != _layerId) line.LayerId = _layerId;
    }

    private static string Signature(Pt a, Pt b, string layer)
        => ContentSignatures.OfTexts(["line", ContentSignatures.OfPolyline([new PolyVertex(a, 0), new PolyVertex(b, 0)]), layer]);
}

/// <summary>
/// The text of a match mark: <c>MATCH LINE STA x — SEE SHEET n</c> (template from config),
/// centred on the line, offset to the segment's own side, rotated along the line and kept
/// readable. Text height and gap are paper units times the segment's view scale.
/// </summary>
public sealed class MatchTextWriter : OwnedWriter<MatchMarkSpec, MText>
{
    private ObjectId _layerId, _styleId;
    private string _layerName = "", _styleName = "";

    /// <summary>A writer in the caller's context.</summary>
    public MatchTextWriter(ModelWriteContext ctx) : base(ctx) { }

    /// <inheritdoc/>
    protected override string What => "match text";

    /// <inheritdoc/>
    public override bool Owns(OwnedKey key)
        => key.Role == OwnedRole.MatchMark && key.Qualifier is OwnedQualifiers.StartText or OwnedQualifiers.EndText;

    /// <inheritdoc/>
    protected override IEnumerable<Problem> Validate(OwnedKey key, MatchMarkSpec spec) => MatchMarkChecks.Validate(key, spec);

    /// <inheritdoc/>
    protected override void ResolveResources(IReadOnlyList<(OwnedKey Key, MatchMarkSpec Spec)> planned, List<Problem> problems)
    {
        var m = Ctx.Config.MatchMarks;
        var layer = DrawingNames.Layer(Tr, Db, m.Layer, "modelWriters.matchMarks.layer", problems);
        var style = DrawingNames.TextStyle(Tr, Db, m.TextStyle, "modelWriters.matchMarks.textStyle", problems);
        if (layer is null || style is null) return;
        _layerId = layer.Value;
        _styleId = style.Value;
        _layerName = ((LayerTableRecord)Tr.GetObject(_layerId, OpenMode.ForRead)).Name;
        _styleName = ((TextStyleTableRecord)Tr.GetObject(_styleId, OpenMode.ForRead)).Name;
    }

    /// <summary>Where and how the text sits: location, direction (unit vector), height.</summary>
    public static (Pt Location, Pt Direction, double Height) Layout(MatchMarkSpec spec, MatchMarkConfig config)
    {
        double height = config.TextHeight * spec.ModelUnitsPerPaperUnit;
        double offset = config.TextGap * spec.ModelUnitsPerPaperUnit + height / 2;
        var mid = new Pt((spec.Left.X + spec.Right.X) / 2, (spec.Left.Y + spec.Right.Y) / 2);
        var loc = new Pt(mid.X + spec.Inside.X * offset, mid.Y + spec.Inside.Y * offset);
        double angle = Math.Atan2(spec.Right.Y - spec.Left.Y, spec.Right.X - spec.Left.X);
        if (angle > Math.PI / 2) angle -= Math.PI;            // keep the text readable: (-90°, 90°]
        else if (angle <= -Math.PI / 2) angle += Math.PI;
        return (loc, new Pt(Math.Cos(angle), Math.Sin(angle)), height);
    }

    /// <inheritdoc/>
    protected override (string Planned, string? Live) Signatures(OwnedKey key, MatchMarkSpec spec, MText? live, List<Problem> notes)
    {
        var (loc, dir, h) = Layout(spec, Ctx.Config.MatchMarks);
        string planned = Signature(Escape(spec.Text), loc, dir, h, _styleName, _layerName, AttachmentPoint.MiddleCenter);
        if (live is null) return (planned, null);
        string liveStyle = ((TextStyleTableRecord)Tr.GetObject(live.TextStyleId, OpenMode.ForRead)).Name;
        return (planned, Signature(live.Contents, new Pt(live.Location.X, live.Location.Y), new Pt(live.Direction.X, live.Direction.Y),
            live.TextHeight, liveStyle, live.Layer, live.Attachment));
    }

    /// <inheritdoc/>
    protected override MText Create(OwnedKey key, MatchMarkSpec spec, LayerGuard guard, List<Problem> notes)
    {
        var t = new MText();
        t.SetDatabaseDefaults(Db);
        Apply(t, spec);
        guard.Allow(_layerId);
        var ms = (BlockTableRecord)Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);
        ms.AppendEntity(t);
        Tr.AddNewlyCreatedDBObject(t, true);
        return t;
    }

    /// <inheritdoc/>
    protected override void Update(MText t, OwnedKey key, MatchMarkSpec spec, List<Problem> notes) => Apply(t, spec);

    private void Apply(MText t, MatchMarkSpec spec)
    {
        var (loc, dir, h) = Layout(spec, Ctx.Config.MatchMarks);
        t.LayerId = _layerId;
        t.TextStyleId = _styleId;
        t.Attachment = AttachmentPoint.MiddleCenter;
        t.Width = 0;
        t.TextHeight = h;
        t.Location = new Point3d(loc.X, loc.Y, 0);
        t.Direction = new Vector3d(dir.X, dir.Y, 0);
        t.Contents = Escape(spec.Text);
    }

    /// <summary>MTEXT escaping so the text is shown literally.</summary>
    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

    // Direction and location are read back through AutoCAD's own normalisation; 12 significant
    // digits keep the comparison stable across that round trip without hiding a real edit.
    private static string Signature(string contents, Pt loc, Pt dir, double height, string style, string layer, AttachmentPoint attachment)
        => ContentSignatures.OfTexts(["mtext", contents, G(loc.X), G(loc.Y), G(dir.X), G(dir.Y), G(height), style, layer, attachment.ToString()]);

    private static string G(double v) => (Math.Abs(v) < 1e-300 ? 0.0 : v).ToString("G12", CultureInfo.InvariantCulture);
}

/// <summary>Input checks shared by the two match-mark writers.</summary>
internal static class MatchMarkChecks
{
    public static IEnumerable<Problem> Validate(OwnedKey key, MatchMarkSpec spec)
    {
        bool finite = double.IsFinite(spec.Left.X) && double.IsFinite(spec.Left.Y) && double.IsFinite(spec.Right.X) && double.IsFinite(spec.Right.Y)
                      && double.IsFinite(spec.Inside.X) && double.IsFinite(spec.Inside.Y);
        if (!finite || spec.Left == spec.Right)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Match mark {key.Qualifier} of segment {key.SegmentId:D} has no length or a non-finite point.", key.SegmentId);
        if (!double.IsFinite(spec.ModelUnitsPerPaperUnit) || spec.ModelUnitsPerPaperUnit <= 0)
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Match mark {key.Qualifier} of segment {key.SegmentId:D} has an invalid view scale.", key.SegmentId);
        if (string.IsNullOrWhiteSpace(spec.Text))
            yield return new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Match mark {key.Qualifier} of segment {key.SegmentId:D} has no text.", key.SegmentId);
    }
}
