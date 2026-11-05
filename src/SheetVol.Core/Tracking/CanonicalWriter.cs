using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SheetVol.Core.Tracking;

/// <summary>
/// Builds the invariant, unambiguous text that fingerprints and content signatures hash
/// with SHA-256 (6.11). Every value carries a type tag and a terminator; strings are
/// length-prefixed so no value can imitate a separator. Doubles use the shortest
/// round-trip form in the invariant culture, with negative zero written as zero, so the
/// same values give the same text on every machine, culture and run. Never uses
/// <see cref="object.GetHashCode"/>.
/// </summary>
public sealed class CanonicalWriter
{
    private readonly StringBuilder _text = new();

    /// <summary>The canonical text written so far.</summary>
    public string Text => _text.ToString();

    /// <summary>Lower-case hex SHA-256 of <see cref="Text"/> encoded as UTF-8.</summary>
    public string Sha256Hex() => Hash(Text);

    /// <summary>Lower-case hex SHA-256 of a canonical text encoded as UTF-8.</summary>
    public static string Hash(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Invariant shortest round-trip text of a double; negative zero is "0".</summary>
    public static string FormatDouble(double value)
        => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Starts a named section.</summary>
    public CanonicalWriter Section(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _text.Append('#');
        AppendString(name);
        return this;
    }

    /// <summary>Writes a string, or a null marker.</summary>
    public CanonicalWriter Add(string? value)
    {
        if (value is null) _text.Append("n;");
        else { _text.Append('s'); AppendString(value); }
        return this;
    }

    /// <summary>Writes a double.</summary>
    public CanonicalWriter Add(double value)
    {
        _text.Append('d').Append(FormatDouble(value)).Append(';');
        return this;
    }

    /// <summary>Writes an integer (also used for list counts).</summary>
    public CanonicalWriter Add(int value)
    {
        _text.Append('i').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
        return this;
    }

    /// <summary>Writes a boolean.</summary>
    public CanonicalWriter Add(bool value)
    {
        _text.Append(value ? "b1;" : "b0;");
        return this;
    }

    /// <summary>Writes a GUID in the lower-case "D" form.</summary>
    public CanonicalWriter Add(Guid value)
    {
        _text.Append('g').Append(value.ToString("D")).Append(';');
        return this;
    }

    /// <summary>Writes a point as two doubles.</summary>
    public CanonicalWriter Add(Pt value) => Add(value.X).Add(value.Y);

    /// <summary>Writes an optional point: a presence flag, then the point when present.</summary>
    public CanonicalWriter Add(Pt? value) => value is Pt p ? Add(true).Add(p) : Add(false);

    /// <summary>Writes a polyline vertex: point, then bulge.</summary>
    public CanonicalWriter Add(PolyVertex value) => Add(value.Point).Add(value.Bulge);

    /// <summary>Writes an enum value by name.</summary>
    public CanonicalWriter AddEnum<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        _text.Append('e');
        AppendString(value.ToString());
        return this;
    }

    private void AppendString(string value)
        => _text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
