using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

/// <summary>
/// End-to-end workflow simulation tests for the streaming join point system.
/// Tests full scenarios: three-player clusters, partial uploaders, abort mid-flight,
/// disconnect during job, legacy fallback, overlapping job attempts, etc.
/// </summary>
[TestFixture]
public class StreamingWorkflowSimulationTest
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

    private ServerPlayer AddPlayer(string name, params int[] loadedMapIds)
    {
        var conn = new RecordingConnection(name);
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        foreach (var m in loadedMapIds)
            player.loadedMaps.Add(m);
        if (loadedMapIds.Length > 0)
        {
            player.currentMapId = loadedMapIds[0];
            player.hasReportedCurrentMap = true;
        }
        server.playerManager.Players.Add(player);
        return player;
    }

    // ===== SCENARIO 1: Three-player transitive cluster =====
    // A has {1,2}, B has {2,3}, C has {3,4}
    // Requesting A → transitive closure includes A, B, C and maps {1,2,3,4}

    [Test]
    public void Scenario_ThreePlayerTransitiveCluster_AllIncluded()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var c = AddPlayer("Charlie", 3, 4);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        Assert.That(job.clusterPlayerIds, Is.EquivalentTo(new[] { a.id, b.id, c.id }));
        Assert.That(job.clusterMapIds, Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void Scenario_ThreePlayerTransitiveCluster_UploaderAssignment()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var c = AddPlayer("Charlie", 3, 4);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // A is requester → uploads world
        Assert.That(job.worldUploaderPlayerId, Is.EqualTo(a.id));

        // A has maps 1,2 → uploads both (requester preference)
        Assert.That(job.mapUploaderByMapId[1], Is.EqualTo(a.id));
        Assert.That(job.mapUploaderByMapId[2], Is.EqualTo(a.id));

        // Map 3: A doesn't have it → lowest id among {B, C} that have it → B (id=2) < C (id=3)
        Assert.That(job.mapUploaderByMapId[3], Is.EqualTo(b.id));

        // Map 4: only C has it
        Assert.That(job.mapUploaderByMapId[4], Is.EqualTo(c.id));
    }

    [Test]
    public void Scenario_ThreePlayerTransitiveCluster_AssignmentsSentCorrectly()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var c = AddPlayer("Charlie", 3, 4);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);

        // All three should receive assignments
        Assert.That(((RecordingConnection)a.conn).SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
        Assert.That(((RecordingConnection)b.conn).SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
        Assert.That(((RecordingConnection)c.conn).SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
    }

    [Test]
    public void Scenario_ThreePlayerTransitiveCluster_FullUploadFinalize()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var c = AddPlayer("Charlie", 3, 4);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // A uploads world + maps 1, 2
        Assert.That(server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId), Is.True);
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 0x10 }, new byte[0], job.jobId), Is.True);
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 2, 10, 0, new byte[] { 0x20 }, new byte[0], job.jobId), Is.True);

        // B uploads map 3
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(b, 3, 10, 0, new byte[] { 0x30 }, new byte[0], job.jobId), Is.True);

        // Not yet complete — map 4 missing
        Assert.That(job.IsComplete, Is.False);

        // C uploads map 4
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(c, 4, 10, 0, new byte[] { 0x40 }, new byte[0], job.jobId), Is.True);

        // Now complete
        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));

        // Verify world data was populated correctly
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 1 }));
        Assert.That(server.worldData.sessionData, Is.EqualTo(new byte[] { 2 }));
        Assert.That(server.worldData.mapData.ContainsKey(1), Is.True);
        Assert.That(server.worldData.mapData.ContainsKey(2), Is.True);
        Assert.That(server.worldData.mapData.ContainsKey(3), Is.True);
        Assert.That(server.worldData.mapData.ContainsKey(4), Is.True);
        Assert.That(server.worldData.mapData[4], Is.EqualTo(new byte[] { 0x40 }));
    }

    // ===== SCENARIO 2: Isolated cluster (some players outside) =====

    [Test]
    public void Scenario_IsolatedCluster_OutsidePlayerUntouched()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2);
        var outsider = AddPlayer("Dave", 5, 6); // Completely disconnected maps

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        Assert.That(job.clusterPlayerIds, Does.Contain(a.id));
        Assert.That(job.clusterPlayerIds, Does.Contain(b.id));
        Assert.That(job.clusterPlayerIds, Does.Not.Contain(outsider.id));
        Assert.That(job.clusterMapIds, Does.Not.Contain(5));
        Assert.That(job.clusterMapIds, Does.Not.Contain(6));

        // Outsider doesn't receive assignment
        Assert.That(((RecordingConnection)outsider.conn).SentPackets, Does.Not.Contain(Packets.Server_StreamingJoinPointRequest));
    }

    // ===== SCENARIO 3: Wrong player uploads =====

    [Test]
    public void Scenario_WrongPlayerUpload_Rejected()
    {
        var a = AddPlayer("Alice", 1);
        var b = AddPlayer("Bob", 2);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // B tries to upload world (A is the world uploader)
        bool accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(b, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId);
        Assert.That(accepted, Is.False);
        Assert.That(job.receivedWorldUpload, Is.False);
    }

    [Test]
    public void Scenario_WrongPlayerUploadsMap_Rejected()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // B tries to upload map 1 (A is the uploader for map 1)
        bool accepted = server.worldData.TryAcceptStandaloneMapSnapshot(b, 1, 10, 0, new byte[] { 1 }, new byte[0], job.jobId);
        Assert.That(accepted, Is.False);
    }

    // ===== SCENARIO 4: Duplicate uploads =====

    [Test]
    public void Scenario_DuplicateWorldUpload_Rejected()
    {
        var a = AddPlayer("Alice", 1);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // First upload succeeds
        Assert.That(server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId), Is.True);
        // Second upload rejected
        Assert.That(server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId), Is.False);
    }

    [Test]
    public void Scenario_DuplicateMapUpload_Rejected()
    {
        var a = AddPlayer("Alice", 1, 2);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 1 }, new byte[0], job.jobId), Is.True);
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 1 }, new byte[0], job.jobId), Is.False);
    }

    // ===== SCENARIO 5: Wrong jobId =====

    [Test]
    public void Scenario_WrongJobId_Rejected()
    {
        var a = AddPlayer("Alice", 1);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        int wrongJobId = job.jobId + 999;
        bool accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], wrongJobId);
        Assert.That(accepted, Is.False);
    }

    // ===== SCENARIO 6: Disconnect mid-flight =====

    [Test]
    public void Scenario_UploaderDisconnects_JobAborted()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // A uploads some data
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 1 }, new byte[0], job.jobId);

        // B disconnects before uploading map 3
        server.playerManager.SetDisconnected(b.conn, MpDisconnectReason.ClientLeft);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Aborted));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void Scenario_NonClusterPlayerDisconnects_JobContinues()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var outsider = AddPlayer("Dave", 5);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // Outsider disconnects — should NOT abort the job
        server.playerManager.SetDisconnected(outsider.conn, MpDisconnectReason.ClientLeft);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
    }

    // ===== SCENARIO 7: Upload after abort =====

    [Test]
    public void Scenario_UploadAfterAbort_Rejected()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;
        int jobId = job.jobId;

        server.worldData.AbortStreamingJob("test abort");

        // Late upload from B — rejected because job was aborted and cleared
        bool accepted = server.worldData.TryAcceptStandaloneMapSnapshot(b, 3, 10, 0, new byte[] { 1 }, new byte[0], jobId);
        Assert.That(accepted, Is.False);
    }

    // ===== SCENARIO 8: Second job after first completes =====

    [Test]
    public void Scenario_SequentialJobs_BothComplete()
    {
        var a = AddPlayer("Alice", 1);

        // First job
        server.worldData.TryStartJoinPointCreation(force: true, sourcePlayer: a);
        var job1 = server.worldData.activeStreamingJoinPointJob!;
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job1.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 0x10 }, new byte[0], job1.jobId);
        Assert.That(job1.state, Is.EqualTo(JoinPointJobState.Completed));

        // Second job (force to skip cooldown)
        server.worldData.TryStartJoinPointCreation(force: true, sourcePlayer: a);
        var job2 = server.worldData.activeStreamingJoinPointJob!;
        Assert.That(job2.jobId, Is.GreaterThan(job1.jobId));

        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 20, 0, new byte[] { 3 }, new byte[] { 4 }, new byte[0], job2.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 20, 0, new byte[] { 0x20 }, new byte[0], job2.jobId);
        Assert.That(job2.state, Is.EqualTo(JoinPointJobState.Completed));

        // Verify final data is from job2
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 3 }));
    }

    // ===== SCENARIO 9: Second job while first is active =====

    [Test]
    public void Scenario_ConcurrentJobAttempt_Rejected()
    {
        var a = AddPlayer("Alice", 1);
        var b = AddPlayer("Bob", 2);

        // First job
        server.worldData.TryStartJoinPointCreation(force: true, sourcePlayer: a);
        var job1 = server.worldData.activeStreamingJoinPointJob!;
        Assert.That(job1.state, Is.EqualTo(JoinPointJobState.CollectingUploads));

        // Second job attempt while first is active — CreatingJoinPoint is still true,
        // so TryStartJoinPointCreation returns false entirely
        bool result = server.worldData.TryStartJoinPointCreation(force: true, sourcePlayer: b);
        Assert.That(result, Is.False);

        // The active job is still job1
        Assert.That(server.worldData.activeStreamingJoinPointJob!.jobId, Is.EqualTo(job1.jobId));
    }

    // ===== SCENARIO 10: Legacy flow when gate is off =====

    [Test]
    public void Scenario_LegacyFallback_NoStreamingWhenGateOff()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            multifaction = false,     // Gate OFF
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
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null,
            "No streaming job should be created when gate is off");
        Assert.That(((RecordingConnection)conn).SentPackets, Does.Not.Contain(Packets.Server_StreamingJoinPointRequest),
            "No streaming assignment should be sent when gate is off");
    }

    [Test]
    public void Scenario_LegacyFallback_NonStandalone_NoStreamingJob()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            multifaction = true,
            asyncTime = true,
        })
        {
            IsStandaloneServer = false,  // Not standalone
        };

        var conn = new RecordingConnection("host");
        var player = new ServerPlayer(1, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        player.loadedMaps.Add(1);
        server.playerManager.Players.Add(player);

        bool result = server.worldData.TryStartJoinPointCreation(sourcePlayer: player);
        Assert.That(result, Is.True);
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    // ===== SCENARIO 11: Upload with jobId=0 bypasses validation =====

    [Test]
    public void Scenario_LegacyUpload_JobIdZero_AcceptedWithoutJob()
    {
        var a = AddPlayer("Alice", 1);

        // No streaming job active
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);

        // Legacy upload with jobId=0 should still be accepted
        bool worldAccepted = server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], 0);
        bool mapAccepted = server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 0x10 }, new byte[0], 0);

        Assert.That(worldAccepted, Is.True);
        Assert.That(mapAccepted, Is.True);
    }

    // ===== SCENARIO 12: Command filtering uses loadedMaps =====

    [Test]
    public void Scenario_CommandFiltering_UsesLoadedMaps()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 3);

        // Service the HandleLoadedMaps to set up state properly
        var handlerA = new ServerPlayingState(a.conn);
        handlerA.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 1,
            loadedMapIds = [1, 2],
        });

        var handlerB = new ServerPlayingState(b.conn);
        handlerB.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 3,
            loadedMapIds = [3],
        });

        // Send a map-scoped command for map 2 — should reach A (has map 2) but not B (doesn't).
        // Use MapTimeSpeed which doesn't require InitData (Sync commands check InitData.DebugOnlySyncCmds).
        var connA = (RecordingConnection)a.conn;
        var connB = (RecordingConnection)b.conn;

        int sentBeforeA = connA.SentPackets.Count;
        int sentBeforeB = connB.SentPackets.Count;

        server.commands.Send(CommandType.MapTimeSpeed, ScheduledCommand.NoFaction, 2, new byte[] { 0 });

        int sentAfterA = connA.SentPackets.Count;
        int sentAfterB = connB.SentPackets.Count;

        Assert.That(sentAfterA, Is.GreaterThan(sentBeforeA), "A should receive map 2 command");
        Assert.That(sentAfterB, Is.EqualTo(sentBeforeB), "B should NOT receive map 2 command");
    }

    // ===== SCENARIO 13: HandleLoadedMaps coherence check =====

    [Test]
    public void Scenario_HandleLoadedMaps_CurrentNotInLoaded_LogsWarning()
    {
        var a = AddPlayer("Alice");
        var handler = new ServerPlayingState(a.conn);

        // currentMapId=5 but loadedMapIds doesn't include 5
        handler.HandleLoadedMaps(new ClientLoadedMapsPacket
        {
            currentMapId = 5,
            loadedMapIds = [1, 2],
        });

        // State is still set (we log but don't reject)
        Assert.That(a.currentMapId, Is.EqualTo(5));
        Assert.That(a.loadedMaps, Is.EquivalentTo(new[] { 1, 2 }));
    }

    // ===== SCENARIO 14: Single player, single map — simplest case =====

    [Test]
    public void Scenario_SinglePlayer_SingleMap_CompleteFlow()
    {
        var a = AddPlayer("Alice", 1);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        Assert.That(job.clusterPlayerIds, Is.EquivalentTo(new[] { a.id }));
        Assert.That(job.clusterMapIds, Is.EquivalentTo(new[] { 1 }));
        Assert.That(job.worldUploaderPlayerId, Is.EqualTo(a.id));
        Assert.That(job.mapUploaderByMapId[1], Is.EqualTo(a.id));

        // Upload everything
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 0xAA }, new byte[] { 0xBB }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 0xCC }, new byte[0], job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 0xAA }));
        Assert.That(server.worldData.sessionData, Is.EqualTo(new byte[] { 0xBB }));
        Assert.That(server.worldData.mapData[1], Is.EqualTo(new byte[] { 0xCC }));
    }

    // ===== SCENARIO 15: Timeout aborts job =====

    [Test]
    public void Scenario_Timeout_AbortsJob()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // Simulate passing time beyond timeout
        job.createdAtUtc = DateTime.UtcNow.AddSeconds(-(StandaloneJoinPointJob.TimeoutSeconds + 1));

        server.worldData.CheckStreamingJobTimeout();

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Aborted));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    // ===== SCENARIO 16: Interleaved uploads from multiple players =====

    [Test]
    public void Scenario_InterleavedUploads_CorrectFinalization()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);
        var c = AddPlayer("Charlie", 3, 4);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // Interleaved upload order:
        // C uploads map 4 first
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(c, 4, 10, 0, new byte[] { 4 }, new byte[0], job.jobId), Is.True);
        Assert.That(job.IsComplete, Is.False);

        // A uploads map 1
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 1 }, new byte[0], job.jobId), Is.True);

        // B uploads map 3
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(b, 3, 10, 0, new byte[] { 3 }, new byte[0], job.jobId), Is.True);

        // A uploads world
        Assert.That(server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 0xAA }, new byte[] { 0xBB }, new byte[0], job.jobId), Is.True);

        // A uploads map 2 — job should complete
        Assert.That(server.worldData.TryAcceptStandaloneMapSnapshot(a, 2, 10, 0, new byte[] { 2 }, new byte[0], job.jobId), Is.True);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
        Assert.That(server.worldData.mapData.Count, Is.GreaterThanOrEqualTo(4));
    }

    // ===== SCENARIO 17: EndJoinPointCreation called by TryFinalizeStreamingJob =====

    [Test]
    public void Scenario_StreamingFinalize_CallsEndJoinPointCreation()
    {
        var a = AddPlayer("Alice", 1);

        // TryStartJoinPointCreation sets CreatingJoinPoint (tmpMapCmds != null)
        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        Assert.That(server.worldData.CreatingJoinPoint, Is.True);

        var job = server.worldData.activeStreamingJoinPointJob!;

        // Complete the upload
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 3 }, new byte[0], job.jobId);

        // TryFinalizeStreamingJob should have called EndJoinPointCreation
        Assert.That(server.worldData.CreatingJoinPoint, Is.False, "EndJoinPointCreation should have been called");
        Assert.That(server.worldData.lastJoinPointAtTick, Is.GreaterThanOrEqualTo(0));
    }

    // ===== SCENARIO 18: Abort streaming job also aborts legacy join point =====

    [Test]
    public void Scenario_AbortStreaming_AlsoAbortsLegacyJoinPoint()
    {
        var a = AddPlayer("Alice", 1, 2);
        var b = AddPlayer("Bob", 2, 3);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        Assert.That(server.worldData.CreatingJoinPoint, Is.True);

        server.worldData.AbortStreamingJob("test");

        Assert.That(server.worldData.CreatingJoinPoint, Is.False, "AbortStreamingJob should abort legacy join point too");
    }

    // ===== SCENARIO 19: Snapshot state tracking =====

    [Test]
    public void Scenario_SnapshotState_UpdatedOnAccept()
    {
        var a = AddPlayer("Alice", 1, 2);

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 42, 7, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 42, 7, new byte[] { 3 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 2, 42, 7, new byte[] { 4 }, new byte[0], job.jobId);

        Assert.That(server.worldData.standaloneWorldSnapshot.tick, Is.EqualTo(42));
        Assert.That(server.worldData.standaloneWorldSnapshot.leaseVersion, Is.EqualTo(7));
        Assert.That(server.worldData.standaloneWorldSnapshot.producerPlayerId, Is.EqualTo(a.id));

        Assert.That(server.worldData.standaloneMapSnapshots[1].tick, Is.EqualTo(42));
        Assert.That(server.worldData.standaloneMapSnapshots[1].producerPlayerId, Is.EqualTo(a.id));
        Assert.That(server.worldData.standaloneMapSnapshots[2].tick, Is.EqualTo(42));
    }

    // ===== SCENARIO 20: mapData preserved from previous state for non-cluster maps =====

    [Test]
    public void Scenario_NonClusterMapData_Preserved()
    {
        var a = AddPlayer("Alice", 1, 2);
        var outsider = AddPlayer("Dave", 5);

        // Pre-populate mapData for map 5
        server.worldData.mapData[5] = new byte[] { 0x55 };

        server.worldData.TryStartJoinPointCreation(sourcePlayer: a);
        var job = server.worldData.activeStreamingJoinPointJob!;

        // Complete the cluster job
        server.worldData.TryAcceptStandaloneWorldSnapshot(a, 10, 0, new byte[] { 1 }, new byte[] { 2 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 1, 10, 0, new byte[] { 0x10 }, new byte[0], job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(a, 2, 10, 0, new byte[] { 0x20 }, new byte[0], job.jobId);

        // Map 5 data should still be there — streaming only updates cluster maps via TryAcceptStandaloneMapSnapshot
        Assert.That(server.worldData.mapData[5], Is.EqualTo(new byte[] { 0x55 }),
            "Non-cluster map data should be preserved");
    }
}
