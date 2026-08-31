using System.Collections.Concurrent;
using System.Text.Json;
using FieldServer.Connections;
using FieldServer.Messaging;

namespace FieldServer.Rooms;

/// <summary>
/// 房间实现：成员用 ConcurrentDictionary，计数用 Interlocked，
/// 每个房间独立，无全局锁，128 个房间可完全并行。
/// </summary>
public sealed class Room : IRoom
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly ConcurrentDictionary<string, IClientConnection> _members = new();
    private readonly int _maxMembers;
    private int _memberCount;

    public Room(int id, int maxMembers)
    {
        Id = id;
        Name = $"room-{id}";
        _maxMembers = maxMembers;
    }

    public int Id { get; }
    public string Name { get; }
    public int MemberCount => _memberCount;

    public bool TryJoin(IClientConnection connection)
    {
        while (true)
        {
            var current = _memberCount;
            if (current >= _maxMembers) return false;
            if (Interlocked.CompareExchange(ref _memberCount, current + 1, current) != current)
                continue; // 计数竞争，重试

            if (_members.TryAdd(connection.Id, connection)) return true;

            Interlocked.Decrement(ref _memberCount); // 已在房间，回滚计数
            return false;
        }
    }

    public bool Leave(string connectionId)
    {
        if (!_members.TryRemove(connectionId, out _)) return false;
        Interlocked.Decrement(ref _memberCount);
        return true;
    }

    public void Broadcast(OutgoingMessage message, string? excludeConnectionId = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        foreach (var (id, member) in _members)
        {
            if (id == excludeConnectionId) continue;
            member.EnqueueRaw(payload);
        }
    }
}
