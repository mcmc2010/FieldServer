using System.Text.Json;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 battle_action：战斗动作（当前支持 attack）。</summary>
public sealed class BattleActionHandler : IMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public string MessageType => MessageTypes.BattleAction;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("尚未加入房间"));
            return;
        }

        BattleActionPayload? payload;
        try { payload = data.Deserialize<BattleActionPayload>(JsonOptions); }
        catch (JsonException) { payload = null; }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Action))
        {
            await context.Connection.SendAsync(OutgoingMessage.Error(
                "battle_action 参数无效，期望 { action: \"attack\", targetId: \"...\" }"));
            return;
        }

        context.Battles.GetOrCreateBattle(roomId).Action(context.Connection, payload.Action, payload.TargetId);
    }
}
