namespace FieldServer.Battle;

/// <summary>对战状态机。</summary>
public enum BattleState
{
    /// <summary>组队中，等待满员。</summary>
    Waiting,

    /// <summary>对战进行中。</summary>
    InProgress,

    /// <summary>已分胜负（广播结果后立即重置回 Waiting）。</summary>
    Finished
}
