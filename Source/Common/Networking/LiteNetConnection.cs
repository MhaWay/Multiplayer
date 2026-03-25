using LiteNetLib;

namespace Multiplayer.Common
{
    public class LiteNetConnection : ConnectionBase
    {
        public readonly NetPeer peer;

        public LiteNetConnection(NetPeer peer)
        {
            this.peer = peer;
        }

        protected override void SendRaw(byte[] raw, bool reliable)
        {
            peer.Send(raw, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        protected override void OnClose()
        {
            peer.NetManager.TriggerUpdate(); // todo: is this needed?
            peer.NetManager.DisconnectPeer(peer);
        }

        public override string ToString()
        {
            return $"NetConnection ({peer.EndPoint}) ({username})";
        }
    }
}
