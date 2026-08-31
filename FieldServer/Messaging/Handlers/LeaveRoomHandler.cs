using System.Text.Json;
using FieldServer.Rooms;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 leave：离开当前房间。</summary>
public sealed class LeaveRoomHandler : IMessageHandler
{
    public string MessageType => MessageTypes.Leave;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("尚未加入任何房间"));
            return;
        }

        RoomHelper.LeaveRoom(context.Rooms, context.Connection, roomId, battles: context.Battles);
        await context.Connection.SendAsync(OutgoingMessage.Of(MessageTypes.Left,
            new LeftPayload(roomId, context.Connection.Id)));
    }
}
