using System.Collections.Concurrent;

namespace FieldServer.Connections;

/// <summary>
/// 全局观察者：订阅全服房间广播事件的特殊连接（dashboard 监控端）。
/// 观察者在任何房间广播出口处收到同样的载荷，只读、不影响游戏逻辑。
/// </summary>
public interface IGlobalObserver
{
    int WatcherCount { get; }

    void Add(IClientConnection connection);
    void Remove(string connectionId);

    /// <summary>把房间/对战广播的已序列化载荷投递给全部观察者。</summary>
    void Fanout(byte[] payload);
}

/// <summary>观察者注册表。发送均为 Channel 入队（非阻塞），与成员广播同一约定。</summary>
public sealed class GlobalObserver : IGlobalObserver
{
    private readonly ConcurrentDictionary<string, IClientConnection> _watchers = new();

    public int WatcherCount => _watchers.Count;

    public void Add(IClientConnection connection) => _watchers[connection.Id] = connection;

    public void Remove(string connectionId) => _watchers.TryRemove(connectionId, out _);

    public void Fanout(byte[] payload)
    {
        foreach (var (_, watcher) in _watchers)
            watcher.EnqueueRaw(payload);
    }
}
