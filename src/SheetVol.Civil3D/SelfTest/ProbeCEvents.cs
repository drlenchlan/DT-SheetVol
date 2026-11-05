// S3 probe P15: which events fire for grip edits, match-line moves, alignment edits and
// reference synchronize (HANDOFF.md 5.3, 6.11). Core Console safe.
//
// SVPROBEEVENTS      subscribe (every open document, and documents activated later)
// SVPROBEEVENTSOFF   unsubscribe; prints what is still buffered and the session totals
// SVPROBEEVENTSDUMP  prints the events recorded since the last dump, then clears the buffer
// SVPROBEMARK        writes a marker line into the log ("grip PI", "synchronize", ...)
// SVPROBEC_EDIT      one API edit per command on FX fixtures, for the headless pre-check:
//                    PI | EQ | VTXDEL | VTXADD | APPEND | ERASE
//
// Every event goes to %TEMP%\sheetvol\probeEvents.log as it fires. Handlers only record:
// they never open other objects or write to the editor (the 6.11 rule for the real wiring).
// Civil 3D's own API declares no public events (AeccDbMgd and AeccDataShortcutMgd, 2025 and
// 2026, read from assembly metadata; SVPROBEEVENTS re-checks the loaded AeccDbMgd at run
// time), so Civil objects are observed through the AutoCAD Database events by class name.

using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using static SheetVol.Civil3D.SelfTest.ProbeCSettings;

namespace SheetVol.Civil3D.SelfTest;

public static partial class ProbeCCommands
{
    /// <summary>Recorder state for one SVPROBEEVENTS session. Static because AutoCAD event
    /// handlers outlive the command that attached them.</summary>
    private static class Rec
    {
        public static readonly object Gate = new();
        public static ProbeCLog? Log;
        public static bool On;
        public static int Seq;
        public static readonly List<string> Buffer = new();
        public static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, int> SessionCounts = new(StringComparer.Ordinal);
        public static readonly HashSet<Document> Docs = new();
        public static readonly HashSet<Database> Dbs = new();
        /// <summary>Command stack per database, from CommandWillStart and the three ends.</summary>
        public static readonly Dictionary<Database, List<string>> Commands = new();
        public static bool IdleArmed;
        public static string IdleArmedBy = "";
        public static int IdleFired;
    }

    // ------------------------------------------------------------------ commands

    // The probe's own commands change nothing in the drawing, so they carry NoUndoMarker.
    // MEASURED 2026-11-05 in Core Console (probeC-events.scr, SVHELLO control): a read-only
    // command without the flag is an undo step of its own, and a following _.U undoes that
    // command instead of the edit before it.

