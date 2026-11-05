// S3 probe P21: the SVPROBEUI window. Built in code so the probe opens even if compiled XAML
// fails to load from a NETLOADed assembly; that question is answered separately by the
// ProbeXamlPanel slot. Everything the window observes goes to %TEMP%\sheetvol\probeUI.log
// with a timestamp (ProbeCLog) and to the log pane at the bottom of the window.
//
// Questions it answers (HANDOFF.md 5.3 P21, 6.14, 7.9 E905, E908, E909, E911):
// - Application.ShowModelessWindow on .NET 10: owner, thread, DPI, size on open.
// - Database work from a button: no lock (control), LockDocument(), ExecuteInCommandContextAsync,
//   SendStringToExecute of a registered command. Which work, and the lock state each saw.
// - Colour theme: COLORTHEME on open; which of Application.SystemVariableChanged,
//   ComponentManager.PropertyChanged and the Internal ActiveThemeColor.ColorThemeChanged fire
//   on a switch; the window restyles by swapping a merged ResourceDictionary.
// - IsCommandActive: Document.CommandWillStart/Ended/Cancelled/Failed and
//   DocumentCollection.DocumentLockModeChanged; work buttons disable while a command runs.
// - Entity.Highlight() and a TransientManager outline, cleared on Clear and on close.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using SheetVol.Civil3D.SelfTest;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace SheetVol.UI.Probe;

internal sealed class ProbeUiWindow : Window
{
    private readonly ProbeCLog _log;
    private readonly TextBox _logPane = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas") };
    private readonly Queue<string> _paneLines = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
    private readonly List<Button> _workButtons = new();
    private readonly List<Action> _unsubscribe = new();
    private readonly DispatcherTimer _resizeSettle;
    private Document? _doc;
    private int _activeCommands;
    private string _lastCommand = "";
    private int _theme = -1;
    private bool _shutDown;

