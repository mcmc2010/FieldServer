using System.Text.Json;
using FieldServer.Connections;
using FieldServer.Rooms;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 watch_all：把当前连接注册为全局观察者，接收全服房间/对战广播（dashboard 用）。</summary>
public sealed class WatchAllHandler : IMessageHandler
{
    private readonly IGlobalObserver _observer;
    private readonly IRoomManager _rooms;

    public WatchAllHandler(IGlobalObserver observer, IRoomManager rooms)
    {
        _observer = observer;
        _rooms = rooms;
    }

    public string MessageType => MessageTypes.WatchAll;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        _observer.Add(context.Connection);
        await context.Connection.SendAsync(OutgoingMessage.Of(MessageTypes.Watching,
            new WatchingPayload(_rooms.RoomCount, _observer.WatcherCount)));
    }
}
