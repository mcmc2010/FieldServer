namespace FieldServer.Messaging;

/// <summary>按消息 type 路由到对应 <see cref="IMessageHandler"/>。</summary>
public sealed class MessageDispatcher
{
    private readonly IReadOnlyDictionary<string, IMessageHandler> _handlers;

    public MessageDispatcher(IEnumerable<IMessageHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.MessageType, StringComparer.OrdinalIgnoreCase);
    }

    public async Task DispatchAsync(MessageContext context, IncomingMessage message)
    {
        if (_handlers.TryGetValue(message.Type, out var handler))
        {
            await handler.HandleAsync(context, message.Data);
            return;
        }
        await context.Connection.SendAsync(OutgoingMessage.Error($"未知消息类型: {message.Type}"));
    }
}
