using System;
using System.Collections.Generic;

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

// A complete snapshot: maps not included are considered to have zero players.
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

    public Dictionary<int, int> ToCountMap()
    {
        var countById = new Dictionary<int, int>();
        int len = Math.Min(mapIds?.Length ?? 0, counts?.Length ?? 0);
        for (int i = 0; i < len; i++)
            countById[mapIds[i]] = counts[i];
        return countById;
    }

    public void Apply(IEnumerable<(int mapId, Action<int> setCount)> targets)
    {
        var countById = ToCountMap();
        foreach (var (mapId, setCount) in targets)
            setCount(countById.TryGetValue(mapId, out int count) ? count : 0);
    }
}
