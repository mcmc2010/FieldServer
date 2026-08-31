using System.Text.Json;
using FieldServer.Battle;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 battle_join：加入当前所在房间的对战（可指定队伍 "A"/"B"）。</summary>
public sealed class BattleJoinHandler : IMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public string MessageType => MessageTypes.BattleJoin;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("尚未加入房间，请先 join"));
            return;
        }

        BattleJoinPayload? payload = null;
        if (data.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            try { payload = data.Deserialize<BattleJoinPayload>(JsonOptions); }
            catch (JsonException) { /* 按无队伍偏好处理 */ }
        }

        context.Battles.GetOrCreateBattle(roomId).Join(context.Connection, payload?.Team);
    }
}
