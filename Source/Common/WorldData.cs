using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Multiplayer.Common;

public class WorldData
{
    public int hostFactionId;
    public int spectatorFactionId;
    public byte[]? savedGame; // Compressed game save
    public byte[]? sessionData; // Compressed semi persistent data
    public Dictionary<int, byte[]> mapData = new(); // Map id to compressed map data

    public Dictionary<int, List<byte[]>> mapCmds = new(); // Map id to serialized cmds list
    public Dictionary<int, List<byte[]>>? tmpMapCmds;
    public int lastJoinPointAtTick = -1;

    public List<byte[]> syncInfos = new();

    public StandaloneWorldSnapshotState standaloneWorldSnapshot = new();
    public Dictionary<int, StandaloneMapSnapshotState> standaloneMapSnapshots = new();

    // Active streaming join point job (only when CanUseStandaloneMapStreaming is true)
    public StandaloneJoinPointJob? activeStreamingJoinPointJob;
    private int nextStreamingJobId;

    private TaskCompletionSource<WorldData>? dataSource;

    public bool CreatingJoinPoint => tmpMapCmds != null;

    public MultiplayerServer Server { get; }

    public WorldData(MultiplayerServer server)
    {
        Server = server;
    }

    /// <summary>
    /// Compute the transitive closure of players and maps starting from the requesting player.
    /// All players that share any map with any player already in the cluster are included,
    /// along with all their loaded maps. Repeats until no new players/maps are found.
    /// </summary>
    public static (HashSet<int> playerIds, HashSet<int> mapIds) ComputeJoinPointCluster(
        ServerPlayer requestingPlayer, IEnumerable<ServerPlayer> allPlayers)
    {
        var clusterPlayerIds = new HashSet<int> { requestingPlayer.id };
        var clusterMapIds = new HashSet<int>(requestingPlayer.loadedMaps);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var player in allPlayers)
            {
                if (clusterPlayerIds.Contains(player.id))
                    continue;

                // Does this player share any map with the cluster?
                foreach (var mapId in player.loadedMaps)
                {
                    if (clusterMapIds.Contains(mapId))
                    {
                        clusterPlayerIds.Add(player.id);
                        // Add all of this player's maps to the cluster
                        foreach (var m in player.loadedMaps)
                            changed |= clusterMapIds.Add(m);
                        break;
                    }
                }
            }
        }

        return (clusterPlayerIds, clusterMapIds);
    }

    /// <summary>
    /// Create a streaming join point job: compute the cluster, assign uploaders, return the job.
    /// Returns null if a job is already active or if the requesting player has no loaded maps.
    /// </summary>
    public StandaloneJoinPointJob? TryCreateStreamingJoinPointJob(
        ServerPlayer requestingPlayer, string reason)
    {
        if (activeStreamingJoinPointJob is { state: JoinPointJobState.Pending or JoinPointJobState.CollectingUploads })
        {
            ServerLog.Detail("Streaming join point skipped: job already active");
            return null;
        }

        if (requestingPlayer.loadedMaps.Count == 0)
        {
            ServerLog.Detail($"Streaming join point skipped: player {requestingPlayer.Username} has no loaded maps");
            return null;
        }

        var (playerIds, mapIds) = ComputeJoinPointCluster(requestingPlayer, Server.PlayingPlayers);

        var job = new StandaloneJoinPointJob(++nextStreamingJobId, reason, requestingPlayer.id)
        {
            clusterPlayerIds = playerIds,
            clusterMapIds = mapIds,
            state = JoinPointJobState.Pending,
        };

        // Assign world uploader: requesting player preferred
        job.worldUploaderPlayerId = requestingPlayer.id;

        // Assign map uploaders: requesting player if they have it, else lowest id
        foreach (var mapId in mapIds)
        {
            if (requestingPlayer.loadedMaps.Contains(mapId))
            {
                job.mapUploaderByMapId[mapId] = requestingPlayer.id;
            }
            else
            {
                int bestId = int.MaxValue;
                foreach (var player in Server.PlayingPlayers)
                {
                    if (playerIds.Contains(player.id) && player.loadedMaps.Contains(mapId) && player.id < bestId)
                        bestId = player.id;
                }

                job.mapUploaderByMapId[mapId] = bestId;
            }
        }

        activeStreamingJoinPointJob = job;
        return job;
    }

    /// <summary>
    /// Send assignment packets to all players in the active job's cluster and transition to CollectingUploads.
    /// </summary>
    public void SendStreamingJoinPointAssignments(StandaloneJoinPointJob job)
    {
        foreach (var player in Server.PlayingPlayers)
        {
            if (!job.clusterPlayerIds.Contains(player.id))
                continue;

            // Maps this player should save locally (intersection of their loadedMaps and cluster)
            var mapsToSave = player.loadedMaps.Where(m => job.clusterMapIds.Contains(m)).ToArray();

            // Maps this player is assigned to upload
            var mapsToUpload = job.mapUploaderByMapId
                .Where(kv => kv.Value == player.id)
                .Select(kv => kv.Key)
                .ToArray();

            var packet = new Networking.Packet.ServerStreamingJoinPointRequestPacket
            {
                jobId = job.jobId,
                reason = job.reason,
                mapIdsToSave = mapsToSave,
                mustUploadWorld = job.worldUploaderPlayerId == player.id,
                mapIdsToUpload = mapsToUpload,
            };

            player.SendPacket(packet);
            ServerLog.Detail($"Streaming join point job {job.jobId}: assigned player {player.Username} " +
                             $"save=[{string.Join(",", mapsToSave)}] uploadMaps=[{string.Join(",", mapsToUpload)}] " +
                             $"uploadWorld={job.worldUploaderPlayerId == player.id}");
        }

        job.state = JoinPointJobState.CollectingUploads;
    }

    private int CurrentJoinPointTick => Server.IsStandaloneServer ? Server.gameTimer : Server.workTicks;

    public bool TryStartJoinPointCreation(bool force = false, ServerPlayer? sourcePlayer = null)
    {
        int currentTick = CurrentJoinPointTick;

        if (!force && lastJoinPointAtTick >= 0 && currentTick - lastJoinPointAtTick < 30)
        {
            ServerLog.Detail($"Join point skipped: cooldown active at tick={currentTick}, last={lastJoinPointAtTick}, standalone={Server.IsStandaloneServer}");
            return false;
        }

        if (CreatingJoinPoint)
        {
            ServerLog.Detail("Join point skipped: already creating one");
            return false;
        }

        ServerLog.Detail($"Join point started at tick={currentTick}, force={force}, standalone={Server.IsStandaloneServer}");
        Server.SendChat("Creating a join point...");

        Server.commands.Send(CommandType.CreateJoinPoint, ScheduledCommand.NoFaction, ScheduledCommand.Global, Array.Empty<byte>(),
            sourcePlayer: Server.IsStandaloneServer ? sourcePlayer : null);
        tmpMapCmds = new Dictionary<int, List<byte[]>>();
        dataSource = new TaskCompletionSource<WorldData>();

        return true;
    }

    public void EndJoinPointCreation()
    {
        int currentTick = CurrentJoinPointTick;
        ServerLog.Detail($"Join point completed at tick={currentTick}, standalone={Server.IsStandaloneServer}");
        mapCmds = tmpMapCmds!;
        tmpMapCmds = null;
        lastJoinPointAtTick = currentTick;

        if (Server.IsStandaloneServer && Server.persistence != null)
        {
            try
            {
                Server.persistence.WriteJoinPoint(this, currentTick);
            }
            catch (Exception e)
            {
                ServerLog.Error($"Failed to persist standalone join point at tick={currentTick}: {e}");
            }
        }

        dataSource!.SetResult(this);
        dataSource = null;
    }

    public void AbortJoinPointCreation()
    {
        if (!CreatingJoinPoint)
            return;

        tmpMapCmds = null;
        dataSource?.SetResult(this);
        dataSource = null;
    }

    public Task<WorldData> WaitJoinPoint()
    {
        return dataSource?.Task ?? Task.FromResult(this);
    }

    public bool TryAcceptStandaloneWorldSnapshot(ServerPlayer player, int tick, int leaseVersion, byte[] worldSnapshot,
        byte[] sessionSnapshot, byte[] expectedHash)
    {
        if (tick < standaloneWorldSnapshot.tick)
            return false;

        var actualHash = ComputeHash(worldSnapshot, sessionSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        savedGame = worldSnapshot;
        sessionData = sessionSnapshot;
        standaloneWorldSnapshot = new StandaloneWorldSnapshotState
        {
            tick = tick,
            leaseVersion = leaseVersion,
            producerPlayerId = player.id,
            producerUsername = player.Username,
            sha256Hash = actualHash
        };

        // Persist to disk
        Server.persistence?.WriteWorldSnapshot(worldSnapshot, sessionSnapshot, tick);

        return true;
    }

    public bool TryAcceptStandaloneMapSnapshot(ServerPlayer player, int mapId, int tick, int leaseVersion,
        byte[] mapSnapshot, byte[] expectedHash)
    {
        if (mapId < 0)
            return false;

        var snapshotState = standaloneMapSnapshots.GetOrAddNew(mapId);
        if (tick < snapshotState.tick)
            return false;

        var actualHash = ComputeHash(mapSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        mapData[mapId] = mapSnapshot;
        snapshotState.tick = tick;
        snapshotState.leaseVersion = leaseVersion;
        snapshotState.producerPlayerId = player.id;
        snapshotState.producerUsername = player.Username;
        snapshotState.sha256Hash = actualHash;
        standaloneMapSnapshots[mapId] = snapshotState;

        // Persist to disk
        Server.persistence?.WriteMapSnapshot(mapId, mapSnapshot);

        return true;
    }

    private static byte[] ComputeHash(params byte[][] payloads)
    {
        using var hasher = SHA256.Create();
        foreach (var payload in payloads)
        {
            hasher.TransformBlock(payload, 0, payload.Length, null, 0);
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hasher.Hash ?? Array.Empty<byte>();
    }
}

public struct StandaloneWorldSnapshotState
{
    public StandaloneWorldSnapshotState() { }
    public int tick;
    public int leaseVersion;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}

public struct StandaloneMapSnapshotState
{
    public StandaloneMapSnapshotState() { }
    public int tick;
    public int leaseVersion;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}
