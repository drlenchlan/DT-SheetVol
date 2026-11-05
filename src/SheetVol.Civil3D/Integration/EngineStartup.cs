// Wave 2 integration. Installs the real A1-A3 bindings when the engine assembly loads, in
// full Civil 3D and in Core Console alike: no AcMgd, no UI, no document access here (the
// factories open the drawing only when a command calls them). SVSELFTEST BindFakes can
// still swap in the fakes for a test session and BindInstalled puts these back.

using Autodesk.AutoCAD.Runtime;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Civil3D.Session;

[assembly: ExtensionApplication(typeof(SheetVol.Civil3D.Integration.EngineStartup))]

namespace SheetVol.Civil3D.Integration;

/// <summary>Sets <see cref="EngineBindings.Current"/> and <see cref="SelfTestWorlds.Installed"/> at load.</summary>
public sealed class EngineStartup : IExtensionApplication
{
    /// <summary>The bindings the product runs on: A1's adapters, A2's and A3's writers, A2's store.</summary>
    public static EngineBindings Installed { get; } = new(new CivilAdapterFactory(), new CivilWriterFactory());

    /// <inheritdoc />
    public void Initialize()
    {
        EngineBindings.Current = Installed;
        SelfTestWorlds.Installed = new CivilSelfTestWorld();
    }

    /// <inheritdoc />
    public void Terminate()
    {
    }
}
