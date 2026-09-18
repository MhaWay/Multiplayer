namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_ViewedMapReport)]
public record struct ClientViewedMapReportPacket : IPacket
{
    public int mapId;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
    }
}

[PacketDefinition(Packets.Client_RequestPlayerCounts)]
public record struct ClientRequestPlayerCountsPacket : IPacket
{
    public void Bind(PacketBuffer buf)
    {
    }
}

[PacketDefinition(Packets.Server_PlayerCounts)]
public record struct ServerPlayerCountsPacket : IPacket
{
    public int[] mapIds;
    public int[] counts;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapIds, BinderOf.Int());
        buf.Bind(ref counts, BinderOf.Int());
    }
}
