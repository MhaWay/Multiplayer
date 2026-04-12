using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class StandaloneMapStreamingTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        })
        {
            IsStandaloneServer = true,
        };
        nextPlayerId = 1;
    }

    private void EnableMapStreaming()
    {
        server.settings.multifaction = true;
        server.settings.asyncTime = true;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private (ServerPlayer player, RecordingConnection conn) AddPlayer(string username, int currentMapId, bool hasReportedCurrentMap = true)
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

    [Test]
    public void StandaloneStreamingDisabled_KeepsBroadcastBehavior()
    {
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connOtherMap) = AddPlayer("map2", 2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connOtherMap.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void NonStandaloneServer_KeepsBroadcastBehavior()
    {
        server.IsStandaloneServer = false;
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connOtherMap) = AddPlayer("map2", 2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connOtherMap.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void StandaloneStreamingDisabled_BroadcastsEvenWithWorldViewer()
    {
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connWorldMap) = AddPlayer("world", -2);
        var (_, connOtherMap) = AddPlayer("map2", 2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connWorldMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connOtherMap.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void InitialPlayerCountMapReport_DoesNotSendMapResponse()
    {
        server.worldData.mapData[5] = [9, 9, 9];
        server.worldData.mapCmds[5] = [ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 10, 0, 5, 1, []))];
        var (player, conn) = AddPlayer("player", -1, hasReportedCurrentMap: false);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new Multiplayer.Common.Networking.Packet.ClientCommandPacket(
            CommandType.PlayerCount,
            ScheduledCommand.Global,
            ByteWriter.GetBytes(-1, 5)
        ));

        Assert.That(player.currentMapId, Is.EqualTo(5));
        Assert.That(player.hasReportedCurrentMap, Is.True);
        Assert.That(conn.SentPackets, Does.Not.Contain(Packets.Server_MapResponse));
    }

    [Test]
    public void WorldToMapTransition_DoesNotSendMapResponse()
    {
        server.worldData.mapData[5] = [9, 9, 9];
        server.worldData.mapCmds[5] = [ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 10, 0, 5, 1, []))];
        var (player, conn) = AddPlayer("player", -2);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new Multiplayer.Common.Networking.Packet.ClientCommandPacket(
            CommandType.PlayerCount,
            ScheduledCommand.Global,
            ByteWriter.GetBytes(-2, 5)
        ));

        Assert.That(player.currentMapId, Is.EqualTo(5));
        Assert.That(conn.SentPackets, Does.Not.Contain(Packets.Server_MapResponse));
    }

    [Test]
    public void MapToMapTransition_DoesNotSendMapResponseWhenStreamingDisabled()
    {
        server.worldData.mapData[5] = [9, 9, 9];
        server.worldData.mapCmds[5] = [ScheduledCommand.Serialize(new ScheduledCommand(CommandType.Designator, 10, 0, 5, 1, []))];
        var (player, conn) = AddPlayer("player", 3);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleClientCommand(new Multiplayer.Common.Networking.Packet.ClientCommandPacket(
            CommandType.PlayerCount,
            ScheduledCommand.Global,
            ByteWriter.GetBytes(3, 5)
        ));

        Assert.That(player.currentMapId, Is.EqualTo(5));
        Assert.That(conn.SentPackets, Does.Not.Contain(Packets.Server_MapResponse));
    }

    // --- Gate verification tests ---

    [Test]
    public void Gate_TrueWhenAllConditionsMet()
    {
        EnableMapStreaming();
        Assert.That(server.CanUseStandaloneMapStreaming(1), Is.True);
    }

    [Test]
    public void Gate_FalseWhenNotStandalone()
    {
        server.IsStandaloneServer = false;
        server.settings.multifaction = true;
        server.settings.asyncTime = true;
        Assert.That(server.CanUseStandaloneMapStreaming(1), Is.False);
    }

    [Test]
    public void Gate_FalseWhenNotMultifaction()
    {
        server.settings.multifaction = false;
        server.settings.asyncTime = true;
        Assert.That(server.CanUseStandaloneMapStreaming(1), Is.False);
    }

    [Test]
    public void Gate_FalseWhenNotAsyncTime()
    {
        server.settings.multifaction = true;
        server.settings.asyncTime = false;
        Assert.That(server.CanUseStandaloneMapStreaming(1), Is.False);
    }

    // --- Command filtering when streaming enabled ---

    [Test]
    public void StreamingEnabled_CommandFilteredByCurrentMap()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connOtherMap) = AddPlayer("map2", 2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connOtherMap.SentPackets, Does.Not.Contain(Packets.Server_Command));
    }

    [Test]
    public void StreamingEnabled_UnreportedPlayerStillReceivesCommand()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connUnreported) = AddPlayer("unreported", -1, hasReportedCurrentMap: false);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connUnreported.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void StreamingEnabled_NegativeMapIdPlayerReceivesCommand()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        var (_, connNegative) = AddPlayer("negative", -2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connNegative.SentPackets, Does.Contain(Packets.Server_Command));
    }

    // --- loadedMaps tests ---

    [Test]
    public void LoadedMaps_InitializedEmpty()
    {
        var (player, _) = AddPlayer("test", 1);
        Assert.That(player.loadedMaps, Is.Empty);
    }

    [Test]
    public void LoadedMaps_ClearedOnRejoin()
    {
        var (player, _) = AddPlayer("test", 1);
        player.loadedMaps.Add(1);
        player.loadedMaps.Add(2);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleRejoin(new ByteReader([]));

        Assert.That(player.loadedMaps, Is.Empty);
        Assert.That(player.hasReportedCurrentMap, Is.False);
    }

    // --- ClientLoadedMapsPacket handler tests ---

    [Test]
    public void HandleLoadedMaps_UpdatesPlayerState()
    {
        EnableMapStreaming();
        var (player, _) = AddPlayer("test", -1, hasReportedCurrentMap: false);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 2,
            loadedMapIds = [1, 2, 3]
        });

        Assert.That(player.currentMapId, Is.EqualTo(2));
        Assert.That(player.hasReportedCurrentMap, Is.True);
        Assert.That(player.loadedMaps, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void HandleLoadedMaps_ReplacesOldSet()
    {
        EnableMapStreaming();
        var (player, _) = AddPlayer("test", 1);
        player.loadedMaps.Add(1);
        player.loadedMaps.Add(5);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 2,
            loadedMapIds = [2, 3]
        });

        Assert.That(player.loadedMaps, Is.EquivalentTo(new[] { 2, 3 }));
        Assert.That(player.loadedMaps, Does.Not.Contain(1));
        Assert.That(player.loadedMaps, Does.Not.Contain(5));
    }

    [Test]
    public void HandleLoadedMaps_IgnoredWhenStreamingDisabled()
    {
        // settings.multifaction and asyncTime default to false
        var (player, _) = AddPlayer("test", -1, hasReportedCurrentMap: false);

        var state = player.conn.GetState<ServerPlayingState>()!;
        state.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 2,
            loadedMapIds = [2, 3]
        });

        Assert.That(player.currentMapId, Is.EqualTo(-1));
        Assert.That(player.hasReportedCurrentMap, Is.False);
        Assert.That(player.loadedMaps, Is.Empty);
    }

    // --- Command filtering with loadedMaps ---

    [Test]
    public void StreamingEnabled_LoadedMapsFilter_ReceivesCommandForLoadedMap()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (player, conn) = AddPlayer("multi", 1);
        player.loadedMaps.Add(1);
        player.loadedMaps.Add(2);

        server.commands.Send(CommandType.Designator, 0, 2, [], sourcePlayer: player);

        Assert.That(conn.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void StreamingEnabled_LoadedMapsFilter_RejectsCommandForUnloadedMap()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap1, connOnMap1) = AddPlayer("map1", 1);
        playerOnMap1.loadedMaps.Add(1);
        playerOnMap1.loadedMaps.Add(2);

        var (playerOnMap3, _) = AddPlayer("map3", 3);
        playerOnMap3.loadedMaps.Add(3);

        server.commands.Send(CommandType.Designator, 0, 3, [], sourcePlayer: playerOnMap3);

        Assert.That(connOnMap1.SentPackets, Does.Not.Contain(Packets.Server_Command));
    }

    [Test]
    public void StreamingEnabled_LoadedMapsFilter_SharedMapReachesBothPlayers()
    {
        EnableMapStreaming();
        server.worldData.mapData[2] = [1, 2, 3];
        var (playerA, connA) = AddPlayer("a", 1);
        playerA.loadedMaps.Add(1);
        playerA.loadedMaps.Add(2);

        var (playerB, connB) = AddPlayer("b", 2);
        playerB.loadedMaps.Add(2);
        playerB.loadedMaps.Add(3);

        server.commands.Send(CommandType.Designator, 0, 2, [], sourcePlayer: playerA);

        Assert.That(connA.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connB.SentPackets, Does.Contain(Packets.Server_Command));
    }

    [Test]
    public void StreamingEnabled_FallbackToCurrentMapId_WhenLoadedMapsEmpty()
    {
        EnableMapStreaming();
        server.worldData.mapData[1] = [1, 2, 3];
        var (playerOnMap, connOnMap) = AddPlayer("map1", 1);
        // loadedMaps is empty — should fall back to currentMapId
        var (_, connOtherMap) = AddPlayer("map2", 2);

        server.commands.Send(CommandType.Designator, 0, 1, [], sourcePlayer: playerOnMap);

        Assert.That(connOnMap.SentPackets, Does.Contain(Packets.Server_Command));
        Assert.That(connOtherMap.SentPackets, Does.Not.Contain(Packets.Server_Command));
    }
}