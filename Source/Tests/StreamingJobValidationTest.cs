using Multiplayer.Common;

namespace Tests;

/// <summary>
/// Tests for Steps 10-12: upload validation against active streaming job,
/// finalization only on completion, and abort on disconnect/timeout.
/// </summary>
[TestFixture]
public class StreamingJobValidationTest
{
    private MultiplayerServer server;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
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

    private StandaloneJoinPointJob CreateActiveJob(ServerPlayer requester)
    {
        var job = server.worldData.TryCreateStreamingJoinPointJob(requester, "test");
        Assert.That(job, Is.Not.Null);
        server.worldData.SendStreamingJoinPointAssignments(job!);
        Assert.That(job!.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        return job;
    }

    private static byte[] FakeData(int size = 16) => new byte[size];

    // ===== Step 10: Upload validation against active job =====

    [Test]
    public void WorldUpload_AcceptedFromAssignedUploader()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(accepted, Is.True);
        Assert.That(job.receivedWorldUpload, Is.True);
    }

    [Test]
    public void WorldUpload_RejectedFromWrongPlayer()
    {
        var a = AddPlayer(1);
        var b = AddPlayer(2);
        var job = CreateActiveJob(a); // a is world uploader

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            b, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(accepted, Is.False);
        Assert.That(job.receivedWorldUpload, Is.False);
    }

