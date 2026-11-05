namespace SheetVol.Core.Volumes;

/// <summary>
/// Rounding for display and the A6 total rule. Values are rounded in <see cref="decimal"/>,
/// half away from zero, so the number shown and the number summed into the total row are the
/// same number. A double converts to decimal at 15 significant digits, so 2.675 rounds to 2.68
/// as a reader expects.
/// </summary>
public static class DisplayRounding
{
    /// <summary>Largest number of display decimals accepted.</summary>
    public const int MaxDecimals = 10;

    /// <summary>
    /// Rounds <paramref name="value"/> to <paramref name="decimals"/> places, half away from zero.
    /// A result that rounds to zero is a positive zero, so it never formats as "-0".
    /// Throws on NaN, infinity or a magnitude outside the decimal range: callers must not
    /// reach display with a value that is not a number.
    /// </summary>
    public static decimal Round(double value, int decimals)
    {
        if (decimals < 0 || decimals > MaxDecimals)
            throw new ArgumentOutOfRangeException(nameof(decimals), decimals, $"Decimals must be 0 to {MaxDecimals}.");
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot display a value that is not a finite number.");
        decimal d;
        try
        {
            d = (decimal)value;
        }
        catch (OverflowException ex)
        {
            throw new ArgumentOutOfRangeException($"Value {value} is too large to display.", ex);
        }
        decimal r = decimal.Round(d, decimals, MidpointRounding.AwayFromZero);
        return r == 0m ? 0m : r;
    }

    /// <summary>E409. Returns 0 when the magnitude is below <paramref name="tolerance"/>, else the value unchanged.</summary>
    public static double SuppressNoise(double value, double tolerance) => Math.Abs(value) < tolerance ? 0.0 : value;

    /// <summary>
    /// A6 for one column. Each row is noise-suppressed then rounded; the total row is the sum of
    /// the rounded rows. The unrounded total is the sum of the raw values, untouched by the zero
    /// tolerance, for the export and the footnote (E413, E420).
    /// </summary>
    public static RoundedColumn Column(IReadOnlyList<double> values, int decimals, double zeroTolerance)
    {
        var rows = new decimal[values.Count];
        double unrounded = 0;
        for (int i = 0; i < values.Count; i++)
        {
            rows[i] = Round(SuppressNoise(values[i], zeroTolerance), decimals);
            unrounded += values[i];
        }
        decimal rowsTotal = rows.Sum();
        decimal unroundedRounded = Round(SuppressNoise(unrounded, zeroTolerance), decimals);
        return new RoundedColumn(rows, rowsTotal == 0m ? 0m : rowsTotal, unrounded, unroundedRounded);
    }
}

/// <summary>
/// One table column under the A6 rule.
/// </summary>
/// <param name="Rows">Each row as displayed (rounded).</param>
/// <param name="RowsTotal">The total row: the sum of <paramref name="Rows"/>.</param>
/// <param name="UnroundedTotal">Sum of the unrounded values, for the export.</param>
/// <param name="UnroundedTotalRounded">The unrounded total rounded to the same decimals.</param>
public sealed record RoundedColumn(
    IReadOnlyList<decimal> Rows,
    decimal RowsTotal,
    double UnroundedTotal,
    decimal UnroundedTotalRounded)
{
    /// <summary>True when the total row differs from the rounded true total, so the footnote is needed (E413, E420).</summary>
    public bool TotalDiffers => RowsTotal != UnroundedTotalRounded;
}
