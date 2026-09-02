using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using FieldServer.Configuration;
using FieldServer.Rooms;

namespace FieldServer.Movement;

/// <summary>按房间懒创建移动状态实例。未来可扩展空闲回收。</summary>
public sealed class MovementManager : IMovementManager
{
    private readonly ConcurrentDictionary<int, IRoomMovement> _movements = new();
    private readonly MovementOptions _options;
    private readonly IRoomManager _rooms;

    public MovementManager(IOptions<FieldServerOptions> options, IRoomManager rooms)
    {
        _options = options.Value.Movement;
        if (_options.FieldWidth <= 0 || _options.FieldHeight <= 0 || _options.MoveSpeed <= 0)
            throw new InvalidOperationException(
                $"Movement 配置非法: {_options.FieldWidth}x{_options.FieldHeight}, speed={_options.MoveSpeed}");
        _rooms = rooms;
    }

    public IRoomMovement GetOrCreateMovement(int roomId) =>
        _movements.GetOrAdd(roomId, id => new RoomMovement(id, _options, _rooms.GetRoom(id)));

    public IRoomMovement? GetMovement(int roomId) =>
        _movements.TryGetValue(roomId, out var movement) ? movement : null;
}
