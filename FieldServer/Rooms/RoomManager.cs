using Microsoft.Extensions.Options;
using FieldServer.Configuration;
using FieldServer.Connections;

namespace FieldServer.Rooms;

/// <summary>
/// 按 YAML 配置在启动时创建全部房间。
/// 未来可扩展为动态创建/销毁房间（如按需开房、空闲回收）。
/// </summary>
public sealed class RoomManager : IRoomManager
{
    private readonly IReadOnlyDictionary<int, IRoom> _byId;

    public RoomManager(IOptions<FieldServerOptions> options, IGlobalObserver observer, ILogger<RoomManager> logger)
    {
        var config = options.Value;
        if (config.RoomCount is <= 0 or > 100_000)
            throw new InvalidOperationException($"RoomCount 配置非法: {config.RoomCount}");

        var rooms = new List<IRoom>(config.RoomCount);
        var byId = new Dictionary<int, IRoom>(config.RoomCount);
        for (var i = 0; i < config.RoomCount; i++)
        {
            var room = new Room(i, config.MaxConnectionsPerRoom, observer);
            rooms.Add(room);
            byId[i] = room;
        }

        Rooms = rooms;
        _byId = byId;
        logger.LogInformation("已按 YAML 配置创建 {Count} 个房间（每房间上限 {Max} 人）",
            config.RoomCount, config.MaxConnectionsPerRoom);
    }

    public int RoomCount => Rooms.Count;
    public IReadOnlyList<IRoom> Rooms { get; }
    public IRoom? GetRoom(int id) => _byId.TryGetValue(id, out var room) ? room : null;
}
