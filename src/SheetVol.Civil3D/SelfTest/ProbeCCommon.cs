// S3 probes (HANDOFF.md 5.3: P14, P15, P21). Evidence code, not product code: every
// command writes what it observed to the command line and to a timestamped log under
// %TEMP%\sheetvol\ so docs/api-findings.md section C can quote it and Dan can send the
// file back. Shared names, numbers and helpers for the ProbeC*.cs files live here.
// The P21 window lives in SheetVol.UI/Probe (WPF and AcMgd are not allowed here, 6.13);
// it uses ProbeCLog and ProbeCSettings from this file so the log format is one thing.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(SheetVol.Civil3D.SelfTest.ProbeCCommands))]

namespace SheetVol.Civil3D.SelfTest;

/// <summary>Every name and number the S3 probes use. Probe-only values; the product's
/// equivalents come from config (HANDOFF.md 6.13).</summary>
public static class ProbeCSettings
{
    /// <summary>Printed when a probe step fails at the command boundary (NOW.md registry).</summary>
    public const string FailCode = "SV-E198";

    public static string OutDir => Path.Combine(Path.GetTempPath(), "sheetvol");
    public const string EventsLogName = "probeEvents.log";
    public const string FramesLogName = "probeFrames.log";
    public const string UiLogName = "probeUI.log";

    // ---------- P15 headless pre-check edits, all on FX fixtures ----------

    /// <summary>FX alignment S: one fixed line (0,0) to (2000,0).</summary>
    internal static string PreCheckAlignment => FixtureSpec.Names.AlignmentS;

    /// <summary>The end PI of S moves 50 ft north (the API has no PI collection; on a fixed
    /// line the PIs are <c>AlignmentLine.PassThroughPoint1/2</c>).</summary>
    internal static Point2d PiMovedTo => new(FixtureSpec.S.EndX, 50.0);

    internal const double EquationRawBack = 1000.0, EquationAhead = 800.0;

    /// <summary>FX surface FG_A and the apex of its pyramid P5, deleted then added back.</summary>
    internal static string PreCheckSurface => FixtureSpec.Names.FgA;
    internal static Point3d PreCheckVertex
    {
        get
        {
            var p5 = FixtureSpec.Pyramids[^1];
            return new Point3d(p5.CenterX, 0.0, p5.ApexZ);
        }
    }

    /// <summary>A plain line appended then erased, so the append and erase events and their
    /// undo can be read on an AutoCAD object as well as on Civil objects.</summary>
    internal static readonly Point3d LineStart = new(0.0, -500.0, 0.0), LineEnd = new(100.0, -500.0, 0.0);

    /// <summary>A dump prints at most this many event lines; the log file has all of them.</summary>
    internal const int DumpMaxLines = 300;

    /// <summary>Recursion guard for exploding view frames (frame, block, polyline, line).</summary>
    internal const int ExplodeMaxDepth = 5;

    /// <summary>Two points closer than this are the same corner when the exploded segments
    /// are chained into the frame rectangle; edges within this cosine of 90° are square.</summary>
    internal const double CornerTolerance = 1e-6, SquareTolerance = 1e-6;

    /// <summary>The 5.2 hypothesis is read as holding when the first edge's direction is
    /// within this many degrees of the alignment tangent at its midpoint.</summary>
    internal const double HypothesisAngleDeg = 1.0;
}

/// <summary>Timestamped append-only log under <see cref="ProbeCSettings.OutDir"/>. One line
/// per call, written and closed at once, so a crash loses nothing and Dan can send the
/// file while Civil 3D is still open. Thread-safe.</summary>
public sealed class ProbeCLog
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();

    public ProbeCLog(string fileName)
    {
        Directory.CreateDirectory(ProbeCSettings.OutDir);
        FilePath = Path.Combine(ProbeCSettings.OutDir, fileName);
    }

    public string FilePath { get; }

    /// <summary>Lines that could not be written (file locked, disk full). Reported by the
    /// probes so a short log is never mistaken for "nothing fired".</summary>
    public int WriteFailures { get; private set; }

    public string LastError { get; private set; } = "";

    /// <summary>Appends one timestamped line and returns it as written.</summary>
    public string Write(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}";
        lock (_gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine, Utf8); }
            catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteFailures++;
                LastError = ex.Message;
            }
        }
        return line;
    }

    /// <summary>Host facts every probe log starts with: process, runtime, assembly versions,
    /// AutoCAD version variables, drawing.</summary>
    public static IEnumerable<string> HostFacts()
    {
        yield return $"process: {Environment.ProcessPath}  Core Console: {ProbeCCommands.IsCoreConsole}";
        yield return $"runtime: {RuntimeInformation.FrameworkDescription}  OS: {RuntimeInformation.OSDescription}";
        yield return $"AcDbMgd {typeof(Database).Assembly.GetName().Version}  AcCoreMgd {typeof(Document).Assembly.GetName().Version}  AeccDbMgd {typeof(CivilDocument).Assembly.GetName().Version}  SheetVol.Civil3D {typeof(ProbeCLog).Assembly.GetName().Version}";
        yield return $"ACADVER={ProbeCCommands.SysVar("ACADVER")}  _VERNUM={ProbeCCommands.SysVar("_VERNUM")}  PRODUCT={ProbeCCommands.SysVar("PRODUCT")}";
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        yield return $"active document: {(doc is null ? "none" : doc.Name)}  documents open: {AcApp.DocumentManager.Count}";
    }
}

