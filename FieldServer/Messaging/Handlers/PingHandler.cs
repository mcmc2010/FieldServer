using System.Text.Json;

namespace FieldServer.Messaging.Handlers;

/// <summary>处理 ping：回 pong，用于延迟测量。</summary>
public sealed class PingHandler : IMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public string MessageType => MessageTypes.Ping;

    public async Task HandleAsync(MessageContext context, JsonElement data)
    {
        long timestamp = 0;
        try { timestamp = data.Deserialize<PingPayload>(JsonOptions)?.Timestamp ?? 0; }
        catch (JsonException) { /* 用默认值 */ }

        await context.Connection.SendAsync(OutgoingMessage.Of(MessageTypes.Pong,
            new PongPayload(timestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
    }
}
