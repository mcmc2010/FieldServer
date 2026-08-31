namespace FieldServer.Configuration;

/// <summary>
/// 服务器配置，由 rooms.yaml 的 FieldServer 节绑定。
/// 新增配置项时在此处添加属性即可。
/// </summary>
public sealed class FieldServerOptions
{
    public const string SectionName = "FieldServer";

    /// <summary>启动时创建的房间数量。</summary>
    public int RoomCount { get; set; } = 128;

    /// <summary>每房间最大连接数。</summary>
    public int MaxConnectionsPerRoom { get; set; } = 64;

    /// <summary>每连接发送队列容量。</summary>
    public int SendQueueCapacity { get; set; } = 1024;

    /// <summary>单条消息最大字节数。</summary>
    public int MaxMessageBytes { get; set; } = 4096;

    /// <summary>对战配置。</summary>
    public BattleOptions Battle { get; set; } = new();
}

/// <summary>
/// 对战参数。TeamSize=5 即 5v5；改为 3 即 3v3，无需改代码。
/// </summary>
public sealed class BattleOptions
{
    /// <summary>每队人数。</summary>
    public int TeamSize { get; set; } = 5;

    /// <summary>玩家初始血量。</summary>
    public int PlayerHp { get; set; } = 100;

    /// <summary>单次攻击伤害。</summary>
    public int AttackDamage { get; set; } = 20;
}