/// <summary>Command class for all S3 probes; partial across the ProbeC*.cs files.</summary>
public static partial class ProbeCCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Editor? _ed;
    private static ProbeCLog? _sayLog;

    internal static bool IsCoreConsole =>
        string.Equals(Path.GetFileName(Environment.ProcessPath), "accoreconsole.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>One line to the command line, and to the current probe's log when one is set.</summary>
    internal static void Say(string s)
    {
        if (_ed is not null) _ed.WriteMessage("\n" + s);
        else Console.WriteLine(s);
        _sayLog?.Write(s);
    }

    /// <summary>Command boundary: one line with SV-E198 on any escape; the transactions inside
    /// are <c>using</c> blocks, so an escape aborts them (6.13).</summary>
    internal static void Run(string probe, ProbeCLog? log, Action<Document, Database> body)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        _ed = doc?.Editor;
        _sayLog = log;
        if (doc is null)
        {
            Console.WriteLine($"{ProbeCSettings.FailCode} {probe}: no active document");
            log?.Write($"{ProbeCSettings.FailCode} {probe}: no active document");
            _sayLog = null;
            return;
        }
        Say($"===== {probe} begin =====");
        if (log is not null) Say($"log: {log.FilePath}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body(doc, doc.Database);
            Say($"===== {probe} end ({sw.Elapsed.TotalSeconds:0.00} s) =====");
        }
        catch (System.Exception ex)
        {
            Say($"{ProbeCSettings.FailCode} {probe} failed: {Describe(ex)}");
            Say($"===== {probe} end (FAILED) =====");
        }
        finally
        {
            if (log is { WriteFailures: > 0 })
            {
                var msg = $"{ProbeCSettings.FailCode} {probe}: {log.WriteFailures} line(s) could not be written to {log.FilePath}: {log.LastError}";
                if (_ed is not null) _ed.WriteMessage("\n" + msg); else Console.WriteLine(msg);
            }
            _sayLog = null;
        }
    }

    /// <summary>Runs one probe step; an exception is an observation, printed, not a failure.</summary>
    internal static bool Try(string label, Action step)
    {
        try { step(); return true; }
        catch (System.Exception ex) { Say($"{label} threw {Describe(ex)}"); return false; }
    }

    public static string Describe(System.Exception ex) => ex switch
    {
        Autodesk.AutoCAD.Runtime.Exception a => $"{a.GetType().FullName} ErrorStatus={a.ErrorStatus} Message='{a.Message}'",
        System.Reflection.TargetInvocationException t when t.InnerException is not null => Describe(t.InnerException),
        AggregateException g when g.InnerException is not null => Describe(g.InnerException),
        _ => $"{ex.GetType().FullName} Message='{ex.Message}'",
    };

    /// <summary>A system variable as text, or "threw ..." (some do not exist in Core Console).</summary>
    public static string SysVar(string name)
    {
        try { return AcApp.GetSystemVariable(name)?.ToString() ?? "null"; }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    // ---------- formatting ----------

    /// <summary>A property read that may throw, as text: the value, or "threw &lt;type&gt;".</summary>
    internal static string S(Func<object?> read)
    {
        try
        {
            return read() switch
            {
                null => "null",
                double d => R(d),
                Point2d p => P(p),
                Point3d p => P(p),
                string str => $"'{str}'",
                var o => o.ToString() ?? "null",
            };
        }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}('{ex.Message}')"; }
    }

    internal static string R(double d) => d.ToString("R", Inv);
    internal static string F(double d) => d.ToString("0.######", Inv);
    internal static string F3(double d) => d.ToString("0.000", Inv);
    internal static string E(double d) => d.ToString("0.00E+00", Inv);
    internal static string P(Point2d p) => $"({F(p.X)}, {F(p.Y)})";
    internal static string P(Point3d p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";
    internal static double Deg(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Angle wrapped into (−π, π].</summary>
    internal static double Wrap(double a)
    {
        while (a <= -Math.PI) a += 2 * Math.PI;
        while (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }
}
