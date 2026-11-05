namespace SheetVol.UI.Dialog;

/// <summary>
/// Accessible names the view needs that <c>SheetVol.Core.Presentation.DialogText</c> does not
/// carry (E910). Everything visible comes from DialogText; these are read by screen readers
/// only. Candidates to move into DialogText when its owner next touches it.
/// </summary>
public static class ViewText
{
    /// <summary>Accessible name of the segments grid.</summary>
    public const string SegmentsGrid = "Segments";

    /// <summary>Accessible name of the run problem list.</summary>
    public const string RunProblems = "Problems for the run";

    /// <summary>Accessible name of the regen progress bar.</summary>
    public const string Progress = "Regenerate progress";

    /// <summary>Accessible name of the left panel.</summary>
    public const string LeftPanel = "Run settings";

    /// <summary>Accessible name of the per-comparison card.</summary>
    public const string ComparisonCard = "Comparison";

    /// <summary>Accessible name of the area unit dropdown in the area column header.</summary>
    public const string AreaUnit = "Area unit";

    /// <summary>Accessible name of the splitter between the chart and the grid.</summary>
    public const string Splitter = "Resize the chart and the grid";
}
