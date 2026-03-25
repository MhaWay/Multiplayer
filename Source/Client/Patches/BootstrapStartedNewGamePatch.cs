using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// When the vanilla flow finishes generating the new game, this fires once the map and pawns are ready.
    /// Backup signal to kick the bootstrap save pipeline in case FinalizeInit was missed or delayed.
    /// </summary>
    [HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.StartedNewGame))]
    public static class BootstrapStartedNewGamePatch
    {
        static void Postfix()
        {
            var window = BootstrapConfiguratorWindow.Instance;
            if (window == null)
                return;

            BootstrapConfiguratorWindow.AwaitingBootstrapMapInit = true;

            OnMainThread.Enqueue(() =>
            {
                window.OnBootstrapMapInitialized();
            });
        }
    }
}
