using System.Text.Json;
using FieldServer.Configuration;
using FieldServer.Connections;
using FieldServer.Messaging;

namespace FieldServer.Battle;

/// <summary>
/// 5v5 对战实例：状态机 Waiting → InProgress → Finished →（广播结果后自动重置 Waiting）。
/// 所有状态修改在 _gate 锁内完成；发送均为 Channel 入队操作（非阻塞），锁内安全。
/// </summary>
public sealed class Battle : IBattle
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly object _gate = new();
    private readonly Dictionary<string, BattlePlayer> _players = new();
    private readonly BattleOptions _options;
    private readonly ILogger _logger;

    public Battle(int roomId, BattleOptions options, ILogger logger)
    {
        RoomId = roomId;
        _options = options;
        _logger = logger;
    }

    public int RoomId { get; }
    public BattleState State { get; private set; } = BattleState.Waiting;

    public int PlayerCount
    {
        get { lock (_gate) return _players.Count; }
    }

    public void Join(IClientConnection connection, string? preferredTeam)
    {
        lock (_gate)
        {
            if (_players.ContainsKey(connection.Id)) { Reply(connection, OutgoingMessage.Error("你已在对战中")); return; }
            if (State != BattleState.Waiting) { Reply(connection, OutgoingMessage.Error("对战进行中，请等待本场结束")); return; }
            if (_players.Count >= _options.TeamSize * 2) { Reply(connection, OutgoingMessage.Error("对战已满")); return; }

            var team = AssignTeam(preferredTeam);
            if (team is null) { Reply(connection, OutgoingMessage.Error($"队伍 {preferredTeam} 已满")); return; }

            _players[connection.Id] = new BattlePlayer
            {
                Connection = connection,
                Team = team,
                Hp = _options.PlayerHp
            };

            Reply(connection, OutgoingMessage.Of(MessageTypes.BattleJoined,
                new BattleJoinedPayload(RoomId, team, State.ToString(), PlayerInfos())));
            Broadcast(OutgoingMessage.Of(MessageTypes.BattlePlayerJoined,
                new BattlePlayerJoinedPayload(RoomId, connection.Id, team, TeamCount("A"), TeamCount("B"))),
                excludeId: connection.Id);

            TryStart(); // 锁内检查满员
        }
    }

    public void Leave(string connectionId)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(connectionId, out var player)) return;

            if (State == BattleState.Waiting)
            {
                _players.Remove(connectionId);
                Broadcast(OutgoingMessage.Of(MessageTypes.BattlePlayerLeft,
                    new BattlePlayerLeftPayload(RoomId, connectionId, player.Team)));
                return;
            }

            // 对战中离开按阵亡处理，并立即检查胜负
            if (State == BattleState.InProgress && player.Alive)
            {
                player.Hp = 0;
                _players.Remove(connectionId); // 不再向已离开/断开的连接广播
                Broadcast(OutgoingMessage.Of(MessageTypes.BattlePlayerDied,
                    new BattlePlayerDiedPayload(RoomId, connectionId, player.Team, "leave")));
                CheckWinner();
                return;
            }

            _players.Remove(connectionId);
        }
    }

    public void Action(IClientConnection connection, string action, string? targetId)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(connection.Id, out var actor)) { Reply(connection, OutgoingMessage.Error("你不在对战中")); return; }
            if (State != BattleState.InProgress) { Reply(connection, OutgoingMessage.Error("对战未开始")); return; }
            if (!actor.Alive) { Reply(connection, OutgoingMessage.Error("你已阵亡，无法行动")); return; }
            if (!string.Equals(action, "attack", StringComparison.OrdinalIgnoreCase)) { Reply(connection, OutgoingMessage.Error($"未知动作: {action}")); return; }
            if (targetId is null || !_players.TryGetValue(targetId, out var target)) { Reply(connection, OutgoingMessage.Error("目标不存在")); return; }
            if (target.Team == actor.Team) { Reply(connection, OutgoingMessage.Error("不能攻击队友")); return; }
            if (!target.Alive) { Reply(connection, OutgoingMessage.Error("目标已阵亡")); return; }

            target.Hp = Math.Max(0, target.Hp - _options.AttackDamage);
            Broadcast(OutgoingMessage.Of(MessageTypes.BattleActionEvent,
                new BattleActionEventPayload(RoomId, actor.Id, "attack", target.Id, _options.AttackDamage, target.Hp)));

            if (!target.Alive)
            {
                Broadcast(OutgoingMessage.Of(MessageTypes.BattlePlayerDied,
                    new BattlePlayerDiedPayload(RoomId, target.Id, target.Team, "killed")));
                CheckWinner();
            }
        }
    }

    /// <summary>两队均满员则开战（调用方须持有 _gate）。</summary>
    private void TryStart()
    {
        if (State != BattleState.Waiting) return;
        if (TeamCount("A") < _options.TeamSize || TeamCount("B") < _options.TeamSize) return;

        State = BattleState.InProgress;
        Broadcast(OutgoingMessage.Of(MessageTypes.BattleStarted,
            new BattleStartedPayload(RoomId, PlayerInfos())));
        _logger.LogInformation("房间 {RoomId} 对战开始（{A}v{B}）", RoomId, TeamCount("A"), TeamCount("B"));
    }

    /// <summary>团灭判定 + 结果广播 + 重置（调用方须持有 _gate）。</summary>
    private void CheckWinner()
    {
        var aAlive = _players.Values.Count(p => p is { Team: "A", Alive: true });
        var bAlive = _players.Values.Count(p => p is { Team: "B", Alive: true });
        if (aAlive > 0 && bAlive > 0) return;

        var winner = aAlive > 0 ? "A" : bAlive > 0 ? "B" : null;
        State = BattleState.Finished;
        Broadcast(OutgoingMessage.Of(MessageTypes.BattleEnded,
            new BattleEndedPayload(RoomId, winner, aAlive, bAlive)));
        _logger.LogInformation("房间 {RoomId} 对战结束，获胜方: {Winner}", RoomId, winner ?? "无（平局）");

        // 重置等待下一场；玩家需重新 battle.join
        _players.Clear();
        State = BattleState.Waiting;
    }

    private string? AssignTeam(string? preferred)
    {
        var a = TeamCount("A");
        var b = TeamCount("B");
        if (preferred is "A" or "B")
            return (preferred == "A" ? a : b) < _options.TeamSize ? preferred : null;
        if (a < _options.TeamSize && a <= b) return "A";
        if (b < _options.TeamSize) return "B";
        return null;
    }

    private int TeamCount(string team) => _players.Values.Count(p => p.Team == team);

    private List<BattlePlayerInfo> PlayerInfos() =>
        _players.Values.Select(p => new BattlePlayerInfo(p.Id, p.Team, p.Hp, p.Alive)).ToList();

    private static void Reply(IClientConnection connection, OutgoingMessage message) =>
        _ = connection.SendAsync(message); // 入队即完成，锁内安全

    private void Broadcast(OutgoingMessage message, string? excludeId = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        foreach (var player in _players.Values)
            if (player.Id != excludeId)
                player.Connection.EnqueueRaw(payload);
    }
}
