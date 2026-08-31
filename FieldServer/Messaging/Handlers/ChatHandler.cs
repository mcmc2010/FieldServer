using System.Text.Json;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 chat：向当前房间广播（含发送者，作为送达回执）。</summary>
public sealed class ChatHandler : IMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public string MessageType => MessageTypes.Chat;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("尚未加入房间，无法发言"));
            return;
        }

        ChatPayload? payload;
        try { payload = data.Deserialize<ChatPayload>(JsonOptions); }
        catch (JsonException) { payload = null; }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Content))
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("chat 参数无效，期望 { content: string }"));
            return;
        }

        var room = context.Rooms.GetRoom(roomId);
        if (room is null)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("房间已不存在"));
            return;
        }

        room.Broadcast(OutgoingMessage.Of(MessageTypes.Chat,
            new ChatEventPayload(room.Id, context.Connection.Id, payload.Content,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
    }
}
