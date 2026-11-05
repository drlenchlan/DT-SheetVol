// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// The command boundary (HANDOFF.md 6.13): every SV command body runs inside Boundary.Run,
// which catches AutoCAD, Civil and .NET exceptions, prints one line with a stable SV-Exxx
// code, and lets the open transaction abort on dispose. Prompts are typed-input first
// (A9): every prompt accepts text from a script line, picks are optional keywords, and
// a cancelled prompt ends the command without a write.

using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace SheetVol.Civil3D.Commands;

/// <summary>Thrown when a prompt is cancelled; the boundary prints it as a plain cancel, not an error.</summary>
internal sealed class PromptCancelledException(string prompt) : Exception($"Cancelled at: {prompt.Trim()}");

/// <summary>One running command: document, editor, engine and prompt helpers.</summary>
internal sealed class Cmd
{
    public Cmd(string name, AcadDocument doc, RegenEngine? engine)
    {
        Name = name;
        Doc = doc;
        Ed = doc.Editor;
        EngineOrNull = engine;
    }

    public string Name { get; }
    public AcadDocument Doc { get; }
    public Editor Ed { get; }
    public RegenEngine? EngineOrNull { get; }
    public RegenEngine Engine => EngineOrNull ?? throw new InvalidOperationException("This command needs valid settings.");

    public void Say(string text) => Ed.WriteMessage("\n" + text);

    public void Report(Problem p, string? prefix = null) =>
        Say($"{p.Code} {p.Severity.ToString().ToUpperInvariant()} {prefix}{p.Message}{(p.Fix is null ? "" : "  Fix: " + p.Fix)}");

    /// <summary>Prints errors and warnings in full and summarises notes by code.</summary>
    public void ReportAll(IEnumerable<Problem> problems, bool includeInfo = false)
    {
        var list = problems.ToList();
        foreach (var p in list.Where(p => p.Severity != Severity.Info || includeInfo)) Report(p);
        if (!includeInfo)
        {
            var notes = list.Where(p => p.Severity == Severity.Info).GroupBy(p => p.Code).Select(g => $"{g.Key} x{g.Count()}").ToList();
            if (notes.Count > 0) Say($"  notes: {string.Join(", ", notes)}");
        }
    }

    public string String(string prompt, bool allowSpaces = false, string? defaultValue = null)
    {
        var o = new PromptStringOptions("\n" + prompt + ": ") { AllowSpaces = allowSpaces };   // AutoCAD appends <default> itself
        if (defaultValue is not null) { o.DefaultValue = defaultValue; o.UseDefaultValue = true; }
        var r = Ed.GetString(o);
        if (r.Status != PromptStatus.OK) throw new PromptCancelledException(prompt);
        string s = (r.StringResult ?? "").Trim();
        if (s.Length == 0 && defaultValue is not null) s = defaultValue;
        return s;
    }

    public string Keyword(string prompt, IReadOnlyList<string> keywords, string defaultKeyword)
    {
        var o = new PromptKeywordOptions("\n" + prompt + $" [{string.Join("/", keywords)}] <{defaultKeyword}>: ") { AllowNone = true, AllowArbitraryInput = false, AppendKeywordsToMessage = false };
        foreach (var k in keywords) o.Keywords.Add(k);
        o.Keywords.Default = defaultKeyword;
        var r = Ed.GetKeywords(o);
        if (r.Status == PromptStatus.None) return defaultKeyword;
        if (r.Status != PromptStatus.OK) throw new PromptCancelledException(prompt);
        return string.IsNullOrEmpty(r.StringResult) ? defaultKeyword : r.StringResult;
    }

    public double Number(string prompt, double? defaultValue = null)
    {
        var o = new PromptDoubleOptions("\n" + prompt + ": ") { AllowNone = defaultValue is not null, AllowNegative = true, AllowZero = true };
        if (defaultValue is { } d) { o.DefaultValue = d; o.UseDefaultValue = true; }
        var r = Ed.GetDouble(o);
        if (r.Status == PromptStatus.None && defaultValue is { } dv) return dv;
        if (r.Status != PromptStatus.OK) throw new PromptCancelledException(prompt);
        return r.Value;
    }

    public bool YesNo(string prompt, bool defaultValue) =>
        Keyword(prompt, ["Yes", "No"], defaultValue ? "Yes" : "No") == "Yes";

    public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The command boundary (6.13).</summary>
internal static class Boundary
{
    private static bool _settingsNoticePrinted;

    /// <summary>Runs a command body with settings loaded and every exception caught.</summary>
    /// <param name="name">Command name for messages.</param>
    /// <param name="body">The body.</param>
    /// <param name="needsEngine">False for SVCONFIG, which must run with an invalid settings file.</param>
    public static void Run(string name, Action<Cmd> body, bool needsEngine = true)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var ed = doc.Editor;
        void Say(string s) => ed.WriteMessage("\n" + s);
        try
        {
            var (engine, load) = EngineHost.Create(EngineBindings.Current);
            if (engine is null && needsEngine)
            {
                Say($"{PipelineCodes.SettingsInvalid} {name}: settings from {load.Source} are not usable; nothing was done. SVCONFIG shows them.");
                foreach (var p in load.Problems) Say($"  {p.Code} {p.Message}");
                return;
            }
            var cmd = new Cmd(name, doc, engine);
            if (!_settingsNoticePrinted && load.Settings is not null)
            {
                _settingsNoticePrinted = true;
                foreach (var p in load.Problems.Concat(SettingsProvider.TakePlaceholderNotice(load.Settings))) cmd.Report(p);
            }
            body(cmd);
        }
        catch (PromptCancelledException ex)
        {
            Say($"{name}: {ex.Message}. Nothing was changed.");
        }
        catch (OperationCanceledException)
        {
            Say($"{PipelineCodes.Cancelled} {name}: cancelled before commit; the drawing is unchanged.");
        }
        catch (NotWiredException ex)
        {
            Say($"{PipelineCodes.NotWired} {name}: {ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            Say($"{PipelineCodes.Unexpected} {name}: AutoCAD {ex.ErrorStatus}: {ex.Message}. The transaction was aborted.");
        }
        catch (Exception ex)
        {
            Say($"{PipelineCodes.Unexpected} {name}: {ex.GetType().FullName}: {ex.Message}. The transaction was aborted.");
        }
    }
}
