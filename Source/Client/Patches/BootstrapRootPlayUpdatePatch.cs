using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Root_Play.Update runs through the whole transition from MapInitializing to Playing.
    /// Arms the map-init trigger as soon as a map exists and ProgramState is Playing.
    /// </summary>
    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
    static class BootstrapRootPlayUpdatePatch
    {
        private static int nextCheckFrame;
        private const int CheckEveryFrames = 10;

        static void Postfix()
        {
            var win = BootstrapConfiguratorWindow.Instance;
            if (win == null)
                return;

            if (UnityEngine.Time.frameCount < nextCheckFrame)
                return;
            nextCheckFrame = UnityEngine.Time.frameCount + CheckEveryFrames;

            win.TryArmAwaitingBootstrapMapInit_FromRootPlayUpdate();
        }
    }
}
