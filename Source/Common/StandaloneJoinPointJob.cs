using System;
using System.Collections.Generic;

namespace Multiplayer.Common;

public enum JoinPointJobState : byte
{
    Pending,
    CollectingUploads,
    Completed,
    Aborted
}

public class StandaloneJoinPointJob
{
    public const int TimeoutSeconds = 30;

    public int jobId;
    public string reason;
    public int requestingPlayerId;
    public DateTime createdAtUtc = DateTime.UtcNow;

    // Cluster determined by flood-fill
    public HashSet<int> clusterPlayerIds = new();
    public HashSet<int> clusterMapIds = new();

    // Upload assignments (one uploader per resource)
    public int worldUploaderPlayerId;
    public Dictionary<int, int> mapUploaderByMapId = new(); // mapId -> playerId

    // Received upload tracking
    public bool receivedWorldUpload;
    public HashSet<int> receivedMapIds = new();

    public JoinPointJobState state = JoinPointJobState.Pending;

    public StandaloneJoinPointJob(int jobId, string reason, int requestingPlayerId)
    {
        this.jobId = jobId;
        this.reason = reason;
        this.requestingPlayerId = requestingPlayerId;
    }

    public bool IsComplete =>
        receivedWorldUpload && receivedMapIds.Count == clusterMapIds.Count;

    public bool IsTimedOut =>
        (DateTime.UtcNow - createdAtUtc).TotalSeconds >= TimeoutSeconds;

    public bool IsUploaderForMap(int playerId, int mapId) =>
        mapUploaderByMapId.TryGetValue(mapId, out var assigned) && assigned == playerId;

    public bool IsWorldUploader(int playerId) =>
        worldUploaderPlayerId == playerId;
}
