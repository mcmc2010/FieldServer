using System.Text.Json;
using FieldServer.Movement;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 move：校验并应用移动，成功由 RoomMovement 广播 moved（含发送者作为权威回执）。</summary>
public sealed class MoveHandler : IMessageHandler
{
    public string MessageType => MessageTypes.Move;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("尚未加入房间，无法移动"));
            return;
        }

        // 直接从 JsonElement 取数：缺字段/非数字/非有限值一律拒绝（"{}" 不等于移动到 (0,0)）
        // 注意：非 Number 元素调 TryGetDouble 会抛 InvalidOperationException，必须先判 ValueKind
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number
            || !xEl.TryGetDouble(out var x)
            || !data.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number
            || !yEl.TryGetDouble(out var y)
            || !double.IsFinite(x) || !double.IsFinite(y))
        {
            await context.Connection.SendAsync(OutgoingMessage.Error("move 参数无效，期望 { x: number, y: number }"));
            return;
        }

        var movement = context.Movements.GetOrCreateMovement(roomId);
        switch (movement.Move(context.Connection.Id, x, y, out _))
        {
            case MoveResult.NotTracked:
                await context.Connection.SendAsync(OutgoingMessage.Error("位置未初始化，请重新加入房间"));
                break;
            case MoveResult.TooFast:
                await context.Connection.SendAsync(OutgoingMessage.Error("移动速度超限，本次移动已忽略"));
                break;
        }
    }
}
