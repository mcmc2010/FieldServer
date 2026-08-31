using System.Text.Json;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 battle_leave：离开当前房间的对战（不退出房间）。</summary>
public sealed class BattleLeaveHandler : IMessageHandler
{
    public string MessageType => MessageTypes.BattleLeave;

    public Task HandleAsync(MessageContext context, JsonElement data)
    {
        if (context.Connection.CurrentRoomId is not int roomId)
        {
            return Task.CompletedTask;
        }

        context.Battles.GetBattle(roomId)?.Leave(context.Connection.Id);
        return Task.CompletedTask;
    }
}
