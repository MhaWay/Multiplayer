using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class ViewedMapSyncTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        ServerLog.error = (msg) => TestContext.Error.WriteLine(msg);
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private (ServerPlayer player, RecordingConnection conn) AddPlayer(string username, int currentMapId,
        bool hasReportedCurrentMap = true)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn)
        {
            currentMapId = currentMapId,
            hasReportedCurrentMap = hasReportedCurrentMap,
        };
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(player);
        return (player, conn);
    }

    private ServerPlayingState PlayingState(ServerPlayer player) =>
        player.conn.GetState<ServerPlayingState>()!;

    [Test]
    public void ViewedMapReport_SetsMapAbsolutely_AndIsIdempotent()
    {
        var (player, conn) = AddPlayer("player", -1, hasReportedCurrentMap: false);

        PlayingState(player).HandleViewedMapReport(new ClientViewedMapReportPacket { mapId = 7 });
        Assert.That(player.currentMapId, Is.EqualTo(7));
        Assert.That(player.hasReportedCurrentMap, Is.True);

        PlayingState(player).HandleViewedMapReport(new ClientViewedMapReportPacket { mapId = 7 });
        Assert.That(player.currentMapId, Is.EqualTo(7));
    }

    [Test]
    public void ViewedMapReport_DoesNotGenerateAnyPacket()
    {
        server.worldData.mapData[3] = [1, 2, 3];
        var (player, conn) = AddPlayer("player", 3);

        PlayingState(player).HandleViewedMapReport(new ClientViewedMapReportPacket { mapId = 4 });

        Assert.That(conn.SentPackets, Is.Empty);
    }

    [Test]
    public void RequestPlayerCounts_AggregatesMapsAndWorld_IgnoresUnreported()
    {
        var (p1a, _) = AddPlayer("a", 1);
        var (p1b, _) = AddPlayer("b", 1);
        var (pw, _) = AddPlayer("w", VTRSyncConstants.WorldMapId);
        var (pn, _) = AddPlayer("n", -1, hasReportedCurrentMap: false);

        var reqConn = new RecordingConnection("req");
        var reqPlayer = new ServerPlayer(999, reqConn)
        {
            currentMapId = 1,
            hasReportedCurrentMap = true,
        };
        reqConn.serverPlayer = reqPlayer;
        reqConn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(reqPlayer);

        PlayingState(reqPlayer).HandleRequestPlayerCounts(new ClientRequestPlayerCountsPacket());

        Assert.That(reqConn.SentPackets, Does.Contain(Packets.Server_PlayerCounts));
        // Requester is on map 1 too, so the snapshot counts them alongside players a and b.
        var counts = DecodeLastPlayerCounts(reqConn);
        Assert.That(counts.ToCountMap(), Is.EqualTo(
            new Dictionary<int, int> { [1] = 3, [VTRSyncConstants.WorldMapId] = 1 }));
    }

    [Test]
    public void RequestPlayerCounts_DoesNotBroadcastToOtherPlayers()
    {
        var (other, otherConn) = AddPlayer("other", 1);
        var (reqPlayer, _) = AddPlayer("req", 1);

        PlayingState(reqPlayer).HandleRequestPlayerCounts(new ClientRequestPlayerCountsPacket());

        Assert.That(otherConn.SentPackets, Does.Not.Contain(Packets.Server_PlayerCounts));
    }

    [Test]
    public void PlayerCountsSnapshot_ZeroesMapsAbsentFromPacket()
    {
        var applied = new Dictionary<int, int> { [5] = 1, [6] = 1, [VTRSyncConstants.WorldMapId] = 1 };
        var targets = new List<(int mapId, Action<int> setCount)>
        {
            (5, value => applied[5] = value),
            (6, value => applied[6] = value),
            (VTRSyncConstants.WorldMapId, value => applied[VTRSyncConstants.WorldMapId] = value),
        };

        new ServerPlayerCountsPacket { mapIds = [5], counts = [2] }.Apply(targets);

        Assert.That(applied, Is.EqualTo(
            new Dictionary<int, int> { [5] = 2, [6] = 0, [VTRSyncConstants.WorldMapId] = 0 }));
    }

    private static ServerPlayerCountsPacket DecodeLastPlayerCounts(RecordingConnection conn)
    {
        var frame = conn.SentPacketData.Last(d => (d[0] & 0x3F) == (byte)Packets.Server_PlayerCounts);
        var packet = default(ServerPlayerCountsPacket);
        packet.Bind(new PacketReader(new ByteReader(frame[1..])));
        return packet;
    }
}

internal static class VTRSyncConstants
{
    public const int WorldMapId = -2;
}
