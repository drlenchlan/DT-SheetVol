using System.Collections;
using System.ComponentModel;

namespace SheetVol.Core.Presentation;

/// <summary>
/// An editable text field with an inline error (E903). Setting <see cref="Text"/> commits
/// it through the owner: on success the owner normally rewrites the text with the
/// formatted value; on failure the typed text stays, <see cref="Error"/> says why, and
/// the owner keeps validating with the last valid value. No modal box, ever.
/// Implements <see cref="INotifyDataErrorInfo"/> so WPF shows the error with its
/// standard validation template.
/// </summary>
public sealed class TextFieldViewModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly Func<string, string?> _commit;
    private readonly Action? _errorChanged;
    private string _text = string.Empty;
    private string _lastValid = string.Empty;
    private string? _error;
    private bool _isEditable = true;

    /// <summary>Creates the field.</summary>
    /// <param name="label">Accessible name and visible label.</param>
    /// <param name="commit">Applies typed text; returns an error message, or null when accepted.</param>
    /// <param name="errorChanged">Called after <see cref="Error"/> changes, so the owner can re-query its commands.</param>
    public TextFieldViewModel(string label, Func<string, string?> commit, Action? errorChanged = null)
    {
        Label = label;
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _errorChanged = errorChanged;
    }

    /// <summary>Accessible name and visible label (E910).</summary>
    public string Label { get; }

    /// <summary>The text shown. Setting it from the view commits it.</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _text, value)) return;
            var error = _commit(value);
            // The commit may have rewritten the text with the formatted value; only a
            // text that is still the typed one becomes the last valid text here.
            if (error is null && _text == value) _lastValid = value;
            SetError(error);
        }
    }

    /// <summary>The inline error, or null.</summary>
    public string? Error => _error;

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => _error is not null;

    /// <summary>False when the field is read-only, for example stations from a view frame.</summary>
    public bool IsEditable
    {
        get => _isEditable;
        internal set => SetProperty(ref _isEditable, value);
    }

    /// <summary>Esc in the view (E910): restores the last valid text and clears the error. Nothing is committed.</summary>
    public void Revert()
    {
        if (_text != _lastValid)
        {
            _text = _lastValid;
            OnPropertyChanged(nameof(Text));
        }
        SetError(null);
    }

    /// <summary>Sets the text from the model without committing it, and clears the error.</summary>
    internal void SetFromModel(string text)
    {
        _lastValid = text;
        if (_text != text)
        {
            _text = text;
            OnPropertyChanged(nameof(Text));
        }
        SetError(null);
    }

    /// <inheritdoc/>
    public bool HasErrors => HasError;

    /// <inheritdoc/>
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <inheritdoc/>
    public IEnumerable GetErrors(string? propertyName)
        => _error is not null && (string.IsNullOrEmpty(propertyName) || propertyName == nameof(Text))
            ? new[] { _error }
            : Array.Empty<string>();

    private void SetError(string? error)
    {
        if (_error == error) return;
        _error = error;
        OnPropertiesChanged(nameof(Error), nameof(HasError), nameof(HasErrors));
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Text)));
        _errorChanged?.Invoke();
    }
}
