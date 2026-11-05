using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SheetVol.UI.Dialog;

/// <summary>Value converters used by the dialog XAML through <c>{x:Static}</c>, so no resource lookup is needed.</summary>
public static class Converters
{
    /// <summary>True is Visible, anything else Collapsed.</summary>
    public static IValueConverter TrueToVisible { get; } = new VisibilityConverter(visibleWhen: true);

    /// <summary>False is Visible, anything else Collapsed.</summary>
    public static IValueConverter FalseToVisible { get; } = new VisibilityConverter(visibleWhen: false);

    /// <summary>A non-empty string or a non-null object is Visible; null or empty Collapsed.</summary>
    public static IValueConverter NotEmptyToVisible { get; } = new NotEmptyConverter();

    /// <summary>Inverts a boolean.</summary>
    public static IValueConverter Not { get; } = new NotConverter();

    private sealed class VisibilityConverter(bool visibleWhen) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b && b == visibleWhen ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class NotEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value switch
            {
                null => Visibility.Collapsed,
                string s when s.Length == 0 => Visibility.Collapsed,
                _ => Visibility.Visible,
            };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    }
}

/// <summary>
/// Gives string headers a wrapping text template (no clipped header text at 125–200 %, E909)
/// and leaves element headers (the 2D area header with its unit dropdown) to the default.
/// </summary>
public sealed class HeaderTemplateSelector : DataTemplateSelector
{
    /// <summary>Template used for string headers.</summary>
    public DataTemplate? TextTemplate { get; set; }

    /// <inheritdoc/>
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is string ? TextTemplate : null;
}

/// <summary>
/// Carries a DataContext into places outside the visual tree, such as DataGrid column
/// headers and visibility (columns are not visual elements). Declare it in a resource
/// dictionary with <c>Data="{Binding}"</c> and bind through <c>Source={StaticResource ...}</c>.
/// </summary>
public sealed class BindingProxy : Freezable
{
    /// <summary>The carried value.</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    /// <summary>The carried value.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc/>
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
