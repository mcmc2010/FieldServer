using FieldServer.Messaging;

namespace FieldServer.Connections;

/// <summary>
/// 单个客户端连接的抽象。发送经独立队列异步执行，
/// 保证广播不会被慢消费者阻塞。
/// </summary>
public interface IClientConnection
{
    string Id { get; }

    /// <summary>当前所在房间，未加入为 null。</summary>
    int? CurrentRoomId { get; set; }

    /// <summary>序列化后入队发送。</summary>
    ValueTask SendAsync(OutgoingMessage message);

    /// <summary>已序列化载荷直接入队（房间广播用，整房间只序列化一次）。</summary>
    bool EnqueueRaw(byte[] payload);

    Task CloseAsync(string reason);
}
