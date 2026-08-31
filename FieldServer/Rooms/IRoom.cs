using FieldServer.Connections;
using FieldServer.Messaging;

namespace FieldServer.Rooms;

/// <summary>房间：成员集合 + 广播能力。</summary>
public interface IRoom
{
    int Id { get; }
    string Name { get; }
    int MemberCount { get; }

    /// <summary>加入房间，满员或已在房间返回 false。</summary>
    bool TryJoin(IClientConnection connection);

    /// <summary>离开房间，不在房间返回 false。</summary>
    bool Leave(string connectionId);

    /// <summary>向全部成员广播（消息只序列化一次）。可排除某个连接。</summary>
    void Broadcast(OutgoingMessage message, string? excludeConnectionId = null);
}
