using FieldServer.Connections;

namespace FieldServer.Battle;

/// <summary>一场对战（挂载在某个房间上）。</summary>
public interface IBattle
{
    int RoomId { get; }
    BattleState State { get; }
    int PlayerCount { get; }

    /// <summary>加入对战（自动/指定队伍），结果直接回消息给该连接。满员自动开战。</summary>
    void Join(IClientConnection connection, string? preferredTeam);

    /// <summary>离开对战（主动/断连/换房）。对战中离开按阵亡处理。</summary>
    void Leave(string connectionId);

    /// <summary>执行战斗动作（当前支持 attack）。</summary>
    void Action(IClientConnection connection, string action, string? targetId);
}
