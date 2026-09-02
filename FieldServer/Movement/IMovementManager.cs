namespace FieldServer.Movement;

/// <summary>按房间懒创建移动状态实例（与 IBattleManager 同一模式）。</summary>
public interface IMovementManager
{
    IRoomMovement GetOrCreateMovement(int roomId);
    IRoomMovement? GetMovement(int roomId);
}
