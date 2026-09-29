using System.Linq;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Common
{
    public class FreezeManager
    {
        private bool frozen;

        public bool Frozen
        {
            get => frozen;
            private set
            {
                frozen = value;
                Server.SendToPlaying(new ServerFreezePacket(frozen, Server.gameTimer));
            }
        }

        public MultiplayerServer Server { get; }

        public FreezeManager(MultiplayerServer server)
        {
            Server = server;
        }

        private const int MaxFreezeWaitTime = GenTicks.TicksPerRealSecond * 10; // 10 seconds

        public void Tick()
        {
            // Note: ServerPlayer.IsHost dereferences MultiplayerServer.instance (static).
            // During a server shutdown (TryStop sets instance = null) the server loop's
            // current iteration can still execute FreezeManager.Tick and blow up with NRE
            // on that accessor before the while(running) guard exits. Resolve "is this the
            // host?" locally against our captured instance so the tick stays crash-safe.
            // Note: ServerPlayer.IsHost dereferences MultiplayerServer.instance (static).
            // During a server shutdown (TryStop sets instance = null) the server loop's
            // current iteration can still execute FreezeManager.Tick and blow up with NRE
            // on that accessor before the while(running) guard exits. Resolve "is this the
            // host?" locally against our captured instance so the tick stays crash-safe.
            var hostUsername = Server.hostUsername;
            ServerPlayer hostPlayer = null;
            foreach (var p in Server.PlayingPlayers)
            {
                if (hostUsername != null && p.Username == hostUsername)
                {
                    hostPlayer = p;
                    break;
                }
            }

            if (hostPlayer != null)
            {
                // Host is present: freeze/unfreeze follows the host's state
                if (!Frozen && hostPlayer.frozen)
                    Frozen = true;

                if (Frozen && !hostPlayer.frozen && (!Server.PlayingPlayers.Any(p => p.frozen) || Server.NetTimer - hostPlayer.unfrozenAt > MaxFreezeWaitTime))
                    Frozen = false;
            }
            else
            {
                // Host is absent: unfreeze if any player is active
                if (Frozen && Server.PlayingPlayers.Any())
                    Frozen = false;
            }
        }
    }
}
