using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SheetVol.Core.Presentation;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for the dialog view models
/// (HANDOFF.md 6.14). No MVVM package and no WPF types, so every view model is
/// unit-tested with <c>dotnet test</c>.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for one property.</summary>
    /// <param name="propertyName">Property name; the caller's name when omitted.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises <see cref="PropertyChanged"/> for each named property.</summary>
    /// <param name="propertyNames">Property names.</param>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }

    /// <summary>
    /// Stores <paramref name="value"/> in <paramref name="field"/> and raises
    /// <see cref="PropertyChanged"/> when it differs from the current value.
    /// </summary>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