    public ProbeUiWindow(ProbeCLog log)
    {
        _log = log;
        Title = ProbeUiSettings.Title;
        Width = ProbeUiSettings.Width;
        Height = ProbeUiSettings.Height;
        MinWidth = ProbeUiSettings.MinWidth;
        MinHeight = ProbeUiSettings.MinHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        _resizeSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProbeUiSettings.ResizeSettleMs) };
        _resizeSettle.Tick += (_, _) => Guard("resize settle", () => { _resizeSettle.Stop(); LogSize("after resize"); });

        ApplyTheme(ReadTheme(), "window construction");
        SetResourceReference(BackgroundProperty, ProbeUiSettings.BackgroundKey);
        SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
        Content = BuildContent();

        Loaded += (_, _) => Guard("Loaded", OnLoaded);
        ContentRendered += (_, _) => Guard("ContentRendered", () => LogSize("first render"));
        SizeChanged += (_, _) => { _resizeSettle.Stop(); _resizeSettle.Start(); };
        DpiChanged += (_, e) => Guard("DpiChanged", () =>
        {
            Log($"DPI changed: {e.OldDpi.DpiScaleX} -> {e.NewDpi.DpiScaleX} ({e.OldDpi.PixelsPerInchX} -> {e.NewDpi.PixelsPerInchX} ppi)");
            LogSize("after DPI change");
        });
        Closed += (_, _) => Guard("Closed", OnClosed);

        Subscribe();
        Follow(CoreApp.DocumentManager.MdiActiveDocument, "window construction");
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

        var top = new StackPanel();
        var path = new TextBox { Text = $"Log file: {_log.FilePath}", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        path.SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
        top.Children.Add(path);
        top.Children.Add(_status);

        top.Children.Add(Group("1. Database work from this window (run each once)", Buttons(
            ("R0  Write with no lock (control: expected to fail)", true, () => Route("R0", Route0)),
            ("R1  Write under LockDocument()", true, () => Route("R1", Route1)),
            ("R2  Write via ExecuteInCommandContextAsync", true, () => Route("R2", Route2)),
            ("R3  Write via SendStringToExecute", true, () => Route("R3", Route3)))));

        top.Children.Add(Group("2. Highlight in the drawing (pick first)", Buttons(
            ("Pick entity", true, () => Route("PICK", Pick)),
            ("Highlight()", true, () => Route("HIGHLIGHT", () => Log("HIGHLIGHT " + ProbeUiWork.Highlight(_doc!)))),
            ("Transient outline", true, () => Route("TRANSIENT", () => Log("TRANSIENT " + ProbeUiWork.Transient(_doc!)))),
            ("Clear", true, () => Route("CLEAR", () => Log("CLEAR " + ProbeUiWork.Clear(_doc, out _)))))));

        top.Children.Add(Group("3. Window, theme and display scaling", Buttons(
            ("Log size and DPI now", false, () => LogSize("button")),
            ("Log theme now", false, () =>
            {
                Log($"THEME now: COLORTHEME={ReadTheme()} applied={(_theme == 0 ? "dark" : "light")} ComponentManager.CurrentTheme={CurrentThemeName()}");
                ThemeCheck("the Log theme now button");
            }))));

        UIElement xaml;
        try
        {
            xaml = new ProbeXamlPanel();
            Log("XAML: ProbeXamlPanel constructed (InitializeComponent from SheetVol.UI.dll succeeded)");
        }
        catch (System.Exception ex)
        {
            Log($"XAML: ProbeXamlPanel failed: {ProbeCCommands.Describe(ex)}{(ex.InnerException is null ? "" : " inner: " + ProbeCCommands.Describe(ex.InnerException))}");
            xaml = new TextBlock { Text = "XAML panel failed to load (see the log).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        }
        top.Children.Add(xaml);

        var seen = new WrapPanel();
        foreach (var text in ProbeUiSettings.Observations)
        {
            var cb = new CheckBox { Content = text, Margin = new Thickness(0, 2, 16, 2) };
            cb.SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
            cb.Checked += (_, _) => Log($"OBSERVED '{text}' = True");
            cb.Unchecked += (_, _) => Log($"OBSERVED '{text}' = False");
            seen.Children.Add(cb);
        }
        var note = new TextBox { MinWidth = 300, Margin = new Thickness(0, 4, 8, 0) };
        var addNote = StyledButton("Add note to log");
        addNote.Click += (_, _) => Guard("note", () => { if (note.Text.Trim().Length > 0) { Log($"NOTE {note.Text.Trim()}"); note.Clear(); } });
        var noteRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(addNote, Dock.Right);
        noteRow.Children.Add(addNote);
        noteRow.Children.Add(note);
        var seenPanel = new StackPanel();
        seenPanel.Children.Add(seen);
        seenPanel.Children.Add(noteRow);
        top.Children.Add(Group("4. What you saw (tick what is true; each tick is logged)", seenPanel));

        // Log pane at a fixed height along the bottom; the controls scroll above it, so a
        // small window never cuts content off by layout alone (E909 is then about scaling).
        _logPane.Height = ProbeUiSettings.LogPaneHeight;
        _logPane.Margin = new Thickness(0, 6, 0, 0);
        _logPane.SetResourceReference(BackgroundProperty, ProbeUiSettings.PanelKey);
        _logPane.SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
        _logPane.SetResourceReference(BorderBrushProperty, ProbeUiSettings.BorderKey);
        DockPanel.SetDock(_logPane, Dock.Bottom);
        root.Children.Add(_logPane);
        root.Children.Add(new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    private UIElement Group(string header, UIElement body)
    {
        var g = new GroupBox { Header = header, Content = body, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(6) };
        g.SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
        g.SetResourceReference(BorderBrushProperty, ProbeUiSettings.BorderKey);
        return g;
    }

    private UIElement Buttons(params (string Text, bool NeedsIdleDocument, Action Click)[] specs)
    {
        var panel = new WrapPanel();
        foreach (var (text, needsIdle, click) in specs)
        {
            var b = StyledButton(text);
            b.Click += (_, _) => Guard(text, click);
            if (needsIdle) _workButtons.Add(b);
            panel.Children.Add(b);
        }
        return panel;
    }

    private static Button StyledButton(string text)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 2, 8, 2), Padding = new Thickness(8, 3, 8, 3) };
        b.SetResourceReference(BackgroundProperty, ProbeUiSettings.ButtonKey);
        b.SetResourceReference(ForegroundProperty, ProbeUiSettings.ForegroundKey);
        b.SetResourceReference(BorderBrushProperty, ProbeUiSettings.BorderKey);
        return b;
    }

    // ------------------------------------------------------------------ open / close

    private void OnLoaded()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var helperOwner = new WindowInteropHelper(this).Owner;
        IntPtr acadMain = IntPtr.Zero;
        // AutoCAD 2025's AcMgd Application has no MainWindow; Core.Application (AcCoreMgd) does.
        try { acadMain = CoreApp.MainWindow.Handle; } catch (System.Exception ex) { Log($"Core.Application.MainWindow threw {ProbeCCommands.Describe(ex)}"); }
        Log($"OPEN: hwnd=0x{hwnd.ToInt64():X} WindowInteropHelper.Owner=0x{helperOwner.ToInt64():X} Core.Application.MainWindow.Handle=0x{acadMain.ToInt64():X} owned by AutoCAD main window={helperOwner == acadMain && acadMain != IntPtr.Zero} WPF Owner={(Owner is null ? "null" : Owner.GetType().Name)}");
        Log($"OPEN: System.Windows.Application.Current={(System.Windows.Application.Current is null ? "null" : System.Windows.Application.Current.GetType().FullName)} dispatcher thread={Dispatcher.Thread.ManagedThreadId} current thread={Environment.CurrentManagedThreadId} apartment={Thread.CurrentThread.GetApartmentState()}");
        Log($"OPEN: thread DPI awareness={ProbeUiNative.ThreadAwareness()} window DPI awareness={ProbeUiNative.WindowAwareness(hwnd)}");
        LogSize("on open (Loaded)");
        Log($"OPEN: theme COLORTHEME={ReadTheme()} ComponentManager.CurrentTheme={CurrentThemeName()}");
        UpdateStatus();
    }

    private void OnClosed() => Shutdown("window closed");

    /// <summary>Clears the drawing, forgets every id and removes every handler. Idempotent:
    /// runs from Closed, and from SVPROBEUI when ShowModelessWindow throws (a window that was
    /// never shown raises no Closed, but it subscribed in its constructor).</summary>
    public void Shutdown(string why)
    {
        if (_shutDown) return;
        _shutDown = true;
        _resizeSettle.Stop();
        string cleared = ProbeUiWork.Clear(CoreApp.DocumentManager.MdiActiveDocument, out bool anything);
        Log(anything ? $"CLOSE {cleared} (E911: highlight cleared on close)" : $"CLOSE nothing was highlighted at close ({cleared})");
        Log("CLOSE " + ProbeUiWork.Forget(null));
        Unfollow();
        foreach (var off in _unsubscribe) Guard("unsubscribe", off);
        Log($"CLOSE: {_unsubscribe.Count} application-level handler(s) removed because {why}");
        _unsubscribe.Clear();
    }

    // ------------------------------------------------------------------ subscriptions

    private void Subscribe()
    {
        var dm = CoreApp.DocumentManager;

        Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventHandler sysVar = (_, e) =>
        {
            if (!string.Equals(e.Name, ProbeUiSettings.ColorThemeVariable, StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.BeginInvoke(() => Guard("COLORTHEME change", () =>
            {
                Log($"EVENT Application.SystemVariableChanged {e.Name} Changed={e.Changed} value now {ReadTheme()}");
                ThemeCheck("Application.SystemVariableChanged");
            }));
        };
        CoreApp.SystemVariableChanged += sysVar;
        _unsubscribe.Add(() => CoreApp.SystemVariableChanged -= sysVar);

        // Whichever of the three theme signals first sees COLORTHEME differ from the applied
        // theme restyles the window, and the THEME line names it.
        EventHandler<System.ComponentModel.PropertyChangedEventArgs> cm = (_, e) =>
            Dispatcher.BeginInvoke(() => Guard("ComponentManager.PropertyChanged", () =>
            {
                Log($"EVENT ComponentManager.PropertyChanged '{e.PropertyName}' CurrentTheme={CurrentThemeName()} COLORTHEME={ReadTheme()}");
                ThemeCheck("ComponentManager.PropertyChanged");
            }));
        try
        {
            Autodesk.Windows.ComponentManager.PropertyChanged += cm;
            _unsubscribe.Add(() => Autodesk.Windows.ComponentManager.PropertyChanged -= cm);
        }
        catch (System.Exception ex) { Log($"ComponentManager.PropertyChanged subscribe threw {ProbeCCommands.Describe(ex)}"); }

        // Autodesk.AutoCAD.Internal is unsupported API: observed here only to learn whether it
        // fires; the product must not depend on it.
        try
        {
            var atc = Autodesk.AutoCAD.Internal.ActiveThemeColor.Instance();
            Autodesk.AutoCAD.Internal.ColorThemeChangedEventHandler internalTheme = (_, _) =>
                Dispatcher.BeginInvoke(() => Guard("Internal.ColorThemeChanged", () =>
                {
                    Log($"EVENT Internal.ActiveThemeColor.ColorThemeChanged COLORTHEME={ReadTheme()}");
                    ThemeCheck("Internal.ActiveThemeColor.ColorThemeChanged");
                }));
            atc.ColorThemeChanged += internalTheme;
            _unsubscribe.Add(() => atc.ColorThemeChanged -= internalTheme);
        }
        catch (System.Exception ex) { Log($"Internal.ActiveThemeColor subscribe threw {ProbeCCommands.Describe(ex)}"); }

        DocumentLockModeChangedEventHandler lockChanged = (_, e) => Guard("lock mode", () =>
            Log($"EVENT DocumentLockModeChanged cmd='{e.GlobalCommandName}' CurrentMode={e.CurrentMode} MyPreviousMode={e.MyPreviousMode} MyCurrentMode={e.MyCurrentMode} doc='{Path.GetFileName(e.Document?.Name ?? "")}'"));
        dm.DocumentLockModeChanged += lockChanged;
        _unsubscribe.Add(() => dm.DocumentLockModeChanged -= lockChanged);

        DocumentCollectionEventHandler activated = (_, e) => Guard("activated", () => Follow(e.Document, "DocumentActivated"));
        dm.DocumentActivated += activated;
        _unsubscribe.Add(() => dm.DocumentActivated -= activated);

        DocumentCollectionEventHandler toBeDestroyed = (_, e) => Guard("to be destroyed", () =>
        {
            Log($"EVENT DocumentToBeDestroyed '{Path.GetFileName(e.Document?.Name ?? "")}' (documents open before close: {dm.Count}); {ProbeUiWork.Forget(e.Document?.Database)}");
            if (e.Document is not null && ReferenceEquals(e.Document, _doc)) Follow(null, "the followed document is closing");
        });
        dm.DocumentToBeDestroyed += toBeDestroyed;
        _unsubscribe.Add(() => dm.DocumentToBeDestroyed -= toBeDestroyed);

        // Counter drift guard: if a CommandWillStart never gets its end event, the work buttons
        // would stay disabled for the rest of the run. On Idle with no command in progress the
        // counter is reset, and the reset is logged (it is itself a finding).
        EventHandler idle = (_, _) =>
        {
            if (_activeCommands == 0 || _doc is null) return;
            Guard("idle drift check", () =>
            {
                if (!string.IsNullOrEmpty(_doc.CommandInProgress) || ProbeCCommands.SysVar("CMDACTIVE") != "0") return;
                Log($"DRIFT: command counter was {_activeCommands} (last '{_lastCommand}') but on Idle CommandInProgress is empty and CMDACTIVE=0; reset to 0");
                _activeCommands = 0;
                UpdateStatus();
            });
        };
        CoreApp.Idle += idle;
        _unsubscribe.Add(() => CoreApp.Idle -= idle);
    }

    /// <summary>Follow one document's command events (E901: rebind to the active drawing).</summary>
    private void Follow(Document? doc, string why)
    {
        if (ReferenceEquals(doc, _doc) && doc is not null) return;
        Unfollow();
        _doc = doc;
        _activeCommands = 0;
        _lastCommand = "";
        if (doc is not null)
        {
            doc.CommandWillStart += OnCommandWillStart;
            doc.CommandEnded += OnCommandEnded;
            doc.CommandCancelled += OnCommandCancelled;
            doc.CommandFailed += OnCommandFailed;
        }
        Log($"FOLLOW {(doc is null ? "no document (controls disabled)" : $"'{Path.GetFileName(doc.Name)}'")} because {why}; {ProbeUiWork.State(doc)}");
        UpdateStatus();
    }

    private void Unfollow()
    {
        if (_doc is null) return;
        _doc.CommandWillStart -= OnCommandWillStart;
        _doc.CommandEnded -= OnCommandEnded;
        _doc.CommandCancelled -= OnCommandCancelled;
        _doc.CommandFailed -= OnCommandFailed;
        _doc = null;
    }

    private void OnCommandWillStart(object? sender, CommandEventArgs e) => Guard("command start", () =>
    {
        _activeCommands++;
        _lastCommand = e.GlobalCommandName;
        Log($"CMD WillStart '{e.GlobalCommandName}' active={_activeCommands} CMDACTIVE={ProbeCCommands.SysVar("CMDACTIVE")}");
        UpdateStatus();
    });

    private void OnCommandEnded(object? sender, CommandEventArgs e) => EndCommand("Ended", e.GlobalCommandName);
    private void OnCommandCancelled(object? sender, CommandEventArgs e) => EndCommand("Cancelled", e.GlobalCommandName);
    private void OnCommandFailed(object? sender, CommandEventArgs e) => EndCommand("Failed", e.GlobalCommandName);

    private void EndCommand(string how, string name) => Guard("command end", () =>
    {
        _activeCommands = Math.Max(0, _activeCommands - 1);
        Log($"CMD {how} '{name}' active={_activeCommands} CMDACTIVE={ProbeCCommands.SysVar("CMDACTIVE")}");
        UpdateStatus();
    });

    // ------------------------------------------------------------------ routes

    private void Route(string name, Action body)
    {
        if (_doc is null) { Log($"{name}: no document"); return; }
        if (_activeCommands > 0) { Log($"{name}: refused, a command is active ('{_lastCommand}')"); return; }
        body();
    }

    private void Route0()
    {
        var doc = _doc!;
        Log($"R0 begin, no lock, application context: {ProbeUiWork.State(doc)}");
        try { Log("R0 result: " + ProbeUiWork.WriteRecord(doc, "R0")); }
        catch (System.Exception ex) { Log($"R0 threw {ProbeCCommands.Describe(ex)}"); }
    }

    private void Route1()
    {
        var doc = _doc!;
        Log($"R1 begin: {ProbeUiWork.State(doc)}");
        try
        {
            using (doc.LockDocument())
            {
                Log($"R1 inside LockDocument(): {ProbeUiWork.State(doc)}");
                Log("R1 result: " + ProbeUiWork.WriteRecord(doc, "R1"));
            }
            Log($"R1 after the lock is disposed: {ProbeUiWork.State(doc)}");
        }
        catch (System.Exception ex) { Log($"R1 threw {ProbeCCommands.Describe(ex)}"); }
    }

    private async void Route2()
    {
        try
        {
            var doc = _doc!;
            Log($"R2 begin, calling DocumentCollection.ExecuteInCommandContextAsync: {ProbeUiWork.State(doc)}");
            var sw = Stopwatch.StartNew();
            // The callback body never throws: whether AutoCAD turns an exception into a faulted
            // Task or lets it through native frames is not known, so it is caught in here.
            await CoreApp.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                Guard("R2 callback", () =>
                {
                    var d = CoreApp.DocumentManager.MdiActiveDocument;
                    Log($"R2 inside the callback after {sw.ElapsedMilliseconds} ms: {ProbeUiWork.State(d)}");
                    try { Log("R2 result, no explicit lock: " + ProbeUiWork.WriteRecord(d, "R2")); }
                    catch (System.Exception ex)
                    {
                        Log($"R2 without an explicit lock threw {ProbeCCommands.Describe(ex)}; retrying under LockDocument()");
                        using (d.LockDocument()) Log("R2 result under LockDocument(): " + ProbeUiWork.WriteRecord(d, "R2"));
                    }
                });
                return Task.CompletedTask;
            }, null);
            Log($"R2 ExecuteInCommandContextAsync completed after {sw.ElapsedMilliseconds} ms: {ProbeUiWork.State(_doc)}");
        }
        catch (System.Exception ex) { Log($"R2 threw {ProbeCCommands.Describe(ex)}"); }
    }

    private void Route3()
    {
        var doc = _doc!;
        Log($"R3 begin, queuing '{ProbeUiSettings.WriteCommand}' with Document.SendStringToExecute(activate: true, wrapUpInactiveDoc: false, echoCommand: false): {ProbeUiWork.State(doc)}");
        try
        {
            doc.SendStringToExecute(ProbeUiSettings.WriteCommand + "\n", true, false, false);
            Log("R3 SendStringToExecute returned (the command runs later and logs its own result)");
        }
        catch (System.Exception ex) { Log($"R3 threw {ProbeCCommands.Describe(ex)}"); }
    }

    private async void Pick()
    {
        // Fall back to a registered command when the prompt cannot run here: AutoCAD normally
        // answers a refused prompt with PromptStatus.Error rather than an exception. A Cancel
        // (the user pressed Esc) is respected, not retried.
        string? fallbackBecause = null;
        try
        {
            Log($"PICK begin via ExecuteInCommandContextAsync: {ProbeUiWork.State(_doc)}");
            await CoreApp.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                Guard("PICK callback", () =>
                {
                    var d = CoreApp.DocumentManager.MdiActiveDocument;
                    Log("PICK " + ProbeUiWork.Pick(d.Editor, out var status));
                    if (status == Autodesk.AutoCAD.EditorInput.PromptStatus.Error) fallbackBecause = "the prompt returned Error";
                });
                return Task.CompletedTask;
            }, null);
        }
        catch (System.Exception ex) { fallbackBecause = $"ExecuteInCommandContextAsync threw {ProbeCCommands.Describe(ex)}"; }

        if (fallbackBecause is null) return;
        Log($"PICK falling back to SendStringToExecute '{ProbeUiSettings.PickCommand}' because {fallbackBecause}");
        try { _doc?.SendStringToExecute(ProbeUiSettings.PickCommand + "\n", true, false, false); }
        catch (System.Exception ex) { Log($"PICK fallback threw {ProbeCCommands.Describe(ex)}"); }
    }

    // ------------------------------------------------------------------ theme

    private static int ReadTheme()
    {
        try { return Convert.ToInt32(CoreApp.GetSystemVariable(ProbeUiSettings.ColorThemeVariable), System.Globalization.CultureInfo.InvariantCulture); }
        catch (System.Exception) { return -1; }
    }

    private static string CurrentThemeName()
    {
        try { return Autodesk.Windows.ComponentManager.CurrentTheme?.ToString() ?? "null"; }
        catch (System.Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    /// <summary>Restyles when COLORTHEME no longer matches the applied theme; the THEME line
    /// names the signal that noticed. The button also reports "unchanged".</summary>
    private void ThemeCheck(string source)
    {
        int value = ReadTheme();
        int wanted = value == 1 ? 1 : 0;
        if (wanted != _theme) ApplyTheme(value, source);
        else if (source.Contains("button", StringComparison.Ordinal)) Log($"THEME check from {source}: unchanged (COLORTHEME={value})");
    }

    /// <summary>Replaces the merged theme dictionary; every DynamicResource reference, in code
    /// and in the XAML panel, picks up the new brushes.</summary>
    private void ApplyTheme(int value, string why)
    {
        var sw = Stopwatch.StartNew();
        bool dark = value != 1;   // COLORTHEME 0 = dark, 1 = light; anything else is logged and treated as dark
        var dict = new ResourceDictionary();
        foreach (var (key, d, l) in ProbeUiSettings.Brushes)
        {
            uint c = dark ? d : l;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
            brush.Freeze();
            dict[key] = brush;
        }
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
        _theme = dark ? 0 : 1;
        Log($"THEME applied {(dark ? "dark" : "light")} for COLORTHEME={value}{(value is 0 or 1 ? "" : " (unexpected value, dark used)")} because {why}, in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        UpdateStatus();
    }

    // ------------------------------------------------------------------ status and log

    private void UpdateStatus()
    {
        bool idle = _doc is not null && _activeCommands == 0;
        foreach (var b in _workButtons) b.IsEnabled = idle;
        string dpi;
        try { var d = VisualTreeHelper.GetDpi(this); dpi = $"{d.DpiScaleX:0.##}"; } catch (System.Exception) { dpi = "?"; }
        _status.Text = (_doc is null ? "Document: none. Open a drawing; the buttons stay disabled until then." : $"Document: {Path.GetFileName(_doc.Name)}")
                       + $"   |   Command active: {(_activeCommands > 0 ? $"yes ('{_lastCommand}'): work buttons disabled" : "no")}"
                       + $"   |   Theme: {(_theme == 0 ? "dark" : "light")} (COLORTHEME={ReadTheme()})   |   DPI scale: {dpi}";
    }

    private void LogSize(string when)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        Log($"SIZE {when}: actual {ActualWidth:0.#} x {ActualHeight:0.#} DIP = {ActualWidth * dpi.DpiScaleX:0} x {ActualHeight * dpi.DpiScaleY:0} px; " +
            $"requested {Width:0.#} x {Height:0.#}; minimum {MinWidth:0} x {MinHeight:0}; DpiScale {dpi.DpiScaleX} x {dpi.DpiScaleY} ({dpi.PixelsPerInchX} ppi); " +
            $"TransformToDevice M11={m?.M11} M22={m?.M22}; GetDpiForWindow={ProbeUiNative.DpiForWindow(hwnd)}; position ({Left:0}, {Top:0}); " +
            $"WorkArea {SystemParameters.WorkArea.Width:0} x {SystemParameters.WorkArea.Height:0}; state {WindowState}");
    }

    /// <summary>Timestamped line to probeUI.log and the log pane.</summary>
    public void Log(string message)
    {
        var line = _log.Write(message);
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendPane(line)); return; }
        AppendPane(line);
    }

    private void AppendPane(string line)
    {
        _paneLines.Enqueue(line);
        while (_paneLines.Count > ProbeUiSettings.LogPaneMaxLines) _paneLines.Dequeue();
        _logPane.Text = string.Join(Environment.NewLine, _paneLines);
        _logPane.ScrollToEnd();
    }

    /// <summary>No exception from a handler may reach AutoCAD's message loop.</summary>
    private void Guard(string what, Action body)
    {
        try { body(); }
        catch (System.Exception ex)
        {
            // If logging itself fails there is nowhere left to report to; ProbeCLog counts
            // file failures, and nothing may escape into AutoCAD's message loop.
            try { Log($"{ProbeCSettings.FailCode} {what} threw {ProbeCCommands.Describe(ex)}"); } catch (System.Exception) { }
        }
    }
}
