// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// What the engine remembers between calls in one AutoCAD session, per document: the last
// successful regen of each run (RunStatus.LastResult, from the dialog or a typed SVREGEN,
// E912) and its reconciled volumes (so the dialog's Export writes what the tables show).
// Commands and the session both write here; the session turns Changed into SourcesChanged.
// Also the read helper every database read goes through, and the run status reader shared
// by GetRunStatus and SVSTATUS.

using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace SheetVol.Civil3D.Session;

/// <summary>The last successful regen of one run in this session.</summary>
/// <param name="Result">The contract result.</param>
/// <param name="Run">The definition as applied.</param>
/// <param name="Reconcile">The reconciled volumes, for exports.</param>
/// <param name="DefinitionSignature">C3 definition signature of <paramref name="Run"/>.</param>
public sealed record CachedRegen(RegenResult Result, RunDefinition Run, ReconcileResult? Reconcile, string DefinitionSignature);

/// <summary>Per-document session memory.</summary>
public static class SessionState
{
    private sealed class DocMemory
    {
        public readonly Dictionary<string, CachedRegen> Last = new(StringComparer.Ordinal);
    }

    // Keyed by the native database pointer: the managed Database wrapper is not guaranteed to
    // be the same object on every Document.Database read (MEASURED 2026-11-05: a
    // ConditionalWeakTable keyed by the wrapper lost its entries between two reads). Entries
    // are dropped when the document closes, so a reused pointer never inherits old results.
    private static readonly Dictionary<IntPtr, DocMemory> Memory = [];
    private static readonly object Gate = new();

    static SessionState()
    {
        Application.DocumentManager.DocumentToBeDestroyed += (_, e) =>
        {
            if (e.Document?.Database is { } db) lock (Gate) Memory.Remove(db.UnmanagedObject);
        };
    }

    /// <summary>Raised after a SheetVol command or regen changed a run in a document.</summary>
    public static event EventHandler<Database>? Changed;

    /// <summary>Remembers a successful regen and raises <see cref="Changed"/>.</summary>
    public static void Record(Database db, RunOutcome outcome)
    {
        if (outcome.Result.Succeeded)
            lock (Gate)
            {
                if (!Memory.TryGetValue(db.UnmanagedObject, out var m)) Memory[db.UnmanagedObject] = m = new DocMemory();
                m.Last[outcome.Result.RunName] = new CachedRegen(outcome.Result, outcome.Run, outcome.Reconcile, Fingerprints.DefinitionSignature(outcome.Run));
            }
        Changed?.Invoke(null, db);
    }

    /// <summary>Forgets a run (SVCLEAR) and raises <see cref="Changed"/>.</summary>
    public static void Forget(Database db, string runName)
    {
        lock (Gate) if (Memory.TryGetValue(db.UnmanagedObject, out var m)) m.Last.Remove(runName);
        Changed?.Invoke(null, db);
    }

    /// <summary>Raises <see cref="Changed"/> after a definition edit (SVDEFINE, SVANALYSIS, SVBREAK).</summary>
    public static void Touch(Database db) => Changed?.Invoke(null, db);

    /// <summary>The last successful regen of a run in this session, or null.</summary>
    public static CachedRegen? Last(Database db, string runName)
    {
        lock (Gate) return Memory.TryGetValue(db.UnmanagedObject, out var m) && m.Last.TryGetValue(runName, out var c) ? c : null;
    }
}

/// <summary>Opens the adapters and the store inside one read transaction under the document lock.</summary>
public static class DrawingRead
{
    /// <summary>Runs <paramref name="read"/> and commits (reads only; committing is cheaper than aborting).</summary>
    public static T Run<T>(AcadDocument doc, RegenEngine engine, Func<IDrawingSources, IPersistenceStore, T> read)
    {
        using var docLock = doc.LockDocument();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var sources = engine.Bindings.Adapters.Open(db, tr, engine.Settings);
        var store = engine.Bindings.Writers.Store(db, tr, engine.Settings);
        var result = read(sources, store);
        tr.Commit();
        return result;
    }

    /// <summary>Runs a write against the store only (definition edits) and commits.</summary>
    public static T Write<T>(AcadDocument doc, RegenEngine engine, Func<IDrawingSources, IPersistenceStore, Transaction, T> write)
    {
        using var docLock = doc.LockDocument();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var sources = engine.Bindings.Adapters.Open(db, tr, engine.Settings);
        var store = engine.Bindings.Writers.Store(db, tr, engine.Settings);
        var result = write(sources, store, tr);
        tr.Commit();
        return result;
    }
}

/// <summary>A run's status: stored or not, stale or clean, why, and what SVSTATUS lists.</summary>
/// <param name="RunName">The run.</param>
/// <param name="Exists">A record is stored.</param>
/// <param name="Record">The stored record, or null.</param>
/// <param name="IsStale">The fingerprint differs from the current sources, or the status cannot be told.</param>
/// <param name="ChangedComponents">C3 fingerprint components that differ.</param>
/// <param name="Problems">Why the status could not be read fully.</param>
public sealed record RunStatusReport(string RunName, bool Exists, RunRecord? Record, bool IsStale, IReadOnlyList<string> ChangedComponents, IReadOnlyList<Problem> Problems);

/// <summary>Computes a run's current fingerprint read-only and compares it with the stored one (6.11).</summary>
public static class RunStatusReader
{
    /// <summary>Reads the status. Never rebuilds or writes.</summary>
    public static RunStatusReport Read(IDrawingSources sources, IPersistenceStore store, string runName, SheetVolSettings settings)
    {
        var load = store.Load(runName);
        if (!load.Exists) return new RunStatusReport(runName, false, null, true, [], []);
        if (load.Record is not { } record) return new RunStatusReport(runName, true, null, true, [], load.Problems);
        var problems = new List<Problem>(load.Problems);

        var alignment = sources.Alignment(record.Run.AlignmentName, record.SourceHandleOf(SourceKinds.Alignment, record.Run.AlignmentName));
        if (!alignment.Succeeded) return new RunStatusReport(runName, true, record, true, [Fingerprints.AlignmentComponent], [.. problems, .. alignment.Problems]);

        var states = new List<AnalysisSurfaceState>();
        var owned = OwnedVolumeSurfaces.Plan(record.Run.Analyses, settings.Report.Volumes);
        if (owned.Succeeded)
            foreach (var plan in owned.Value)
                foreach (var id in plan.AnalysisIds)
                    if (sources.ReadVolumeSurfaceState(id, plan, record.SourceHandleOf(SourceKinds.VolumeSurface, plan.Name)) is { } st)
                        states.Add(st);

        var manual = new Dictionary<Guid, IReadOnlyList<PolyVertex>>();
        foreach (var m in record.Run.ManualSegments)
        {
            var o = sources.ManualOutline(m.PolylineHandle);
            if (o.Succeeded) manual[m.Id] = o.Value.Vertices;
        }

        var current = Fingerprints.Compute(new FingerprintInput(record.Run, alignment.Value.Geometry, [], states, manual));
        var cmp = Fingerprints.Compare(record.Fingerprint, current);
        return new RunStatusReport(runName, true, record, cmp.IsStale, cmp.ChangedComponents, problems);
    }
}
