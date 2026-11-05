using System.Diagnostics.CodeAnalysis;

namespace SheetVol.Core.Presentation;

/// <summary>Edits made in the dialog and not yet regenerated, for one drawing (E907).</summary>
/// <param name="Run">The edited run definition, including its draw options (<see cref="RunDefinition.Draw"/>) and per-segment overrides.</param>
public sealed record PendingEdit(RunDefinition Run);

/// <summary>
/// In-memory store of edits not yet regenerated, keyed by drawing name (E907). The host
/// keeps one instance for the Civil 3D session and passes it to every
/// <see cref="DialogViewModel"/> it creates, so closing and reopening the dialog restores
/// the edits. Nothing here is ever written to the drawing.
/// </summary>
public sealed class PendingEdits
{
    private readonly Dictionary<string, PendingEdit> _byDrawing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of drawings with pending edits.</summary>
    public int Count => _byDrawing.Count;

    /// <summary>Gets the pending edit for a drawing.</summary>
    /// <param name="drawingName">Drawing name as reported by <see cref="ISheetVolSession.ActiveDrawing"/>.</param>
    /// <param name="edit">The pending edit, when there is one.</param>
    public bool TryGet(string drawingName, [NotNullWhen(true)] out PendingEdit? edit)
        => _byDrawing.TryGetValue(drawingName, out edit);

    /// <summary>Stores or replaces the pending edit for a drawing.</summary>
    /// <param name="drawingName">Drawing name.</param>
    /// <param name="edit">The edit.</param>
    public void Save(string drawingName, PendingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(drawingName);
        _byDrawing[drawingName] = edit ?? throw new ArgumentNullException(nameof(edit));
    }

    /// <summary>Drops the pending edit for a drawing.</summary>
    /// <param name="drawingName">Drawing name.</param>
    /// <returns>True when there was one.</returns>
    public bool Clear(string drawingName) => _byDrawing.Remove(drawingName);
}
