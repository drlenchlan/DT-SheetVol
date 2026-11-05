// S3 probe P21: the database and graphics work the SVPROBEUI window runs from its buttons.
// Each method does one thing and returns a line for the log; the window decides the route
// (no lock, LockDocument, ExecuteInCommandContextAsync, SendStringToExecute) and logs the
// document state around it. All database access is inside a transaction (6.13).

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using SheetVol.Civil3D.SelfTest;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using GiTransientManager = Autodesk.AutoCAD.GraphicsInterface.TransientManager;
using GiTransientDrawingMode = Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode;
using DbPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace SheetVol.UI.Probe;

internal static class ProbeUiWork
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The entity picked for the highlight tests (E911). Reset by <see cref="Forget"/>
    /// before its drawing closes: an ObjectId of a destroyed database must never be read.</summary>
    public static ObjectId Picked { get; private set; } = ObjectId.Null;

    private static ObjectId _highlighted = ObjectId.Null;
    private static DbPolyline? _transient;
    private static Database? _transientDb;

    /// <summary>Transients that could not be erased (their drawing was not current, or it
    /// closed). Kept referenced so the GC never finalizes a drawable AutoCAD still holds.</summary>
    private static readonly List<DbPolyline> KeepAlive = new();

    /// <summary>Everything that decides whether database work is allowed right now.</summary>
    public static string State(Document? doc)
    {
        if (doc is null) return "no document";
        var dm = CoreApp.DocumentManager;
        return $"doc='{Path.GetFileName(doc.Name)}' LockMode()={S(() => doc.LockMode())} LockMode(includeMyLocks)={S(() => doc.LockMode(true))} " +
               $"CommandInProgress='{S(() => doc.CommandInProgress)}' IsApplicationContext={S(() => dm.IsApplicationContext)} " +
               $"Editor.IsQuiescent={S(() => doc.Editor.IsQuiescent)} CMDACTIVE={ProbeCCommands.SysVar("CMDACTIVE")} thread={Environment.CurrentManagedThreadId}";
    }

    /// <summary>Writes one Xrecord under NOD/SHEETVOL_PROBE/P21_&lt;route&gt;. Opening the NOD
    /// for write is where a missing document lock shows up.</summary>
    public static string WriteRecord(Document doc, string route)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        DBDictionary dict;
        if (nod.Contains(ProbeUiSettings.ProbeDictionary))
            dict = (DBDictionary)tr.GetObject(nod.GetAt(ProbeUiSettings.ProbeDictionary), OpenMode.ForWrite);
        else
        {
            dict = new DBDictionary();
            nod.SetAt(ProbeUiSettings.ProbeDictionary, dict);
            tr.AddNewlyCreatedDBObject(dict, true);
        }
        string key = ProbeUiSettings.EntryPrefix + route;
        string stamp = DateTime.Now.ToString("O", Inv);
        using var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, route), new TypedValue((int)DxfCode.Text, stamp));
        if (dict.Contains(key))
        {
            var x = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForWrite);
            x.Data = data;
        }
        else
        {
            var x = new Xrecord { Data = data };
            dict.SetAt(key, x);
            tr.AddNewlyCreatedDBObject(x, true);
        }
        int count = dict.Count;
        tr.Commit();
        return $"wrote {ProbeUiSettings.ProbeDictionary}/{key} = {stamp}; the dictionary now holds {count} entr{(count == 1 ? "y" : "ies")}";
    }

    /// <summary>Prompts for an entity. Needs command context (run from a command or from
    /// ExecuteInCommandContextAsync).</summary>
    public static string Pick(Editor ed, out PromptStatus status)
    {
        var res = ed.GetEntity(new PromptEntityOptions("\nSVPROBEUI: pick any entity to highlight: "));
        status = res.Status;
        if (res.Status != PromptStatus.OK) return $"pick ended with {res.Status}";
        Picked = res.ObjectId;
        return $"picked {Describe(res.ObjectId)}";
    }

    /// <summary>Why the picked entity cannot be used in this drawing, or null when it can.</summary>
    private static string? Unusable(Document doc) =>
        Picked.IsNull ? "nothing picked (use Pick entity first)"
        : Picked.Database != doc.Database ? "the picked entity belongs to another drawing; pick again in this one"
        : Picked.IsErased ? "the picked entity was erased; pick again"
        : null;

    /// <summary>Entity.Highlight() on the picked entity, under a document lock.</summary>
    public static string Highlight(Document doc)
    {
        if (Unusable(doc) is { } why) return why;
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var ent = (Entity)tr.GetObject(Picked, OpenMode.ForRead);
            ent.Highlight();
            tr.Commit();
        }
        _highlighted = Picked;
        doc.Editor.UpdateScreen();
        return $"Entity.Highlight() on {Describe(Picked)}; Editor.UpdateScreen() called";
    }

    /// <summary>A transient closed polyline around the picked entity's extents.</summary>
    public static string Transient(Document doc)
    {
        if (Unusable(doc) is { } why) return why;
        string previous = ClearTransient(doc);
        Extents3d ext;
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            ext = ((Entity)tr.GetObject(Picked, OpenMode.ForRead)).GeometricExtents;
            tr.Commit();
        }
        double pad = Math.Max(ext.MinPoint.DistanceTo(ext.MaxPoint) * ProbeUiSettings.TransientPadFraction, 1e-3);
        double x0 = ext.MinPoint.X - pad, y0 = ext.MinPoint.Y - pad, x1 = ext.MaxPoint.X + pad, y1 = ext.MaxPoint.Y + pad;
        var pl = new DbPolyline();
        pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
        pl.Closed = true;
        pl.ColorIndex = ProbeUiSettings.TransientColorIndex;
        bool added = GiTransientManager.CurrentTransientManager.AddTransient(pl, GiTransientDrawingMode.DirectTopmost,
            ProbeUiSettings.TransientSubDrawingMode, new IntegerCollection());
        if (added) { _transient = pl; _transientDb = doc.Database; } else pl.Dispose();
        doc.Editor.UpdateScreen();
        return $"{(previous.Length > 0 ? previous + "; " : "")}TransientManager.AddTransient(DirectTopmost) returned {added}; outline ({F(x0)}, {F(y0)})..({F(x1)}, {F(y1)}) around {Describe(Picked)}";
    }

    /// <summary>Unhighlight and erase the transient; safe to call when nothing is shown.
    /// <paramref name="anything"/> says whether a highlight or an outline was showing.</summary>
    public static string Clear(Document? doc, out bool anything)
    {
        anything = !_highlighted.IsNull || _transient is not null;
        var parts = new List<string>();
        if (_highlighted.IsNull) parts.Add("no highlight shown");
        else if (doc is not null && _highlighted.Database == doc.Database && !_highlighted.IsErased)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                ((Entity)tr.GetObject(_highlighted, OpenMode.ForRead)).Unhighlight();
                tr.Commit();
            }
            parts.Add($"Unhighlight() on {_highlighted.Handle}");
            _highlighted = ObjectId.Null;
        }
        else parts.Add($"highlight on {_highlighted.Handle} not cleared: its drawing is not the active one, or the entity was erased");
        string t = ClearTransient(doc);
        parts.Add(t.Length > 0 ? t : "no transient shown");
        doc?.Editor.UpdateScreen();
        return string.Join("; ", parts);
    }

    /// <summary>Erases the transient when its drawing is the current one. Otherwise the
    /// drawable stays referenced (KeepAlive) so it is never finalized while AutoCAD holds it.</summary>
    private static string ClearTransient(Document? doc)
    {
        if (_transient is null) return "";
        if (doc is null || _transientDb != doc.Database)
        {
            KeepAlive.Add(_transient);
            _transient = null;
            _transientDb = null;
            return "transient belongs to a drawing that is not current: not erased, kept referenced";
        }
        bool erased = GiTransientManager.CurrentTransientManager.EraseTransient(_transient, new IntegerCollection());
        if (erased) _transient.Dispose(); else KeepAlive.Add(_transient);
        _transient = null;
        _transientDb = null;
        return $"EraseTransient returned {erased}{(erased ? "" : " (drawable kept referenced, not disposed)")}";
    }

    /// <summary>Drops every id and drawable that belongs to <paramref name="db"/>. Called from
    /// DocumentToBeDestroyed, while the database is still alive, and on window close.</summary>
    public static string Forget(Database? db)
    {
        var parts = new List<string>();
        if (!Picked.IsNull && (db is null || Picked.Database == db)) { parts.Add($"picked {Picked.Handle}"); Picked = ObjectId.Null; }
        if (!_highlighted.IsNull && (db is null || _highlighted.Database == db)) { parts.Add($"highlight {_highlighted.Handle}"); _highlighted = ObjectId.Null; }
        if (_transient is not null && (db is null || _transientDb == db))
        {
            KeepAlive.Add(_transient);
            _transient = null;
            _transientDb = null;
            parts.Add("transient (kept referenced)");
        }
        return parts.Count == 0 ? "nothing to forget" : "forgot " + string.Join(", ", parts);
    }

    private static string Describe(ObjectId id) => $"{id.ObjectClass?.Name} {id.Handle}";

    private static string F(double d) => d.ToString("0.###", Inv);

    private static string S(Func<object?> read)
    {
        try { return read()?.ToString() ?? "null"; }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }
}

/// <summary>DPI facts WPF does not expose (E909): per-window DPI and the DPI awareness that
/// AutoCAD runs its UI thread and this window under.</summary>
internal static class ProbeUiNative
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    public static string DpiForWindow(IntPtr hwnd)
    {
        try { return hwnd == IntPtr.Zero ? "no handle" : GetDpiForWindow(hwnd).ToString(CultureInfo.InvariantCulture); }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    public static string ThreadAwareness()
    {
        try { return Name(GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())); }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    public static string WindowAwareness(IntPtr hwnd)
    {
        try { return hwnd == IntPtr.Zero ? "no handle" : Name(GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd))); }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    private static string Name(int awareness) => awareness switch
    {
        0 => "Unaware",
        1 => "SystemAware",
        2 => "PerMonitorAware",
        _ => $"Invalid({awareness})",
    };
}
