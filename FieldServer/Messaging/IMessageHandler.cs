using System.Text.Json;
using FieldServer.Battle;
using FieldServer.Connections;
using FieldServer.Rooms;

namespace FieldServer.Messaging;

/// <summary>
/// 消息处理器扩展点。
/// 新增功能 = 新建一个实现类 + 在 Program.cs 注册一行 DI，无需改动既有代码。
/// 构造函数可注入任意服务（如未来的持久化、认证等）。
/// </summary>
public interface IMessageHandler
{
    /// <summary>处理的消息类型（对应 <see cref="MessageTypes"/>）。</summary>
    string MessageType { get; }

    Task HandleAsync(MessageContext context, JsonElement data);
}

/// <summary>一次消息调用的上下文：当前连接 + 房间/对战服务 + 取消令牌。</summary>
public sealed class MessageContext
{
    public required IClientConnection Connection { get; init; }
    public required IRoomManager Rooms { get; init; }
    public required IBattleManager Battles { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}
