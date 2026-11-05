using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SheetVol.Core.Presentation;

namespace SheetVol.UI.Dialog;

/// <summary>
/// Keyboard behaviour of the dialog's text fields (E910): Enter commits the field (pushes the
/// text to its <see cref="TextFieldViewModel"/>, which validates), Esc reverts it to the last
/// valid value. Fields commit on focus loss too (the binding's default trigger).
/// </summary>
public static class FieldBehavior
{
    /// <summary>Turns the behaviour on for a TextBox bound to <c>TextFieldViewModel.Text</c>.</summary>
    public static readonly DependencyProperty CommitOnEnterProperty =
        DependencyProperty.RegisterAttached("CommitOnEnter", typeof(bool), typeof(FieldBehavior),
            new PropertyMetadata(false, OnCommitOnEnterChanged));

    /// <summary>Gets <see cref="CommitOnEnterProperty"/>.</summary>
    public static bool GetCommitOnEnter(DependencyObject element) => (bool)element.GetValue(CommitOnEnterProperty);

    /// <summary>Sets <see cref="CommitOnEnterProperty"/>.</summary>
    public static void SetCommitOnEnter(DependencyObject element, bool value) => element.SetValue(CommitOnEnterProperty, value);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true) box.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (binding is null) return;
        if (e.Key == Key.Enter)
        {
            binding.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && binding.ResolvedSource is TextFieldViewModel field)
        {
            field.Revert();
            binding.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }
}

/// <summary>Names the <see cref="TextFieldViewModel"/> an editable grid column edits, so the grid's code-behind can gate and revert it.</summary>
public static class GridEdit
{
    /// <summary>One of <see cref="Begin"/>, <see cref="End"/>, <see cref="Ohwm"/>; null for read-only columns.</summary>
    public static readonly DependencyProperty FieldProperty =
        DependencyProperty.RegisterAttached("Field", typeof(string), typeof(GridEdit), new PropertyMetadata(null));

    /// <summary>Begin station column.</summary>
    public const string Begin = "Begin";

    /// <summary>End station column.</summary>
    public const string End = "End";

    /// <summary>HCFCD per-segment OHWM column.</summary>
    public const string Ohwm = "Ohwm";

    /// <summary>Gets <see cref="FieldProperty"/>.</summary>
    public static string? GetField(DependencyObject element) => (string?)element.GetValue(FieldProperty);

    /// <summary>Sets <see cref="FieldProperty"/>.</summary>
    public static void SetField(DependencyObject element, string? value) => element.SetValue(FieldProperty, value);

    /// <summary>The field a column edits on a row, or null.</summary>
    public static TextFieldViewModel? FieldOf(DataGridColumn? column, object? item)
        => item is SegmentRowViewModel row && column is not null
            ? GetField(column) switch
            {
                Begin => row.BeginStation,
                End => row.EndStation,
                Ohwm => row.Ohwm,
                _ => null,
            }
            : null;
}
