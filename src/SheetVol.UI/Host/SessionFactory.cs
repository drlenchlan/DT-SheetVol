using SheetVol.Core;

namespace SheetVol.UI.Host;

/// <summary>
/// Creates the database side of <see cref="ISheetVolSession"/> (A4, <c>Civil3D/Session</c>) for
/// the running Civil 3D session. SVDIALOG wraps what it returns in the editor-side decorator
/// (<see cref="EditorSession"/>), so the factory's session does not implement picks,
/// highlight or zoom and must not marshal itself into the command context: the decorator
/// calls it on the main thread, under a document lock, in command context for
/// <see cref="ISheetVolSession.RegenerateAsync"/> and <see cref="ISheetVolSession.ExportAsync"/>.
/// </summary>
public interface ISessionFactory
{
    /// <summary>Creates a session. Called once per SVDIALOG window.</summary>
    ISheetVolSession Create();
}

/// <summary>
/// Where the engine registers its <see cref="ISessionFactory"/> at load time. SVDIALOG uses the
/// registered factory; with none registered it opens against the design fixture when
/// <see cref="HostConfig.FakeSessionVariable"/> is set, and otherwise refuses with SV-E900.
/// </summary>
public static class SessionFactories
{
    private static ISessionFactory? _registered;

    /// <summary>The registered factory, or null before the engine registers one.</summary>
    public static ISessionFactory? Registered => _registered;

    /// <summary>Registers the engine's factory. A later registration replaces an earlier one.</summary>
    /// <param name="factory">The factory.</param>
    public static void Register(ISessionFactory factory)
        => _registered = factory ?? throw new ArgumentNullException(nameof(factory));
}
