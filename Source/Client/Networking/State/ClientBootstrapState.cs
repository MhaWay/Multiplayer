using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

/// <summary>
/// Client connection state used while configuring a bootstrap server.
/// The server is in ServerBootstrap and expects upload packets; the client must keep the connection alive
/// and handle bootstrap completion / disconnect packets.
/// </summary>
[PacketHandlerClass]
public class ClientBootstrapState(ConnectionBase connection) : ClientBaseState(connection)
{
    [TypedPacketHandler]
    public void HandleBootstrap(ServerBootstrapPacket packet)
    {
        // The server sends this again when entering ServerBootstrapState.StartState().
        // We already have the bootstrap info from ClientJoiningState; just ignore it.
    }

    [TypedPacketHandler]
    public override void HandleDisconnected(ServerDisconnectPacket packet)
    {
        // If bootstrap completed successfully, show success message before closing the window
        if (packet.reason == MpDisconnectReason.BootstrapCompleted)
        {
            OnMainThread.Enqueue(() => Messages.Message(
                "Bootstrap configuration completed. The server will now shut down; please restart it manually to start normally.",
                MessageTypeDefOf.PositiveEvent, false));
        }

        // Close the bootstrap configurator window now that the process is complete
        OnMainThread.Enqueue(() =>
        {
            var window = Find.WindowStack.WindowOfType<BootstrapConfiguratorWindow>();
            if (window != null)
                Find.WindowStack.TryRemove(window);
        });

        // Let the base class handle the disconnect
        base.HandleDisconnected(packet);
    }
}
