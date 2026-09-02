using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using FieldServer.Configuration;
using FieldServer.Connections;

namespace FieldServer.Battle;

/// <summary>按房间懒创建对战实例。未来可扩展空闲对战回收。</summary>
public sealed class BattleManager : IBattleManager
{
    private readonly ConcurrentDictionary<int, IBattle> _battles = new();
    private readonly BattleOptions _options;
    private readonly IGlobalObserver _observer;
    private readonly ILoggerFactory _loggerFactory;

    public BattleManager(IOptions<FieldServerOptions> options, IGlobalObserver observer, ILoggerFactory loggerFactory)
    {
        _options = options.Value.Battle;
        if (_options.TeamSize < 1)
            throw new InvalidOperationException($"Battle.TeamSize 配置非法: {_options.TeamSize}");
        _observer = observer;
        _loggerFactory = loggerFactory;
    }

    public IBattle GetOrCreateBattle(int roomId) =>
        _battles.GetOrAdd(roomId, id => new Battle(id, _options, _observer, _loggerFactory.CreateLogger<Battle>()));

    public IBattle? GetBattle(int roomId) =>
        _battles.TryGetValue(roomId, out var battle) ? battle : null;
}
