using Verse;

namespace Multiplayer.Client.Comp
{
    /// <summary>
    /// Runs during bootstrap to detect when the new game has fully entered Playing and a map exists.
    /// Keeps the save trigger logic reliable even when the bootstrap window may not receive regular updates.
    /// </summary>
    public class BootstrapCoordinator : GameComponent
    {
        private int nextCheckTick;
        private const int CheckIntervalTicks = 60; // ~1s

        public BootstrapCoordinator(Game game)
        {
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            var win = BootstrapConfiguratorWindow.Instance;
            if (win == null)
                return;

            // Throttle checks
            if (Find.TickManager != null && Find.TickManager.TicksGame < nextCheckTick)
                return;

            if (Find.TickManager != null)
                nextCheckTick = Find.TickManager.TicksGame + CheckIntervalTicks;

            win.BootstrapCoordinatorTick();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextCheckTick, "mp_bootstrap_nextCheckTick", 0);
        }
    }
}