    [CommandMethod("SVPROBEEVENTS", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void EventsOn() => Run("P15 SVPROBEEVENTS", null, (doc, db) =>
    {
        if (Rec.On) { Say("event probe already on; SVPROBEEVENTSOFF first"); return; }
        Rec.Log = new ProbeCLog(EventsLogName);
        var log = Rec.Log;
        log.Write("===== P15 event probe ON =====");
        foreach (var f in ProbeCLog.HostFacts()) Say(Echo(f));

        Rec.On = true;
        Rec.Seq = 0;
        Rec.Buffer.Clear();
        Rec.Counts.Clear();
        Rec.SessionCounts.Clear();
        Rec.IdleFired = 0;

        var dm = AcApp.DocumentManager;
        dm.DocumentActivated += OnDocumentActivated;
        dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        foreach (Document d in dm) Attach(d);

        Say(Echo($"subscribed: Database.ObjectAppended, ObjectModified, ObjectErased, ObjectUnappended, ObjectReappended; " +
                 $"Document.CommandWillStart, CommandEnded, CommandCancelled, CommandFailed; DocumentCollection.DocumentActivated; " +
                 $"Application.Idle (one-shot after each command end) on {Rec.Docs.Count} document(s)"));
        Say(Echo(CivilEventCensus()));
        Say($"log: {log.FilePath}");
        ArmIdle("SVPROBEEVENTS");
    });

    [CommandMethod("SVPROBEEVENTSOFF", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void EventsOff() => Run("P15 SVPROBEEVENTSOFF", null, (doc, db) =>
    {
        if (!Rec.On) { Say("event probe is not on"); return; }
        Detach();
        Say(Echo("===== P15 event probe OFF: unsubscribed everywhere ====="));
        DumpBuffer();
        Say("session totals by event and object class:");
        lock (Rec.Gate)
            foreach (var kv in Rec.SessionCounts.OrderBy(k => k.Key, StringComparer.Ordinal)) Say($"  {kv.Key}: {kv.Value}");
        Say($"Idle one-shot fired {Rec.IdleFired} time(s) in the session");
        if (Rec.Log is { WriteFailures: > 0 } l) Say($"{FailCode} {l.WriteFailures} event line(s) could not be written to {l.FilePath}: {l.LastError}");
        Say($"log: {Rec.Log?.FilePath}");
    });

    [CommandMethod("SVPROBEEVENTSDUMP", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void EventsDump() => Run("P15 SVPROBEEVENTSDUMP", null, (doc, db) =>
    {
        Say($"event probe {(Rec.On ? "on" : "off")}");
        DumpBuffer();
    });

    /// <summary>Read-only state of everything SVPROBEC_EDIT touches, so the pre-check can show
    /// what each _.U actually reverted.</summary>
    [CommandMethod("SVPROBEC_STATE", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void EditState() => Run("P15 SVPROBEC_STATE", null, (doc, db) =>
    {
        using var tr = db.TransactionManager.StartTransaction();
        Say($"UNDOCTL={SysVar("UNDOCTL")}");
        Try("alignment", () =>
        {
            var al = FindAlignment(tr, db, PreCheckAlignment);
            var line = al.Entities.GetEntityByOrder(0) as AlignmentLine;
            Say($"{al.Name}: PassThroughPoint2={(line is null ? "n/a" : P(line.PassThroughPoint2))} EndingStation={R(al.EndingStation)} StationEquations.Count={al.StationEquations.Count}");
        });
        Try("surface", () =>
        {
            var s = FindTin(tr, db, PreCheckSurface);
            var v = PreCheckVertex;
            string at;
            try { at = P(s.FindVertexAtXY(v.X, v.Y).Location); } catch (System.Exception ex) { at = $"none ({ex.GetType().Name})"; }
            Say($"{s.Name}: triangles={s.GetTinProperties().NumberOfTriangles} vertex at ({F(v.X)}, {F(v.Y)}): {at}");
        });
        Say(_appendedLine.IsNull ? "appended line: none yet" : $"appended line {_appendedLine.Handle}: IsErased={_appendedLine.IsErased}");
        tr.Commit();
    });

    [CommandMethod("SVPROBEMARK", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void Mark()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var pr = doc.Editor.GetString(new PromptStringOptions("\nMarker text: ") { AllowSpaces = true });
        if (pr.Status != PromptStatus.OK) return;
        string text = $"===== MARK: {pr.StringResult} =====";
        if (Rec.Log is null) { doc.Editor.WriteMessage($"\n{FailCode} SVPROBEMARK: the event probe has not run in this session; nothing to mark"); return; }
        Record(text);
        doc.Editor.WriteMessage("\n" + text);
    }

    [CommandMethod("SVPROBEC_EDIT", CommandFlags.Modal)]
    public static void Edit()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var pr = doc.Editor.GetString(new PromptStringOptions("\nEdit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: ") { AllowSpaces = false });
        if (pr.Status != PromptStatus.OK) return;
        string what = pr.StringResult.Trim().ToUpperInvariant();
        Run($"P15 SVPROBEC_EDIT {what}", null, (d, db) =>
        {
            using var tr = db.TransactionManager.StartTransaction();
            switch (what)
            {
                case "PI": EditPi(tr, db); break;
                case "EQ": EditEquation(tr, db); break;
                case "VTXDEL": EditVertex(tr, db, add: false); break;
                case "VTXADD": EditVertex(tr, db, add: true); break;
                case "APPEND": EditAppend(tr, db); break;
                case "ERASE": EditErase(tr, db); break;
                default: throw new ArgumentException($"unknown edit '{what}'; use PI, EQ, VTXDEL, VTXADD, APPEND or ERASE");
            }
            tr.Commit();
        });
    }

    // ------------------------------------------------------------------ edits (pre-check)

    private static ObjectId _appendedLine = ObjectId.Null;

    private static Alignment FindAlignment(Transaction tr, Database db, string name)
    {
        foreach (ObjectId id in CivilDocument.GetCivilDocument(db).GetAlignmentIds())
            if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                return a;
        throw new InvalidOperationException($"alignment '{name}' not found; run SVFIXTURE S first");
    }

    private static TinSurface FindTin(Transaction tr, Database db, string name)
    {
        foreach (ObjectId id in CivilDocument.GetCivilDocument(db).GetSurfaceIds())
            if (tr.GetObject(id, OpenMode.ForRead) is TinSurface s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s;
        throw new InvalidOperationException($"TIN surface '{name}' not found; run SVFIXTURE A first");
    }

    private static void EditPi(Transaction tr, Database db)
    {
        var al = FindAlignment(tr, db, PreCheckAlignment);
        al.UpgradeOpen();
        var line = al.Entities.GetEntityByOrder(0) as AlignmentLine
                   ?? throw new InvalidOperationException($"{al.Name}: first entity is not an AlignmentLine");
        Say($"{al.Name} {al.Handle}: before PassThroughPoint1={P(line.PassThroughPoint1)} PassThroughPoint2={P(line.PassThroughPoint2)} EndingStation={R(al.EndingStation)}");
        line.PassThroughPoint2 = PiMovedTo;
        Say($"{al.Name}: set PassThroughPoint2={P(PiMovedTo)}; after PassThroughPoint2={P(line.PassThroughPoint2)} EndingStation={R(al.EndingStation)} Length={R(al.Length)}");
    }

    private static void EditEquation(Transaction tr, Database db)
    {
        var al = FindAlignment(tr, db, PreCheckAlignment);
        al.UpgradeOpen();
        al.StationEquations.Add(EquationRawBack, EquationAhead, StationEquationType.Increasing);
        Say($"{al.Name} {al.Handle}: StationEquations.Add({R(EquationRawBack)}, {R(EquationAhead)}, Increasing); Count={al.StationEquations.Count}");
    }

    private static void EditVertex(Transaction tr, Database db, bool add)
    {
        var s = FindTin(tr, db, PreCheckSurface);
        s.UpgradeOpen();
        int before = s.GetTinProperties().NumberOfTriangles;
        var v = PreCheckVertex;
        if (add)
        {
            using (s.AddVertex(v)) { }
            Say($"{s.Name} {s.Handle}: AddVertex{P(v)}; triangles {before} -> {s.GetTinProperties().NumberOfTriangles}");
        }
        else
        {
            var found = s.FindVertexAtXY(v.X, v.Y);
            s.DeleteVertex(found);
            Say($"{s.Name} {s.Handle}: DeleteVertex at {P(new Point2d(v.X, v.Y))}; triangles {before} -> {s.GetTinProperties().NumberOfTriangles}");
        }
    }

    private static void EditAppend(Transaction tr, Database db)
    {
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var line = new Line(LineStart, LineEnd);
        _appendedLine = ms.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
        Say($"appended Line {line.Handle} {P(LineStart)} -> {P(LineEnd)}");
    }

    private static void EditErase(Transaction tr, Database db)
    {
        if (_appendedLine.IsNull || _appendedLine.IsErased || _appendedLine.Database != db)
            throw new InvalidOperationException("no line from SVPROBEC_EDIT APPEND to erase in this drawing");
        var line = (Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(_appendedLine, OpenMode.ForWrite);
        line.Erase();
        Say($"erased Line {line.Handle}");
    }

    // ------------------------------------------------------------------ attach / detach

    private static void Attach(Document d)
    {
        lock (Rec.Gate)
        {
            if (!Rec.Docs.Add(d)) return;
            d.CommandWillStart += OnCommandWillStart;
            d.CommandEnded += OnCommandEnded;
            d.CommandCancelled += OnCommandCancelled;
            d.CommandFailed += OnCommandFailed;
            var db = d.Database;
            if (Rec.Dbs.Add(db))
            {
                db.ObjectAppended += OnObjectAppended;
                db.ObjectModified += OnObjectModified;
                db.ObjectErased += OnObjectErased;
                db.ObjectUnappended += OnObjectUnappended;
                db.ObjectReappended += OnObjectReappended;
                Rec.Commands[db] = new List<string>();
            }
        }
        Record($"attached to document '{d.Name}'");
    }

    private static void DetachDocument(Document d)
    {
        lock (Rec.Gate)
        {
            if (!Rec.Docs.Remove(d)) return;
            d.CommandWillStart -= OnCommandWillStart;
            d.CommandEnded -= OnCommandEnded;
            d.CommandCancelled -= OnCommandCancelled;
            d.CommandFailed -= OnCommandFailed;
            var db = d.Database;
            if (Rec.Dbs.Remove(db))
            {
                db.ObjectAppended -= OnObjectAppended;
                db.ObjectModified -= OnObjectModified;
                db.ObjectErased -= OnObjectErased;
                db.ObjectUnappended -= OnObjectUnappended;
                db.ObjectReappended -= OnObjectReappended;
                Rec.Commands.Remove(db);
            }
        }
    }

    private static void Detach()
    {
        var dm = AcApp.DocumentManager;
        dm.DocumentActivated -= OnDocumentActivated;
        dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        DisarmIdle();
        List<Document> docs;
        lock (Rec.Gate) docs = Rec.Docs.ToList();
        foreach (var d in docs) DetachDocument(d);
        Rec.On = false;
    }

    // ------------------------------------------------------------------ handlers

    private static void OnObjectAppended(object? sender, ObjectEventArgs e) => RecordObject("ObjectAppended", sender, e.DBObject, "");
    private static void OnObjectModified(object? sender, ObjectEventArgs e) => RecordObject("ObjectModified", sender, e.DBObject, "");
    private static void OnObjectErased(object? sender, ObjectErasedEventArgs e) => RecordObject("ObjectErased", sender, e.DBObject, $" Erased={e.Erased}");
    private static void OnObjectUnappended(object? sender, ObjectEventArgs e) => RecordObject("ObjectUnappended", sender, e.DBObject, "");
    private static void OnObjectReappended(object? sender, ObjectEventArgs e) => RecordObject("ObjectReappended", sender, e.DBObject, "");

    private static void OnCommandWillStart(object? sender, CommandEventArgs e)
    {
        var d = sender as Document;
        if (d is not null) lock (Rec.Gate) if (Rec.Commands.TryGetValue(d.Database, out var st)) st.Add(e.GlobalCommandName);
        RecordCommand("CommandWillStart", d, e.GlobalCommandName);
    }

    private static void OnCommandEnded(object? sender, CommandEventArgs e) => EndCommand("CommandEnded", sender as Document, e.GlobalCommandName);
    private static void OnCommandCancelled(object? sender, CommandEventArgs e) => EndCommand("CommandCancelled", sender as Document, e.GlobalCommandName);
    private static void OnCommandFailed(object? sender, CommandEventArgs e) => EndCommand("CommandFailed", sender as Document, e.GlobalCommandName);

    private static void EndCommand(string evt, Document? d, string name)
    {
        RecordCommand(evt, d, name);
        if (d is not null)
            lock (Rec.Gate)
                if (Rec.Commands.TryGetValue(d.Database, out var st))
                {
                    int i = st.LastIndexOf(name);
                    if (i >= 0) st.RemoveAt(i);
                }
        ArmIdle(name);
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        Record($"DocumentActivated '{e.Document?.Name}'");
        Count("DocumentActivated");
        if (e.Document is not null && Rec.On) Attach(e.Document);
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        Record($"DocumentToBeDestroyed '{e.Document?.Name}' (detached)");
        if (e.Document is not null) DetachDocument(e.Document);
    }

    private static void ArmIdle(string after)
    {
        lock (Rec.Gate)
        {
            if (!Rec.On) return;
            Rec.IdleArmedBy = after;
            if (Rec.IdleArmed) return;
            Rec.IdleArmed = true;
        }
        AcApp.Idle += OnIdle;
    }

    private static void DisarmIdle()
    {
        bool was;
        lock (Rec.Gate) { was = Rec.IdleArmed; Rec.IdleArmed = false; }
        if (was) AcApp.Idle -= OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        string by;
        lock (Rec.Gate) { by = Rec.IdleArmedBy; Rec.IdleFired++; }
        DisarmIdle();
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        Record($"Idle (one-shot) after '{by}'; CMDACTIVE={SysVar("CMDACTIVE")} CommandInProgress={S(() => doc?.CommandInProgress)} IsApplicationContext={S(() => AcApp.DocumentManager.IsApplicationContext)}");
        Count("Idle");
    }

    // ------------------------------------------------------------------ recording

    private static void RecordObject(string evt, object? sender, DBObject? o, string extra)
    {
        string cls = o is null ? "null" : S(() => o.GetRXClass().Name).Trim('\'');
        string managed = o?.GetType().Name ?? "null";
        string handle = o is null ? "?" : S(() => o.Handle);
        string undoing = o is null ? "?" : S(() => o.IsUndoing);
        string cmd = CurrentCommand(sender as Database);
        Record($"{evt} {cls}/{managed} h={handle} cmd={cmd} IsUndoing={undoing}{extra}");
        Count($"{evt} {cls}");
    }

    private static void RecordCommand(string evt, Document? d, string name)
    {
        Record($"{evt} '{name}' doc='{d?.Name}'");
        Count($"{evt} {name}");
    }

    private static string CurrentCommand(Database? db)
    {
        lock (Rec.Gate)
            if (db is not null && Rec.Commands.TryGetValue(db, out var st) && st.Count > 0)
                return string.Join(">", st);
        return "(none)";
    }

    private static void Record(string text)
    {
        var log = Rec.Log;
        if (log is null) return;
        int n;
        lock (Rec.Gate) n = ++Rec.Seq;
        var line = log.Write($"#{n} {text}");
        lock (Rec.Gate) Rec.Buffer.Add(line);
    }

    private static void Count(string key)
    {
        lock (Rec.Gate)
        {
            Rec.Counts[key] = Rec.Counts.TryGetValue(key, out var c) ? c + 1 : 1;
            Rec.SessionCounts[key] = Rec.SessionCounts.TryGetValue(key, out var s) ? s + 1 : 1;
        }
    }

    /// <summary>Echo a line to the event log as well as the command line.</summary>
    private static string Echo(string s)
    {
        Rec.Log?.Write(s);
        return s;
    }

    private static void DumpBuffer()
    {
        List<string> lines;
        List<KeyValuePair<string, int>> counts;
        bool armed; string armedBy;
        lock (Rec.Gate)
        {
            lines = Rec.Buffer.ToList();
            counts = Rec.Counts.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            Rec.Buffer.Clear();
            Rec.Counts.Clear();
            armed = Rec.IdleArmed; armedBy = Rec.IdleArmedBy;
        }
        Say($"{lines.Count} line(s) recorded since the last dump:");
        foreach (var l in lines.Take(DumpMaxLines)) Say("  " + l);
        if (lines.Count > DumpMaxLines) Say($"  ... {lines.Count - DumpMaxLines} more in {Rec.Log?.FilePath}");
        if (counts.Count > 0)
        {
            Say("counts since the last dump:");
            foreach (var kv in counts) Say($"  {kv.Key}: {kv.Value}");
        }
        Say($"Idle one-shot: {(armed ? $"armed after '{armedBy}', not fired yet" : "not armed")}; fired {Rec.IdleFired} time(s) this session");
    }

    /// <summary>Public events on the loaded Civil 3D managed assemblies, by run-time reflection.</summary>
    private static string CivilEventCensus()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var parts = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name is { } n && n.StartsWith("Aecc", StringComparison.OrdinalIgnoreCase) && n.EndsWith("Mgd", StringComparison.OrdinalIgnoreCase)
                                 && !n.Contains("Ui", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            catch (System.Exception ex) { parts.Add($"{asm.GetName().Name}: GetExportedTypes threw {ex.GetType().Name}"); continue; }
            var events = new List<string>();
            int unreadable = 0;
            foreach (var t in types)
            {
                try { events.AddRange(t.GetEvents(all).Select(ev => $"{t.Name}.{ev.Name}")); }
                catch (System.Exception) { unreadable++; }
            }
            parts.Add($"{asm.GetName().Name} {asm.GetName().Version}: {types.Length} exported type(s), {events.Count} public event(s)" +
                      (unreadable > 0 ? $", {unreadable} type(s) whose events could not be read" : "") +
                      (events.Count > 0 ? ": " + string.Join(", ", events.Take(20)) : ""));
        }
        return "Civil 3D API events (run-time reflection over loaded Aecc*Mgd, UI assemblies excluded): " + (parts.Count == 0 ? "none loaded" : string.Join("; ", parts));
    }
}
