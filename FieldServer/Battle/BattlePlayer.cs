using FieldServer.Connections;

namespace FieldServer.Battle;

/// <summary>对战中的玩家状态。</summary>
public sealed class BattlePlayer
{
    public required IClientConnection Connection { get; init; }
    public required string Team { get; init; } // "A" / "B"
    public int Hp { get; set; }

    public string Id => Connection.Id;
    public bool Alive => Hp > 0;
}
