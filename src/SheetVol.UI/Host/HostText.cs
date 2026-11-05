using SheetVol.Core.Presentation;

namespace SheetVol.UI.Host;

/// <summary>
/// Every user-facing string of the host and the editor side of the session: command-line
/// lines and prompts. Dialog labels live in <see cref="DialogText"/>. Lines that report a
/// failure carry a stable SV-E9xx code (HANDOFF.md 6.13); entries ending in <c>Format</c>
/// are composite format strings.
/// </summary>
internal static class HostText
{
    /// <summary>SVDIALOG with no engine session: {0} variable name, {1} value that enables the design fixture.</summary>
    public const string NoEngineSessionFormat =
        "SV-E900 The SheetVol engine session is not available in this build. Set {0}={1} before starting Civil 3D to open the dialog against the design fixture.";

    /// <summary>Strip at the top of the window in design-fixture mode.</summary>
    public const string DesignModeNote =
        "Design fixture: canned T2 values on a straight alignment along +X. Nothing is read from or written to the drawing.";

    /// <summary>SVDIALOG failed: {0} message.</summary>
    public const string OpenFailedFormat = "SV-E901 SVDIALOG could not open the dialog: {0}";

    /// <summary>The dialog could not follow the active drawing: {0} message.</summary>
    public const string RebindFailedFormat = "SV-E901 The dialog could not rebind to the active drawing: {0}";

    /// <summary>Code of a failed read from the drawing (Validate, station text, object lists).</summary>
    public const string ReadFailedCode = "SV-E901";

    /// <summary>A read from the drawing failed: {0} message. Shown as a run problem and on the command line.</summary>
    public const string ReadFailedMessageFormat = "The dialog could not read the drawing: {0}";

    /// <summary>Editor work queued for one drawing found another drawing active when it ran.</summary>
    public const string DrawingChanged = "The active drawing changed before the request ran. Try again.";

    /// <summary>Releasing the dialog's resources on close failed: {0} message.</summary>
    public const string CloseFailedFormat = "SV-E901 The dialog closed, but releasing it did not complete: {0}";

    /// <summary>Theme follow failed: {0} message.</summary>
    public const string ThemeFailedFormat = "SV-E908 The dialog could not follow the colour theme: {0}";

    /// <summary>Highlight failed: {0} message.</summary>
    public const string HighlightFailedFormat = "SV-E911 Highlight did not complete: {0}";

    /// <summary>Zoom to failed: {0} message.</summary>
    public const string ZoomFailedFormat = "SV-E911 Zoom to did not complete: {0}";

    /// <summary>Highlight or zoom on a segment without a valid boundary.</summary>
    public const string NoBoundaryForSegment = "This segment has no valid boundary yet. Fix the problem shown under its row.";

    /// <summary>Zoom to in paper space.</summary>
    public const string ZoomNeedsModelSpace = "Zoom to works in model space. Switch to the Model tab or into a model-space viewport.";

    /// <summary>Editor work refused while a command runs (E905).</summary>
    public const string CommandActive = DialogText.BlockedCommandActive;

    /// <summary>Editor work with no drawing open (E901).</summary>
    public const string NoDrawing = DialogText.NoDrawingOpen;

    /// <summary>Alignment pick prompt.</summary>
    public const string PickAlignmentPrompt = "\nSelect an alignment: ";

    /// <summary>Alignment pick reject message.</summary>
    public const string PickAlignmentReject = "\nThat is not an alignment.";

    /// <summary>Station pick prompt: {0} alignment name.</summary>
    public const string PickStationPromptFormat = "\nPick a station on alignment {0}: ";

    /// <summary>Station pick with no alignment chosen.</summary>
    public const string PickStationNoAlignment = DialogText.BlockedNoAlignment;

    /// <summary>Station pick on an alignment the drawing does not have: {0} name.</summary>
    public const string AlignmentNotFoundFormat = "Alignment {0} was not found in the drawing.";

    /// <summary>Station pick outside the alignment: {0} name.</summary>
    public const string PointNotBesideAlignmentFormat = "The picked point is not beside alignment {0}.";

    /// <summary>SVDIALOGSMOKE first line: {0} output folder.</summary>
    public const string SmokeStartFormat = "SVDIALOGSMOKE: rendering into {0}";

    /// <summary>SVDIALOGSMOKE last line: {0} PASS or FAIL.</summary>
    public const string SmokeResultFormat = "SVDIALOGSMOKE: {0}";

    /// <summary>SVDIALOGSMOKE threw: {0} message.</summary>
    public const string SmokeFailedFormat = "SV-E909 SVDIALOGSMOKE stopped: {0}";
}
