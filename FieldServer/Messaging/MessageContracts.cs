using System.Text.Json;

namespace FieldServer.Messaging;

/// <summary>消息类型常量。新增消息类型在此登记。</summary>
public static class MessageTypes
{
    // 客户端 → 服务器
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Chat = "chat";
    public const string Ping = "ping";

    // 客户端 → 服务器（对战）
    public const string BattleJoin = "battle_join";
    public const string BattleLeave = "battle_leave";
    public const string BattleAction = "battle_action";

    // 服务器 → 客户端
    public const string Joined = "joined";
    public const string Left = "left";
    public const string Pong = "pong";
    public const string System = "system";
    public const string Error = "error";

    // 服务器 → 客户端（对战）
    public const string BattleJoined = "battle_joined";
    public const string BattlePlayerJoined = "battle_player_joined";
    public const string BattlePlayerLeft = "battle_player_left";
    public const string BattleStarted = "battle_started";
    public const string BattleActionEvent = "battle_action_event";
    public const string BattlePlayerDied = "battle_player_died";
    public const string BattleEnded = "battle_ended";
}

/// <summary>客户端 → 服务器 消息信封：{ "type": "...", "data": { ... } }</summary>
public sealed class IncomingMessage
{
    public string Type { get; set; } = string.Empty;
    public JsonElement Data { get; set; }
}

/// <summary>服务器 → 客户端 消息信封。</summary>
public sealed class OutgoingMessage
{
    public required string Type { get; init; }
    public object? Data { get; init; }

    public static OutgoingMessage Of(string type, object? data) => new() { Type = type, Data = data };
    public static OutgoingMessage Error(string content) => Of(MessageTypes.Error, new ErrorPayload(content));
}

// ---- 各消息的 data 载荷约定 ----

public sealed record JoinPayload(int RoomId);
public sealed record ChatPayload(string Content);
public sealed record PingPayload(long Timestamp);

public sealed record JoinedPayload(int RoomId, string ConnectionId, int MemberCount);
public sealed record LeftPayload(int RoomId, string ConnectionId);
public sealed record ChatEventPayload(int RoomId, string From, string Content, long Timestamp);
public sealed record PongPayload(long Timestamp, long ServerTimestamp);
public sealed record SystemPayload(string Content);
public sealed record ErrorPayload(string Content);

// ---- 对战消息载荷 ----

public sealed record BattleJoinPayload(string? Team);
public sealed record BattleActionPayload(string Action, string? TargetId);

public sealed record BattlePlayerInfo(string Id, string Team, int Hp, bool Alive);
public sealed record BattleJoinedPayload(int RoomId, string Team, string State, List<BattlePlayerInfo> Players);
public sealed record BattlePlayerJoinedPayload(int RoomId, string PlayerId, string Team, int TeamACount, int TeamBCount);
public sealed record BattlePlayerLeftPayload(int RoomId, string PlayerId, string Team);
public sealed record BattleStartedPayload(int RoomId, List<BattlePlayerInfo> Players);
public sealed record BattleActionEventPayload(int RoomId, string ActorId, string Action, string TargetId, int Damage, int TargetHp);
public sealed record BattlePlayerDiedPayload(int RoomId, string PlayerId, string Team, string Reason);
public sealed record BattleEndedPayload(int RoomId, string? WinnerTeam, int TeamAAlive, int TeamBAlive);
