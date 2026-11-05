namespace SheetVol.Core.Output;

/// <summary>Which table a model describes.</summary>
public enum TableKind
{
    /// <summary>Per-sheet table (6.8).</summary>
    Segment,

    /// <summary>Master table (6.8).</summary>
    Master,

    /// <summary>HCFCD label-bound table (6.16).</summary>
    Hcfcd,
}

/// <summary>What a row holds, so writers can grow or shrink an analysis block in place (E505) and tests can find rows without positions.</summary>
public enum RowKind
{
    /// <summary>Table title.</summary>
    Title,

    /// <summary>Analysis name row heading a block.</summary>
    AnalysisHeader,

    /// <summary>Column header row.</summary>
    ColumnHeader,

    /// <summary>Per-sheet table: cut row.</summary>
    Cut,

    /// <summary>Per-sheet table: fill row.</summary>
    Fill,

    /// <summary>Per-sheet table: net row.</summary>
    Net,

    /// <summary>Per-sheet table: optional area row.</summary>
    Area,

    /// <summary>Master table: one sheet.</summary>
    Segment,

    /// <summary>Master table: outside-limits row (6.5).</summary>
    OutsideLimits,

    /// <summary>Master table: total row (A6).</summary>
    Total,

    /// <summary>Footer or footnote line.</summary>
    Footer,

    /// <summary>Template row with fixed text (6.16).</summary>
    Fixed,

    /// <summary>HCFCD material row added under the material header (6.16).</summary>
    Material,
}

/// <summary>A rectangular merge, inclusive, zero-based.</summary>
/// <param name="FirstRow">First row.</param>
/// <param name="FirstColumn">First column.</param>
/// <param name="LastRow">Last row.</param>
/// <param name="LastColumn">Last column.</param>
public sealed record CellRange(int FirstRow, int FirstColumn, int LastRow, int LastColumn);

/// <summary>One table row. <paramref name="Cells"/> always has exactly the table's column count.</summary>
/// <param name="Kind">What the row holds.</param>
/// <param name="Cells">Cell text, left to right. Empty string for a blank cell.</param>
/// <param name="AnalysisId">The analysis this row belongs to, when it belongs to one.</param>
/// <param name="SegmentId">The segment this row reports, when it reports one.</param>
public sealed record TableRowModel(RowKind Kind, IReadOnlyList<string> Cells, Guid? AnalysisId = null, Guid? SegmentId = null);

/// <summary>
/// A table described without any Autodesk type, for the model and paper writers (A2, A3) to
/// render. Writers create the table once and afterwards update cell text only (6.7, E502).
/// </summary>
/// <param name="Kind">Which table.</param>
/// <param name="SegmentId">The segment of a per-sheet or HCFCD table; null for the master table.</param>
/// <param name="ColumnCount">Number of columns.</param>
/// <param name="Rows">Rows top to bottom.</param>
/// <param name="Merges">Merged ranges.</param>
/// <param name="Style">Table style, layer and text height from config.</param>
public sealed record TableModel(
    TableKind Kind,
    Guid? SegmentId,
    int ColumnCount,
    IReadOnlyList<TableRowModel> Rows,
    IReadOnlyList<CellRange> Merges,
    TableStyleSettings Style)
{
    /// <summary>Cell text at a zero-based row and column.</summary>
    public string Cell(int row, int column) => Rows[row].Cells[column];

    /// <summary>Rows of one kind, top to bottom.</summary>
    public IEnumerable<TableRowModel> RowsOf(RowKind kind) => Rows.Where(r => r.Kind == kind);
}

/// <summary>Collects rows and merges while a builder assembles a table.</summary>
internal sealed class TableBuilder(int columns)
{
    private readonly List<TableRowModel> _rows = [];
    private readonly List<CellRange> _merges = [];

    public int Columns { get; } = columns;

    public int Count => _rows.Count;

    /// <summary>Adds a row; pads with blanks to the column count, throws when given too many cells.</summary>
    public void Row(RowKind kind, IReadOnlyList<string> cells, Guid? analysisId = null, Guid? segmentId = null)
    {
        if (cells.Count > Columns) throw new ArgumentException($"Row has {cells.Count} cells; the table has {Columns} columns.");
        var full = cells.Concat(Enumerable.Repeat("", Columns - cells.Count)).ToList();
        _rows.Add(new TableRowModel(kind, full, analysisId, segmentId));
    }

    /// <summary>Adds a row whose text spans every column.</summary>
    public void SpanRow(RowKind kind, string text, Guid? analysisId = null, Guid? segmentId = null)
    {
        Row(kind, [text], analysisId, segmentId);
        if (Columns > 1) _merges.Add(new CellRange(_rows.Count - 1, 0, _rows.Count - 1, Columns - 1));
    }

    public TableModel Build(TableKind kind, Guid? segmentId, TableStyleSettings style)
        => new(kind, segmentId, Columns, _rows.ToList(), _merges.ToList(), style);
}
