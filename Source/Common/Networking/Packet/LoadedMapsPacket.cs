namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_LoadedMaps)]
public record struct ClientLoadedMapsPacket : IPacket
{
    public int currentMapId;
    public int[] loadedMapIds;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref currentMapId);
        buf.Bind(ref loadedMapIds, BinderOf.Int(), maxLength: 128);
    }
}
