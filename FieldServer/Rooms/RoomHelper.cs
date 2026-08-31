using FieldServer.Battle;
using FieldServer.Connections;
using FieldServer.Messaging;

namespace FieldServer.Rooms;

/// <summary>处理器与会话层共用的房间操作。</summary>
public static class RoomHelper
{
    /// <summary>离开指定房间并通知其他成员；若在该房间的对战中，同时退出对战（对战中按阵亡处理）。</summary>
    public static void LeaveRoom(IRoomManager rooms, IClientConnection connection, int roomId, bool notify = true,
        IBattleManager? battles = null)
    {
        battles?.GetBattle(roomId)?.Leave(connection.Id);

        var room = rooms.GetRoom(roomId);
        if (room is null) return;
        if (!room.Leave(connection.Id)) return;

        connection.CurrentRoomId = null;
        if (notify)
        {
            room.Broadcast(OutgoingMessage.Of(MessageTypes.System,
                new SystemPayload($"{connection.Id} 离开了房间 {room.Id}")));
        }
    }
}
