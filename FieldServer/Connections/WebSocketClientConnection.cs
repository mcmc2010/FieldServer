using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using FieldServer.Configuration;
using FieldServer.Messaging;

namespace FieldServer.Connections;

/// <summary>
/// 基于 WebSocket 的连接实现：
/// 独立发送循环（Channel 有界队列）保证同一时刻只有一个 SendAsync 写 socket；
/// 队列持续满说明是慢消费者，主动断开以保护服务器。
/// </summary>
public sealed class WebSocketClientConnection : IClientConnection, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly WebSocket _socket;
    private readonly Channel<byte[]> _sendQueue;
    private readonly Task _sendLoop;
    private readonly CancellationTokenSource _closed = new();
    private readonly ILogger _logger;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public int? CurrentRoomId { get; set; }

    public WebSocketClientConnection(WebSocket socket, FieldServerOptions options, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait // 不阻塞写入方：EnqueueRaw 用 TryWrite，满则断开
        });
        _sendLoop = Task.Run(SendLoopAsync);
    }

    public ValueTask SendAsync(OutgoingMessage message)
    {
        EnqueueRaw(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions));
        return ValueTask.CompletedTask;
    }

    public bool EnqueueRaw(byte[] payload)
    {
        if (_sendQueue.Writer.TryWrite(payload)) return true;

        // 队列满（慢消费者）或通道已关闭。连接可能正在被释放，Cancel 需容错。
        try
        {
            if (!_closed.IsCancellationRequested)
            {
                _logger.LogWarning("连接 {Id} 发送队列溢出（慢消费者），断开", Id);
                _closed.Cancel();
            }
        }
        catch (ObjectDisposedException) { /* 连接已释放 */ }
        return false;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var payload in _sendQueue.Reader.ReadAllAsync(_closed.Token))
            {
                if (_socket.State != WebSocketState.Open) break;
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _closed.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "连接 {Id} 发送循环异常", Id);
        }
    }

    public async Task CloseAsync(string reason)
    {
        _sendQueue.Writer.TryComplete();
        if (!_closed.IsCancellationRequested) await _closed.CancelAsync();
        try { await _sendLoop; } catch { /* 已处理 */ }
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
            catch { /* 对端可能已断开 */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed");
        _closed.Dispose();
    }
}
