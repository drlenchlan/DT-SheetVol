using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SheetVol.Core;
using SheetVol.Core.Presentation;
using SheetVol.UI.Dialog.Themes;

namespace SheetVol.UI.Dialog;

/// <summary>
/// The SVDIALOG window (HANDOFF.md 6.14): XAML bound to C4's <see cref="DialogViewModel"/>
/// plus thin code-behind for what XAML cannot express: grid cell editing (Enter commits,
/// Esc reverts, read-only cells refuse edit), the total row following the grid's columns,
/// the theme swap, and closing through the view model. No Autodesk types, so it loads and
/// renders in a plain WPF process (see <see cref="UiSmoke"/>).
/// </summary>
public partial class DialogWindow : Window
{
    private bool _closing;

    /// <summary>Creates the window and its view model over any session.</summary>
    /// <param name="session">The engine door: in Civil 3D the host's editor-side decorator; elsewhere a design session.</param>
    /// <param name="pendingEdits">Session-lifetime store of edits not yet regenerated (E907); a fresh one when null.</param>
    /// <param name="config">Formats and defaults; <see cref="PresentationConfig.Default"/> when null.</param>
    /// <param name="theme">Initial colour theme.</param>
    /// <param name="modeNote">Text of a strip at the top of the window, for example that values are a design fixture; none when null.</param>
    public DialogWindow(
        ISheetVolSession session,
        PendingEdits? pendingEdits = null,
        PresentationConfig? config = null,
        DialogTheme theme = DialogTheme.Dark,
        string? modeNote = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Resources.MergedDictionaries.Add(CreateTheme(theme));
        Resources.MergedDictionaries.Add(new SheetVolStyles());
        Theme = theme;

        ViewModel = new DialogViewModel(session, pendingEdits ?? new PendingEdits(), config);
        InitializeComponent();
        DataContext = ViewModel;

        if (!string.IsNullOrEmpty(modeNote))
        {
            ModeNoteText.Text = modeNote;
            ModeNoteStrip.Visibility = Visibility.Visible;
        }

        ViewModel.CloseRequested += OnCloseRequested;
        SegmentsGrid.BeginningEdit += OnBeginningEdit;
        SegmentsGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        SegmentsGrid.CellEditEnding += OnCellEditEnding;
        SegmentsGrid.PreviewKeyDown += OnGridPreviewKeyDown;
        SegmentsGrid.SelectionChanged += OnGridSelectionChanged;
        SegmentsGrid.LayoutUpdated += (_, _) => SyncTotalsRow();
        SegmentsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnGridScrollChanged));
    }

    /// <summary>The window's view model.</summary>
    public DialogViewModel ViewModel { get; }

    /// <summary>The colour theme in use.</summary>
    public DialogTheme Theme { get; private set; }

    /// <summary>Swaps the colour dictionary; every brush is a DynamicResource, so the open window restyles (E908).</summary>
    public void ApplyTheme(DialogTheme theme)
    {
        if (theme == Theme) return;
        Resources.MergedDictionaries[0] = CreateTheme(theme);
        Theme = theme;
    }

    private static ResourceDictionary CreateTheme(DialogTheme theme)
        => theme == DialogTheme.Light ? new LightTheme() : new DarkTheme();

    // ---- Closing ---------------------------------------------------------------------------------

    /// <summary>The title-bar close goes through the view model (pending edits kept, highlight cleared); refused during a regen, like Close.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        if (ViewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }
        _closing = true;
        ViewModel.Close();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    // ---- Grid editing (E903, E910) ------------------------------------------------------------------

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        var field = GridEdit.FieldOf(e.Column, e.Row.Item);
        if (field is null || !field.IsEditable) e.Cancel = true;
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox box) return;
        box.Focus();
        if (e.EditingEventArgs is TextCompositionEventArgs typed)
        {
            // Typing on a selected cell starts the edit with that character, as in a spreadsheet.
            box.Text = typed.Text;
            box.CaretIndex = box.Text.Length;
        }
        else
        {
            box.SelectAll();
        }
    }

    /// <summary>Enter (or leaving the cell) commits the typed text to the field, which validates it; Esc discards it.</summary>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.EditingElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Esc on a cell that shows an error (not editing) reverts it to its last valid value.</summary>
    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var cell = SegmentsGrid.CurrentCell;
        if (cell.Column is null || IsEditing(cell)) return;
        var field = GridEdit.FieldOf(cell.Column, cell.Item);
        if (field is null || !field.HasError) return;
        field.Revert();
        e.Handled = true;
    }

    private bool IsEditing(DataGridCellInfo info)
    {
        if (info.Column?.GetCellContent(info.Item)?.Parent is DataGridCell cell) return cell.IsEditing;
        return false;
    }

    /// <summary>A selection made in the chart scrolls the grid to it.</summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SegmentsGrid.SelectedItem is { } item) SegmentsGrid.ScrollIntoView(item);
    }

    // ---- Total row --------------------------------------------------------------------------------------

    /// <summary>
    /// Each total cell takes the width and visibility of the grid column (or span of columns) named
    /// in its Tag, "3" or "0-1", so the totals line up under the grid.
    /// </summary>
    private void SyncTotalsRow()
    {
        foreach (var child in TotalsRow.Children)
        {
            if (child is not FrameworkElement cell || TotalSpanWidth(cell) is not double width) continue;
            var visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (cell.Visibility != visibility) cell.Visibility = visibility;
            if (visibility == Visibility.Visible && (double.IsNaN(cell.Width) || Math.Abs(cell.Width - width) > 0.25)) cell.Width = width;
        }
    }

    /// <summary>Sum of the visible column widths a total cell spans; null when its Tag names no column.</summary>
    internal double? TotalSpanWidth(FrameworkElement cell)
    {
        if (cell.Tag is not string tag) return null;
        var parts = tag.Split('-');
        if (!int.TryParse(parts[0], out var first)) return null;
        var last = parts.Length > 1 && int.TryParse(parts[1], out var end) ? end : first;
        var columns = SegmentsGrid.Columns;
        if (first < 0 || last >= columns.Count || last < first) return null;
        var width = 0.0;
        for (var i = first; i <= last; i++)
            if (columns[i].Visibility == Visibility.Visible && !double.IsNaN(columns[i].ActualWidth)) width += columns[i].ActualWidth;
        return width;
    }

    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer { Name: "DG_ScrollViewer" }) TotalsShift.X = -e.HorizontalOffset;
    }

    /// <summary>Finds the first visual descendant of a type, for diagnostics and the smoke check.</summary>
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
