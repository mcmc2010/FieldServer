using FieldServer.Battle;
using FieldServer.Movement;
using FieldServer.Rooms;

namespace FieldServer.Endpoints;

/// <summary>
/// HTTP 调试端点：服务信息、房间/对战/移动状态查询。
/// 所有 HTTP 端点集中在 Endpoints/ 下，Program.cs 只调用 MapXxxEndpoints。
/// </summary>
public static class DebugEndpoints
{
    public static void MapDebugEndpoints(this WebApplication app)
    {
        // 服务信息
        app.MapGet("/", () => Results.Text("FieldServer is running", "text/plain"))
            .WithName("GetServerInfo");

        // 房间状态（可用来验证 YAML 配置是否生效）
        app.MapGet("/rooms", (IRoomManager rooms) => Results.Json(new
        {
            roomCount = rooms.RoomCount,
            totalMembers = rooms.Rooms.Sum(r => r.MemberCount),
            rooms = rooms.Rooms.Select(r => new { r.Id, r.Name, r.MemberCount })
        })).WithName("GetRooms");

        // 对战状态
        app.MapGet("/battles/{roomId:int}", (int roomId, IBattleManager battles) =>
        {
            var battle = battles.GetBattle(roomId);
            return battle is null
                ? Results.NotFound(new { roomId, state = "none" })
                : Results.Json(new { roomId, state = battle.State.ToString(), players = battle.PlayerCount });
        }).WithName("GetBattle");

        // 房间内玩家位置
        app.MapGet("/movement/{roomId:int}", (int roomId, IMovementManager movements) =>
        {
            var movement = movements.GetMovement(roomId);
            return movement is null
                ? Results.NotFound(new { roomId, state = "none" })
                : Results.Json(new { roomId, players = movement.Snapshot() });
        }).WithName("GetMovement");
    }
}
