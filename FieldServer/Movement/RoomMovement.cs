using FieldServer.Configuration;
using FieldServer.Messaging;
using FieldServer.Rooms;

namespace FieldServer.Movement;

/// <summary>
/// 单房间移动状态实现。所有状态修改在 _gate 锁内完成；
/// 广播为 Channel 入队操作（非阻塞），锁内安全（与 Battle 同一约定）。
/// </summary>
public sealed class RoomMovement : IRoomMovement
{
    private sealed class Tracked
    {
        public double X;
        public double Y;
        public DateTimeOffset UpdatedAt;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _players = new();
    private readonly MovementOptions _options;
    private readonly IRoom? _room;

    public RoomMovement(int roomId, MovementOptions options, IRoom? room)
    {
        RoomId = roomId;
        _options = options;
        _room = room;
    }

    public int RoomId { get; }

    public int PlayerCount
    {
        get { lock (_gate) return _players.Count; }
    }

    /// <summary>出生点 = 场地中心。</summary>
    public PlayerPosition SpawnPoint => new(_options.FieldWidth / 2, _options.FieldHeight / 2);

    public PlayerPosition Spawn(string connectionId)
    {
        var spawn = SpawnPoint;
        lock (_gate)
        {
            // UpdatedAt 置为 MinValue：出生后的首次移动不限速（视为进场定位），
            // 首次移动被接受后才进入正常速度校验。
            _players[connectionId] = new Tracked { X = spawn.X, Y = spawn.Y, UpdatedAt = DateTimeOffset.MinValue };
        }
        return spawn;
    }

    public bool Remove(string connectionId)
    {
        lock (_gate) return _players.Remove(connectionId);
    }

    public bool TryGet(string connectionId, out PlayerPosition position)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(connectionId, out var tracked))
            {
                position = new PlayerPosition(tracked.X, tracked.Y);
                return true;
            }
            position = new PlayerPosition(0, 0);
            return false;
        }
    }

    public MoveResult Move(string connectionId, double x, double y, out PlayerPosition position)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(connectionId, out var tracked))
            {
                position = new PlayerPosition(0, 0);
                return MoveResult.NotTracked;
            }

            // 边界钳制：越界坐标收敛到场地边缘
            x = Math.Clamp(x, 0, _options.FieldWidth);
            y = Math.Clamp(y, 0, _options.FieldHeight);

            // 速度上限：距离超过 MoveSpeed × 间隔 × 容差视为瞬移，忽略本次移动
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - tracked.UpdatedAt;
            if (elapsed > TimeSpan.Zero)
            {
                var allowed = _options.MoveSpeed * elapsed.TotalSeconds * _options.SpeedTolerance;
                var dx = x - tracked.X;
                var dy = y - tracked.Y;
                if (dx * dx + dy * dy > allowed * allowed)
                {
                    position = new PlayerPosition(tracked.X, tracked.Y);
                    return MoveResult.TooFast;
                }
            }

            tracked.X = x;
            tracked.Y = y;
            tracked.UpdatedAt = now;
            position = new PlayerPosition(x, y);

            // 广播给房间全部成员（含发送者，作为权威位置回执）
            _room?.Broadcast(OutgoingMessage.Of(MessageTypes.Moved,
                new MovedPayload(RoomId, connectionId, x, y, now.ToUnixTimeMilliseconds())));
            return MoveResult.Ok;
        }
    }

    public IReadOnlyList<PlayerPositionInfo> Snapshot()
    {
        lock (_gate)
            return _players.Select(p => new PlayerPositionInfo(p.Key, p.Value.X, p.Value.Y)).ToList();
    }
}
