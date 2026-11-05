using System.Windows;

namespace SheetVol.UI.Dialog.Themes;

/// <summary>Dark colour keys (E908). Loaded by type, not by pack URI, so it resolves the same way in AutoCAD and in a plain WPF host.</summary>
public partial class DarkTheme : ResourceDictionary
{
    /// <summary>Loads the dictionary.</summary>
    public DarkTheme() => InitializeComponent();
}

/// <summary>Light colour keys (E908); the same keys as <see cref="DarkTheme"/>.</summary>
public partial class LightTheme : ResourceDictionary
{
    /// <summary>Loads the dictionary.</summary>
    public LightTheme() => InitializeComponent();
}

/// <summary>Control styles and templates. Every colour is a DynamicResource to a theme key, so a theme swap restyles live.</summary>
public partial class SheetVolStyles : ResourceDictionary
{
    /// <summary>Loads the dictionary.</summary>
    public SheetVolStyles() => InitializeComponent();
}
