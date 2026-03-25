using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Root_Play.Start is called when the game fully transitions into Playing.
    /// This is a reliable signal for arming the bootstrap map init detection.
    /// </summary>
    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Start))]
    static class BootstrapRootPlayPatch
    {
        static void Postfix()
        {
            var inst = BootstrapConfiguratorWindow.Instance;
            if (inst == null)
                return;

            inst.TryArmAwaitingBootstrapMapInit_FromRootPlay();
        }
    }
}
