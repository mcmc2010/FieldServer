namespace FieldServer.Battle;

/// <summary>对战管理：每个房间最多一场对战，按需创建。</summary>
public interface IBattleManager
{
    /// <summary>取得（或创建）指定房间的对战实例。</summary>
    IBattle GetOrCreateBattle(int roomId);

    /// <summary>仅查询，不创建。</summary>
    IBattle? GetBattle(int roomId);
}
