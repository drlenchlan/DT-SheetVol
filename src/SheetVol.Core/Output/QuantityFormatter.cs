using System.Globalization;
using SheetVol.Core.Volumes;

namespace SheetVol.Core.Output;

/// <summary>
/// Turns run-unit quantities into table cell text with the configured culture and formats
/// (E508). Volumes pass through the zero tolerance (E409) and are rounded in decimal so the
/// text shown and the value summed into a total row agree (A6). Never used for exports.
/// </summary>
public sealed class QuantityFormatter
{
    private readonly ReportConfig _config;

    /// <summary>Builds a formatter. The config must have passed <see cref="ReportConfig.Validate"/>.</summary>
    public QuantityFormatter(QuantityUnits units, ReportConfig config)
    {
        Units = units;
        _config = config;
        Culture = config.TableCulture();
    }

    /// <summary>The unit converter, for labels and conversions.</summary>
    public QuantityUnits Units { get; }

    /// <summary>Culture used for every cell (E508).</summary>
    public CultureInfo Culture { get; }

    private FormatConfig F => _config.Formats;

    /// <summary>A volume in the run unit, noise-suppressed (E409) and rounded to the volume decimals.</summary>
    public decimal RoundVolume(double runUnitVolume)
        => DisplayRounding.Round(DisplayRounding.SuppressNoise(runUnitVolume, _config.Volumes.Tolerances.VolumeZeroTolerance), F.VolumeDecimals);

    /// <summary>A6 column of volumes in the run unit.</summary>
    public RoundedColumn VolumeColumn(IReadOnlyList<double> runUnitVolumes)
        => DisplayRounding.Column(runUnitVolumes, F.VolumeDecimals, _config.Volumes.Tolerances.VolumeZeroTolerance);

    /// <summary>A6 column of areas in <paramref name="unit"/>. No zero tolerance: area noise is below every display precision.</summary>
    public RoundedColumn AreaColumn(IReadOnlyList<double> areasInUnit, AreaUnit unit)
        => DisplayRounding.Column(areasInUnit, _config.AreaDecimals(unit), 0);

    /// <summary>A number with the given decimals, grouped when configured: <c>#,##0</c> or <c>#,##0.00</c>.</summary>
    public string Number(decimal rounded, int decimals)
    {
        string pattern = (F.UseThousandsSeparator ? "#,##0" : "0") + (decimals > 0 ? "." + new string('0', decimals) : "");
        return (rounded == 0m ? 0m : rounded).ToString(pattern, Culture);
    }

    /// <summary>Volume cell: <c>100 CY</c>.</summary>
    public string VolumeCell(decimal roundedVolume)
        => string.Format(Culture, F.VolumeCellTemplate, Number(roundedVolume, F.VolumeDecimals), Units.VolumeLabel);

    /// <summary>
    /// Net cell (A5): <c>50 CY (CUT)</c> when positive, <c>50 CY (FILL)</c> when negative, and
    /// <c>0 CY</c> with no tag when the displayed value is zero (E414), which includes exact zero,
    /// noise below the zero tolerance (E409) and values that round to zero.
    /// </summary>
    public string NetCell(decimal roundedNet)
    {
        if (roundedNet == 0m) return VolumeCell(0m);
        string tag = roundedNet > 0 ? F.CutTag : F.FillTag;
        return string.Format(Culture, F.NetCellTemplate, Number(Math.Abs(roundedNet), F.VolumeDecimals), Units.VolumeLabel, tag);
    }

    /// <summary>Area number in <paramref name="unit"/> with that unit's decimals, without the label.</summary>
    public string AreaNumber(decimal roundedArea, AreaUnit unit) => Number(roundedArea, _config.AreaDecimals(unit));

    /// <summary>Area value with its label: <c>100,000 SF</c>.</summary>
    public string AreaCell(decimal roundedArea, AreaUnit unit)
        => string.Format(Culture, F.VolumeCellTemplate, AreaNumber(roundedArea, unit), Units.AreaLabel(unit));

    /// <summary>Area in <paramref name="unit"/> from square drawing units, rounded to that unit's decimals.</summary>
    public decimal RoundArea(double squareDrawingUnits, AreaUnit unit)
        => DisplayRounding.Round(Units.ToArea(squareDrawingUnits, unit), _config.AreaDecimals(unit));

    /// <summary>A cut or fill factor for a footer (E410).</summary>
    public string Factor(double factor) => factor.ToString(F.FactorFormat, Culture);

    /// <summary>
    /// Coverage as a percent with the configured decimals and a percent sign. A flagged value is
    /// floored, so a coverage below 100 % never displays as 100 %.
    /// </summary>
    public string CoveragePercent(double coverage, bool flagged)
    {
        double pct = coverage * 100;
        decimal shown;
        if (!flagged) shown = DisplayRounding.Round(pct, F.CoverageDecimals);
        else
        {
            decimal scale = (decimal)Math.Pow(10, F.CoverageDecimals);
            shown = Math.Floor((decimal)pct * scale) / scale;
        }
        return Number(shown, F.CoverageDecimals) + Culture.NumberFormat.PercentSymbol;
    }

    /// <summary>
    /// A value for a footnote: the column's decimals plus <see cref="FormatConfig.FootnoteExtraDecimals"/>,
    /// with its unit label (E413, E420).
    /// </summary>
    public string FootnoteValue(double unrounded, int columnDecimals, string unitLabel)
    {
        int d = columnDecimals + F.FootnoteExtraDecimals;
        return string.Format(Culture, F.VolumeCellTemplate, Number(DisplayRounding.Round(unrounded, d), d), unitLabel);
    }

    /// <summary>An unrounded net for a footnote, tagged the same way as <see cref="NetCell"/>.</summary>
    public string FootnoteNet(double unroundedNet)
    {
        int d = F.VolumeDecimals + F.FootnoteExtraDecimals;
        decimal r = DisplayRounding.Round(DisplayRounding.SuppressNoise(unroundedNet, _config.Volumes.Tolerances.VolumeZeroTolerance), d);
        if (r == 0m) return string.Format(Culture, F.VolumeCellTemplate, Number(0m, d), Units.VolumeLabel);
        return string.Format(Culture, F.NetCellTemplate, Number(Math.Abs(r), d), Units.VolumeLabel, r > 0 ? F.CutTag : F.FillTag);
    }
}
