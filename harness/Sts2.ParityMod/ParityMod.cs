using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Godot;

namespace Sts2.ParityMod;

/// <summary>
/// Entry point for the parity recording mod. ModManager calls <see cref="Init"/> once it has
/// loaded this assembly, on the Godot main thread, while NGame is still starting up.
/// </summary>
[ModInitializer(nameof(Init))]
public static class ParityMod
{
    public static void Init()
    {
        try
        {
            // Make the game skip animation delays and interactive waits. This is the same hook
            // AutoSlay uses; it is not TestMode, so model/combat behavior is untouched.
            NonInteractiveMode.AutoSlayerCheck = () => true;

            var tree = (SceneTree)Engine.GetMainLoop();
            Log.Info("[parity] initializer; sync context=" + (SynchronizationContext.Current?.GetType().FullName ?? "<null>"));
            var driver = new ParityDriver(tree);
            _ = driver.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error("[parity] initializer failed: " + ex);
        }
    }
}
