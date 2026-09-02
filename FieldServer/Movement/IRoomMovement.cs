namespace FieldServer.Movement;

/// <summary>玩家位置（2D 场地坐标）。</summary>
public sealed record PlayerPosition(double X, double Y);

/// <summary>某玩家的位置快照（调试端点 / joined 载荷用）。</summary>
public sealed record PlayerPositionInfo(string ConnectionId, double X, double Y);

/// <summary>移动结果。</summary>
public enum MoveResult
{
    Ok,
    /// <summary>该连接没有位置记录（正常流程不会发生，需先 Spawn）。</summary>
    NotTracked,
    /// <summary>超过速度上限（疑似瞬移），本次移动被忽略。</summary>
    TooFast,
}

/// <summary>单个房间的移动状态：成员位置表 + 移动校验 + 广播。</summary>
public interface IRoomMovement
{
    int RoomId { get; }
    int PlayerCount { get; }

    /// <summary>在出生点（场地中心）放置玩家，返回出生位置。</summary>
    PlayerPosition Spawn(string connectionId);

    /// <summary>移除玩家位置（退房/断连时调用），不在表内返回 false。</summary>
    bool Remove(string connectionId);

    bool TryGet(string connectionId, out PlayerPosition position);

    /// <summary>校验并应用移动：边界钳制 + 速度上限检查；成功则广播 moved。</summary>
    MoveResult Move(string connectionId, double x, double y, out PlayerPosition position);

    /// <summary>当前全部位置快照。</summary>
    IReadOnlyList<PlayerPositionInfo> Snapshot();
}