    [Test]
    public void WorldUpload_RejectedDuplicate()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        var secondAccepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 101, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(secondAccepted, Is.False);
    }

    [Test]
    public void WorldUpload_RejectedWhenNoActiveJob()
    {
        var a = AddPlayer(1);

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: 999);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void WorldUpload_RejectedWhenJobIdMismatch()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId + 100);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void MapUpload_AcceptedFromAssignedUploader()
    {
        var a = AddPlayer(1, 2);
        var job = CreateActiveJob(a);

        var accepted = server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(accepted, Is.True);
        Assert.That(job.receivedMapIds, Does.Contain(1));
    }

    [Test]
    public void MapUpload_RejectedFromWrongPlayer()
    {
        var a = AddPlayer(1);
        var b = AddPlayer(2);
        var job = CreateActiveJob(a); // a uploads map 1, b uploads map 2

        // b tries to upload map 1, but a is the assigned uploader for map 1
        var accepted = server.worldData.TryAcceptStandaloneMapSnapshot(
            b, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(accepted, Is.False);
        Assert.That(job.receivedMapIds, Does.Not.Contain(1));
    }

    [Test]
    public void MapUpload_RejectedDuplicate()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        var secondAccepted = server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 101, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(secondAccepted, Is.False);
    }

    [Test]
    public void MapUpload_RejectedWhenNoActiveJob()
    {
        var a = AddPlayer(1);

        var accepted = server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: 999);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void NormalUpload_StillWorksWithoutJobId()
    {
        var a = AddPlayer(1);
        // No job active, jobId=0 => normal standalone path
        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: 0);

        Assert.That(accepted, Is.True);
        Assert.That(server.worldData.savedGame, Is.Not.Null);
    }

    [Test]
    public void NormalMapUpload_StillWorksWithoutJobId()
    {
        var a = AddPlayer(1);

        var accepted = server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: 0);

        Assert.That(accepted, Is.True);
        Assert.That(server.worldData.mapData, Does.ContainKey(1));
    }

    // ===== Step 11: Finalize only on job complete =====

    [Test]
    public void Job_NotFinalized_WhenPartialUploads()
    {
        var a = AddPlayer(1, 2);
        var job = CreateActiveJob(a);

        // Upload world only (maps still missing)
        server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
    }

    [Test]
    public void Job_Finalized_WhenAllUploadsReceived()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        // Upload world
        server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);
        // Upload map 1
        server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void Job_Finalized_MultiPlayer_MultiMap()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);
        var job = CreateActiveJob(a);

        // a uploads world
        server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads), "Should not finalize yet");

        // a uploads map 1 and map 2 (a is uploader for both since a is requester)
        server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 2, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads), "Still waiting for map 3");

        // b uploads map 3 (b is uploader for map 3)
        server.worldData.TryAcceptStandaloneMapSnapshot(
            b, mapId: 3, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job.jobId);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void Job_CanCreateNewJobAfterCompletion()
    {
        var a = AddPlayer(1);
        var job1 = CreateActiveJob(a);

        // Complete it
        server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job1.jobId);
        server.worldData.TryAcceptStandaloneMapSnapshot(
            a, mapId: 1, tick: 100, leaseVersion: 1, FakeData(), [], jobId: job1.jobId);

        Assert.That(job1.state, Is.EqualTo(JoinPointJobState.Completed));

        // New job should be possible
        var job2 = server.worldData.TryCreateStreamingJoinPointJob(a, "second");
        Assert.That(job2, Is.Not.Null);
        Assert.That(job2!.jobId, Is.GreaterThan(job1.jobId));
    }

    // ===== Step 12: Abort on disconnect =====

    [Test]
    public void AbortStreamingJob_SetsStateAborted()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        server.worldData.AbortStreamingJob("test abort");

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Aborted));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void AbortStreamingJob_NoOpWhenNoJob()
    {
        // Should not throw
        server.worldData.AbortStreamingJob("nothing");
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void AbortStreamingJob_NoOpWhenAlreadyCompleted()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);
        job.state = JoinPointJobState.Completed;

        server.worldData.AbortStreamingJob("try abort completed");

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Completed));
    }

    [Test]
    public void Disconnect_AbortsJobForClusterPlayer()
    {
        var a = AddPlayer(1, 2);
        var b = AddPlayer(2, 3);
        var job = CreateActiveJob(a);

        // Disconnecting player b who is in the cluster
        server.playerManager.SetDisconnected(b.conn, MpDisconnectReason.ClientLeft);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Aborted));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void Disconnect_DoesNotAbortForNonClusterPlayer()
    {
        var a = AddPlayer(1);
        var c = AddPlayer(5); // different map, not in cluster
        var job = CreateActiveJob(a);

        server.playerManager.SetDisconnected(c.conn, MpDisconnectReason.ClientLeft);

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
    }

    [Test]
    public void UploadsRejected_AfterJobAborted()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        server.worldData.AbortStreamingJob("aborted");

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job.jobId);

        Assert.That(accepted, Is.False);
    }

    // ===== Step 12: Timeout handling =====

    [Test]
    public void Timeout_AbortsJob()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        // Force the created time to be in the past
        job.createdAtUtc = System.DateTime.UtcNow.AddSeconds(-StandaloneJoinPointJob.TimeoutSeconds - 1);

        server.worldData.CheckStreamingJobTimeout();

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.Aborted));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Null);
    }

    [Test]
    public void Timeout_DoesNotAbortFreshJob()
    {
        var a = AddPlayer(1);
        var job = CreateActiveJob(a);

        server.worldData.CheckStreamingJobTimeout();

        Assert.That(job.state, Is.EqualTo(JoinPointJobState.CollectingUploads));
        Assert.That(server.worldData.activeStreamingJoinPointJob, Is.Not.Null);
    }

    [Test]
    public void IsTimedOut_TrueAfterTimeout()
    {
        var job = new StandaloneJoinPointJob(1, "test", 1)
        {
            createdAtUtc = System.DateTime.UtcNow.AddSeconds(-StandaloneJoinPointJob.TimeoutSeconds - 1),
        };

        Assert.That(job.IsTimedOut, Is.True);
    }

    [Test]
    public void IsTimedOut_FalseBeforeTimeout()
    {
        var job = new StandaloneJoinPointJob(1, "test", 1);

        Assert.That(job.IsTimedOut, Is.False);
    }

    // ===== Edge cases =====

    [Test]
    public void WorldUpload_RejectedWhenJobNotInCollectingState()
    {
        var a = AddPlayer(1);
        var job = server.worldData.TryCreateStreamingJoinPointJob(a, "test");
        // Job is still in Pending state, not CollectingUploads

        var accepted = server.worldData.TryAcceptStandaloneWorldSnapshot(
            a, tick: 100, leaseVersion: 1, FakeData(), FakeData(), [], jobId: job!.jobId);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void CanCreateNewJob_AfterAbort()
    {
        var a = AddPlayer(1);
        var job1 = CreateActiveJob(a);

        server.worldData.AbortStreamingJob("test");

        var job2 = server.worldData.TryCreateStreamingJoinPointJob(a, "retry");
        Assert.That(job2, Is.Not.Null);
        Assert.That(job2!.jobId, Is.GreaterThan(job1.jobId));
    }
}
