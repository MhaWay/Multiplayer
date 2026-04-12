using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class JoinPointClusterTest
{
    private int nextPlayerId = 1;

    private ServerPlayer MakePlayer(params int[] loadedMapIds)
    {
        var conn = new RecordingConnection($"player{nextPlayerId}");
        var player = new ServerPlayer(nextPlayerId++, conn);
        foreach (var m in loadedMapIds)
            player.loadedMaps.Add(m);
        return player;
    }

    [SetUp]
    public void SetUp()
    {
        nextPlayerId = 1;
    }

    [Test]
    public void SinglePlayer_SingleMap()
    {
        var a = MakePlayer(1);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void SinglePlayer_MultipleMaps()
    {
        var a = MakePlayer(1, 2, 3);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void TwoPlayers_SharedMap()
    {
        var a = MakePlayer(1, 2);
        var b = MakePlayer(2, 3);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a, b]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id, b.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void TransitiveClosure_ThreePlayers()
    {
        // A has {1,2}, B has {2,3}, C has {3,4}
        // Starting from A: A->maps{1,2}, B shares 2->maps{1,2,3}, C shares 3->maps{1,2,3,4}
        var a = MakePlayer(1, 2);
        var b = MakePlayer(2, 3);
        var c = MakePlayer(3, 4);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a, b, c]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id, b.id, c.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void PlayerOutsideCluster_Excluded()
    {
        // A has {1,2}, B has {2,3}, C has {4} — C is isolated
        var a = MakePlayer(1, 2);
        var b = MakePlayer(2, 3);
        var c = MakePlayer(4);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a, b, c]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id, b.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
        Assert.That(playerIds, Does.Not.Contain(c.id));
    }

    [Test]
    public void DisjointClusters_OnlyRequestingCluster()
    {
        // A has {1}, B has {2}, C has {3} — all isolated, only A's cluster
        var a = MakePlayer(1);
        var b = MakePlayer(2);
        var c = MakePlayer(3);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a, b, c]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void EmptyLoadedMaps_RequestingPlayerOnly()
    {
        var a = MakePlayer(); // no maps
        var b = MakePlayer(1);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(a, [a, b]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id }));
        Assert.That(mapIds, Is.Empty);
    }

    [Test]
    public void ActionPlanExample()
    {
        // B has {2,3}, A has {1,2}, C has {4}
        // Starting from B: B->maps{2,3}, A shares 2->maps{1,2,3}
        // C has {4} which is not in cluster
        // Cluster: players={A,B}, maps={1,2,3}
        var a = MakePlayer(1, 2);
        var b = MakePlayer(2, 3);
        var c = MakePlayer(4);
        var (playerIds, mapIds) = WorldData.ComputeJoinPointCluster(b, [a, b, c]);

        Assert.That(playerIds, Is.EquivalentTo(new[] { a.id, b.id }));
        Assert.That(mapIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
        Assert.That(playerIds, Does.Not.Contain(c.id));
    }
}

[TestFixture]
public class JoinPointJobCreationTest
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
            lan = false,
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

    [Test]
    public void CreatesJob_WithCorrectCluster()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        Assert.That(job, Is.Not.Null);
        Assert.That(job!.clusterPlayerIds, Is.EquivalentTo(new[] { a.id, b.id }));
        Assert.That(job.clusterMapIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void WorldUploader_IsRequestingPlayer()
    {
        var a = AddPlayer(1, 2);
        AddPlayer(2, 3);

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        Assert.That(job!.worldUploaderPlayerId, Is.EqualTo(a.id));
    }

    [Test]
    public void MapUploader_PrefersRequestingPlayer()
    {
        var a = AddPlayer(1, 2);
        AddPlayer(2, 3);

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        // a has maps 1 and 2, so a should upload both
        Assert.That(job!.mapUploaderByMapId[1], Is.EqualTo(a.id));
        Assert.That(job.mapUploaderByMapId[2], Is.EqualTo(a.id));
    }

    [Test]
    public void MapUploader_FallsBackToLowestId()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        // a doesn't have map 3, but b does. b is the only option.
        Assert.That(job!.mapUploaderByMapId[3], Is.EqualTo(b.id));
    }

    [Test]
    public void SharedMap_AssignedToRequestingPlayer()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        // Map 2 is shared. Requesting player a has it, so a uploads it.
        Assert.That(job!.mapUploaderByMapId[2], Is.EqualTo(a.id));
    }

    [Test]
    public void RejectsJob_WhenAlreadyActive()
    {
        var a = AddPlayer(1);
        server.worldData.TryCreateStreamingJoinPointJob(a, "first");

        var second = server.worldData.TryCreateStreamingJoinPointJob(a, "second");

        Assert.That(second, Is.Null);
    }

    [Test]
    public void RejectsJob_WhenNoLoadedMaps()
    {
        var a = AddPlayer(); // no maps

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        Assert.That(job, Is.Null);
    }

    [Test]
    public void JobId_Increments()
    {
        var a = AddPlayer(1);
        var job1 = server.worldData.TryCreateStreamingJoinPointJob(a, "first");
        // Complete the first job so we can create another
        job1!.state = JoinPointJobState.Completed;

        var job2 = server.worldData.TryCreateStreamingJoinPointJob(a, "second");

        Assert.That(job2!.jobId, Is.GreaterThan(job1.jobId));
    }

    [Test]
    public void IsComplete_FalseInitially()
    {
        var a = AddPlayer(1, 2);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        Assert.That(job!.IsComplete, Is.False);
    }

    [Test]
    public void IsComplete_TrueWhenAllReceived()
    {
        var a = AddPlayer(1, 2);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        job!.receivedWorldUpload = true;
        job.receivedMapIds.Add(1);
        job.receivedMapIds.Add(2);

        Assert.That(job.IsComplete, Is.True);
    }

    [Test]
    public void IsComplete_FalseWhenMissingMap()
    {
        var a = AddPlayer(1, 2);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        job!.receivedWorldUpload = true;
        job.receivedMapIds.Add(1);
        // map 2 still missing

        Assert.That(job.IsComplete, Is.False);
    }

    [Test]
    public void IsComplete_FalseWhenMissingWorld()
    {
        var a = AddPlayer(1);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        // world not received
        job!.receivedMapIds.Add(1);

        Assert.That(job.IsComplete, Is.False);
    }

    [Test]
    public void SendAssignments_SendsPacketToClusterPlayers()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);
        var c = AddPlayer(4); // outside cluster

        var connA = (RecordingConnection)a.conn;
        var connB = (RecordingConnection)b.conn;
        var connC = (RecordingConnection)c.conn;

        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");
        server.worldData.SendStreamingJoinPointAssignments(job!);

        Assert.That(connA.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
        Assert.That(connB.SentPackets, Does.Contain(Packets.Server_StreamingJoinPointRequest));
        Assert.That(connC.SentPackets, Does.Not.Contain(Packets.Server_StreamingJoinPointRequest));
    }

    [Test]
    public void SendAssignments_TransitionsToCollectingUploads()
    {
        var a = AddPlayer(1);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");

        Assert.That(job!.state, Is.EqualTo(JoinPointJobState.Pending));
        server.worldData.SendStreamingJoinPointAssignments(job);
        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
    }
}
