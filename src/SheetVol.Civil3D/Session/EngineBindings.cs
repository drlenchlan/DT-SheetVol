// A4 work package (HANDOFF.md section 10, Wave 2). Owner: A4.
//
// Which adapter and writer implementations the engine uses. Integration/EngineStartup (an
// IExtensionApplication, Core Console safe) sets Current to the real A1-A3 factories when the
// assembly loads. Before that, or in a build without it, the commands are bound to
// "unwired" factories that refuse with SV-E822: running fakes in a real drawing would write
// fake numbers, which 6.13 forbids. SVSELFTEST builds its own bindings (fakes, or Current
// with the Installed keyword); its test-only BindFakes keyword swaps Current for a session.

using Autodesk.AutoCAD.DatabaseServices;

namespace SheetVol.Civil3D.Session;

/// <summary>Thrown when an engine component is not wired in this build (SV-E822).</summary>
public sealed class NotWiredException(string component)
    : InvalidOperationException($"{component} is not wired in this build of SheetVol: the A1-A3 implementations are integrated by the orchestrator. Nothing was changed.")
{
    /// <summary>The missing component.</summary>
    public string Component { get; } = component;
}

/// <summary>The adapter and writer factories the engine runs on.</summary>
/// <param name="Adapters">A1.</param>
/// <param name="Writers">A2 and A3.</param>
public sealed record EngineBindings(IAdapterFactory Adapters, IWriterFactory Writers)
{
    private static EngineBindings? _current;

    /// <summary>Factories that refuse every call with SV-E822.</summary>
    public static EngineBindings Unwired { get; } = new(new UnwiredAdapterFactory(), new UnwiredWriterFactory());

    /// <summary>The bindings the product commands and the session use. Unwired until the orchestrator sets them.</summary>
    public static EngineBindings Current
    {
        get => _current ?? Unwired;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>True when neither factory is the unwired placeholder.</summary>
    public bool IsWired => Adapters is not UnwiredAdapterFactory && Writers is not UnwiredWriterFactory;

    private sealed class UnwiredAdapterFactory : IAdapterFactory
    {
        public IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings) => throw new NotWiredException("The Civil 3D adapters (A1)");
        public IVolumeSurfaceSources OpenVolumeSurfaces(Database db, Transaction tr, SheetVolSettings settings) => throw new NotWiredException("The Civil 3D adapters (A1)");
    }

    private sealed class UnwiredWriterFactory : IWriterFactory
    {
        public IModelWriter Model(Database db, Transaction tr, SheetVolSettings settings) => throw new NotWiredException("The model writers (A2)");
        public IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings) => throw new NotWiredException("The paper writers (A3)");
        public IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings) => throw new NotWiredException("The persistence store (A2)");
    }
}
