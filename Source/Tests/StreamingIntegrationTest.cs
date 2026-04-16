using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

/// <summary>
/// Integration tests for the streaming join point flow:
/// TryStartJoinPointCreation + streaming job creation,
/// HandleLoadedMaps server handler, and full end-to-end flow.
/// </summary>
[TestFixture]
public class StreamingIntegrationTest
{
    private MultiplayerServer server;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            multifaction = true,
            asyncTime = true,
        })
        {
            IsStandaloneServer = true,
        };
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private ServerPlayer AddPlayer(params int[] loadedMapIds)
    {
        var conn = new RecordingConnection($"player{nextPlayerId}");
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        foreach (var m in loadedMapIds)
            player.loadedMaps.Add(m);
        server.playerManager.Players.Add(player);
        return player;
    }

    // ===== TryStartJoinPointCreation + Streaming Job =====

    [Test]
    public void TryStartJoinPointCreation_WithSourcePlayer_CreatesStreamingJob()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        bool result = server.worldData.TryStartJoinPointCreation(sourcePlayer: a);

        Assert.That(result, Is.True);
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
        Assert.That(server.worldData.activeStreamingJoinPointJob!.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        Assert.That(server.worldData.activeStreamingJoinPointJob.clusterPlayerIds, Does.Contain(a.id));
        Assert.That(server.worldData.activeStreamingJoinPointJob.clusterPlayerIds, Does.Contain(b.id));
    }

    [Test]
    public void TryStartJoinPointCreation_WithoutSourcePlayer_UsesFallbackPlayer()
    {
        var a = AddPlayer(1);

        bool result = server.worldData.TryStartJoinPointCreation(sourcePlayer: null);

        Assert.That(result, Is.True);
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
        Assert.That(server.worldData.activeStreamingJoinPointJob!.worldUploaderPlayerId, Is.EqualTo(a.id));
    }

    [Test]
    public void TryStartJoinPointCreation_NonStreamingServer_NoStreamingJob()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            multifaction = false,
            asyncTime = true,
        })
        {
            IsStandaloneServer = true,
        };

        var conn = new RecordingConnection("player1");
        var player = new ServerPlayer(1, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        player.loadedMaps.Add(1);
        server.playerManager.Players.Add(player);

        bool result = server.worldData.TryStartJoinPointCreation(sourcePlayer: player);

        Assert.That(result, Is.True);
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void TryStartJoinPointCreation_SendsAssignmentsToClusterPlayers()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);
        var c = AddPlayer(4); // Not in cluster

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);

        var connA = (RecordingConnection)a.conn;
        var connB = (RecordingConnection)b.conn;
        var connC = (RecordingConnection)c.conn;

        Assert.That(connA.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest),
            "Player A (in cluster) should receive streaming assignment");
        Assert.That(connB.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest),
            "Player B (in cluster) should receive streaming assignment");
        Assert.That(connC.SentPackets, Does.Not.Contain(Packets.Server_StreamingJoinPointRequest),
            "Player C (not in cluster) should NOT receive streaming assignment");
    }

    [Test]
    public void TryStartJoinPointCreation_JobHasCorrectUploaderAssignments()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // Requesting player A is world uploader
        Assert.That(job.worldUploaderPlayerId, Is.EqualTo(a.id));
        // A uploads maps 1 and 2 (has both, is requester)
        Assert.That(job.mapUploaderByMapId[1], Is.EqualTo(a.id));
        Assert.That(job.mapUploaderByMapId[2], Is.EqualTo(a.id));
        // B uploads map 3 (only B has it)
        Assert.That(job.mapUploaderByMapId[3], Is.EqualTo(b.id));
    }

    [Test]
    public void TryStartJoinPointCreation_Cooldown_SkipsSecondCall()
    {
        var a = AddPlayer(1);

        bool first = server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        Assert.That(first, Is.True);

        // Complete the job
        var job = server.worldData.activeStreamingJoinPointJob!;
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 0, 0, new byte[1], new byte[1], new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 0, 0, new byte[1], new byte[0], job.jobId);
        server.worldData.TryFinalizeStreamingJob();

        // Second call skipped due to cooldown (tick hasn't advanced)
        bool second = server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        Assert.That(second, Is.False);
    }

    [Test]
    public void TryStartJoinPointCreation_Force_IgnoresCooldown()
    {
        var a = AddPlayer(1);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 0, 0, new byte[1], new byte[1], new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 0, 0, new byte[1], new byte[0], job.jobId);
        server.worldData.TryFinalizeStreamingJob();

        bool second = server.worldData.TryStartJoinPointCreation(force: true, sourcePlayer: a);
        Assert.That(second, Is.True);
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
    }

    // ===== HandleLoadedMaps server handler =====

    [Test]
    public void HandleLoadedMaps_UpdatesPlayerState()
    {
        var a = AddPlayer();

        var handler = new ServerPlayingState(a.conn);

        handler.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 1,
            loadedMapIds = [1, 2, 3],
        });

        Assert.That(a.currentMapId, Is.EqualTo(1));
        Assert.That(a.loadedMaps, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void HandleLoadedMaps_NonStreaming_DoesNothing()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            multifaction = false,
            asyncTime = true,
        })
        {
            IsStandaloneServer = true,
        };

        var conn = new RecordingConnection("player1");
        var player = new ServerPlayer(1, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(player);

        var handler = new ServerPlayingState(player.conn);

        handler.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 1,
            loadedMapIds = [1, 2],
        });

        Assert.That(player.loadedMaps, Is.Empty);
    }

    [Test]
    public void TryStartJoinPointCreation_PlayerWithNoLoadedMaps_NoStreamingJob()
    {
        var a = AddPlayer(); // No loaded maps

        bool result = server.worldData.TryStartJoinPointCreation(sourcePlayer: a);

        Assert.That(result, Is.True);
        // Job not created because player has no loaded maps
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    // ===== Full streaming flow: create → assign → upload → finalize =====

    [Test]
    public void FullFlow_TwoPlayers_CreateAssignUploadFinalize()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        // 1. Start join point creation (triggers streaming job)
        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob;
        Assert.That(job, Is.Not.Null);
        Assert.That(job!.state, Is.EqualTo(JoinPointJobState.CollectingUploads));

        // 2. Both players received assignments
        var connA = (RecordingConnection)a.conn;
        var connB = (RecordingConnection)b.conn;
        Assert.That(connA.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
        Assert.That(connB.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));

        // 3. Player A uploads world + map 1 + map 2
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 0, 0, new byte[1], new byte[1], new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 0, 0, new byte[1], new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 2, 0, 0, new byte[1], new byte[0], job.jobId);

        // Not finalized yet — map 3 missing
        server.worldData.TryFinalizeStreamingJob();
        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));

        // 4. Player B uploads map 3
        server.worldData.TryAcceptStandaloneMapSnapshot(b, 3, 0, 0, new byte[1], new byte[0], job.jobId);

        // Now should finalize
        server.worldData.TryFinalizeStreamingJob();
        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }
}
