namespace Multiplayer.Common.Networking.Packet;

/// <summary>
/// Sent from server to each player in the cluster, telling them what to save and upload.
/// Only used when CanUseStandaloneMapStreaming is true.
/// </summary>
[PacketDefinition(Packets.Server_StreamingJoinPointRequest)]
public record struct ServerStreamingJoinPointRequestPacket : IPacket
{
    public int jobId;
    public string reason;
    public int[] mapIdsToSave;
    public bool mustUploadWorld;
    public int[] mapIdsToUpload;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref jobId);
        buf.Bind(ref reason);
        buf.Bind(ref mapIdsToSave, BinderOf.Int(), maxLength: 128);
        buf.Bind(ref mustUploadWorld);
        buf.Bind(ref mapIdsToUpload, BinderOf.Int(), maxLength: 128);
    }
}
