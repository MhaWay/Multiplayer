using Multiplayer.Client.Networking;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Client;

public abstract class ClientBaseState(ConnectionBase connection) : MpConnectionState(connection)
{
    protected MultiplayerSession Session => Multiplayer.session;

    [TypedPacketHandler]
    public void HandleDisconnected(ServerDisconnectPacket packet)
    {
        ConnectionStatusListeners.TryNotifyAll_Disconnected(SessionDisconnectInfo.From(packet.reason, new ByteReader(packet.data)));
        Multiplayer.StopMultiplayer();
    }
}
