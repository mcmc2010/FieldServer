using System.Text.Json;
using FieldServer.Rooms;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 join：加入指定房间（自动离开旧房间）。</summary>
public sealed class JoinRoomHandler : IMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public string MessageType => MessageTypes.Join;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (!TryDeserialize(data, out JoinPayload? payload) || payload is null)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("join 参数无效，期望 { roomId: number }"));
            return;
        }

        var room = context.Rooms.GetRoom(payload.RoomId);
        if (room is null)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error($"房间 {payload.RoomId} 不存在（共 {context.Rooms.RoomCount} 个）"));
            return;
        }

        if (context.Connection.CurrentRoomId == room.Id)
        {
            var current = context.Movements.GetOrCreateMovement(room.Id);
            if (!current.TryGet(context.Connection.Id, out var currentPos))
                currentPos = current.Spawn(context.Connection.Id);
            await context.Connection.SendAsync(OutgoingMessage.Of(MessageTypes.Joined,
                new JoinedPayload(room.Id, context.Connection.Id, room.MemberCount, currentPos.X, currentPos.Y)));
            return;
        }

        if (context.Connection.CurrentRoomId is int oldRoomId)
            RoomHelper.LeaveRoom(context.Rooms, context.Connection, oldRoomId,
                battles: context.Battles, movements: context.Movements);

        if (!room.TryJoin(context.Connection))
        {
            await context.Connection.SendAsync(OutgoingMessage.Error($"房间 {room.Id} 已满"));
            return;
        }
        context.Connection.CurrentRoomId = room.Id;

        var spawn = context.Movements.GetOrCreateMovement(room.Id).Spawn(context.Connection.Id);
        await context.Connection.SendAsync(OutgoingMessage.Of(MessageTypes.Joined,
            new JoinedPayload(room.Id, context.Connection.Id, room.MemberCount, spawn.X, spawn.Y)));

        room.Broadcast(OutgoingMessage.Of(MessageTypes.System,
            new SystemPayload($"{context.Connection.Id} 加入了房间 {room.Id}")),
            excludeConnectionId: context.Connection.Id);
    }

    private static bool TryDeserialize<T>(JsonElement data, out T? value)
    {
        value = default;
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return false;
        try { value = data.Deserialize<T>(JsonOptions); return true; }
        catch (JsonException) { return false; }
    }
}
