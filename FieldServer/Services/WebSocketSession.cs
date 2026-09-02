using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FieldServer.Battle;
using FieldServer.Configuration;
using FieldServer.Connections;
using FieldServer.Messaging;
using FieldServer.Movement;
using FieldServer.Rooms;

namespace FieldServer.Services;

/// <summary>
/// 单个 WebSocket 会话：接收循环 → JSON 解析 → 消息分发，
/// 断开时自动清理房间成员关系和对战状态。
/// </summary>
public sealed class WebSocketSession
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly MessageDispatcher _dispatcher;
    private readonly IRoomManager _rooms;
    private readonly IBattleManager _battles;
    private readonly IMovementManager _movements;
    private readonly IGlobalObserver _observer;
    private readonly FieldServerOptions _options;
    private readonly ILogger<WebSocketSession> _logger;

    public WebSocketSession(
        MessageDispatcher dispatcher,
        IRoomManager rooms,
        IBattleManager battles,
        IMovementManager movements,
        IGlobalObserver observer,
        IOptions<FieldServerOptions> options,
        ILogger<WebSocketSession> logger)
    {
        _dispatcher = dispatcher;
        _rooms = rooms;
        _battles = battles;
        _movements = movements;
        _observer = observer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await using var connection = new WebSocketClientConnection(socket, _options, _logger);
        var context = new MessageContext
        {
            Connection = connection,
            Rooms = _rooms,
            Battles = _battles,
            Movements = _movements,
            CancellationToken = cancellationToken
        };

        var buffer = ArrayPool<byte>.Shared.Rent(_options.MaxMessageBytes + 1024);
        _logger.LogDebug("连接 {Id} 建立", connection.Id);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var messageBytes = await ReceiveFullMessageAsync(socket, buffer, cancellationToken);
                if (messageBytes is null) break; // Close 帧或超限断开

                IncomingMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IncomingMessage>(messageBytes, JsonOptions);
                }
                catch (JsonException)
                {
                    await connection.SendAsync(OutgoingMessage.Error("消息不是合法 JSON"));
                    continue;
                }

                if (message is null || string.IsNullOrWhiteSpace(message.Type))
                {
                    await connection.SendAsync(OutgoingMessage.Error("缺少消息类型 type"));
                    continue;
                }

                try
                {
                    await _dispatcher.DispatchAsync(context, message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单条消息的处理器异常不应杀死会话
                    _logger.LogError(ex, "处理消息 {Type} 时处理器异常", message.Type);
                    await connection.SendAsync(OutgoingMessage.Error("服务器内部错误"));
                }
            }
        }
        catch (WebSocketException) { /* 客户端异常断开 */ }
        catch (OperationCanceledException) { /* 服务器关闭 */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _observer.Remove(connection.Id); // 若是观察者（dashboard）则注销
            if (connection.CurrentRoomId is int roomId)
                RoomHelper.LeaveRoom(_rooms, connection, roomId,
                    battles: _battles, movements: _movements); // 断连自动退房+退对战+清理位置
            _logger.LogDebug("连接 {Id} 结束", connection.Id);
        }
    }

    /// <summary>读取一条完整消息（可能跨多个帧），超限或收到 Close 返回 null。</summary>
    private async Task<byte[]?> ReceiveFullMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(total), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* 忽略 */ }
                return null;
            }

            total += result.Count;
            if (total > _options.MaxMessageBytes)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "消息过大", CancellationToken.None); }
                catch { /* 忽略 */ }
                return null;
            }

            if (result.EndOfMessage)
                return buffer.AsSpan(0, total).ToArray();
        }
    }
}
