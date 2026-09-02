using Microsoft.AspNetCore.Mvc;
using FieldServer.Battle;
using FieldServer.Configuration;
using FieldServer.Connections;
using FieldServer.Endpoints;
using FieldServer.Messaging;
using FieldServer.Messaging.Handlers;
using FieldServer.Movement;
using FieldServer.Rooms;
using FieldServer.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- 配置：YAML 驱动（房间数等在 rooms.yaml 中调整）----
var yamlPath = Path.Combine(AppContext.BaseDirectory, "rooms.yaml");
builder.Configuration.AddYamlFile(yamlPath, optional: false, reloadOnChange: false);
builder.Services.Configure<FieldServerOptions>(
    builder.Configuration.GetSection(FieldServerOptions.SectionName));

// ---- 核心服务 ----
builder.Services.AddSingleton<IGlobalObserver, GlobalObserver>();
builder.Services.AddSingleton<IRoomManager, RoomManager>();
builder.Services.AddSingleton<IBattleManager, BattleManager>();
builder.Services.AddSingleton<IMovementManager, MovementManager>();
builder.Services.AddSingleton<MessageDispatcher>();
builder.Services.AddTransient<WebSocketSession>();

// ---- 消息处理器（★ 扩展点：新增功能 = 新建 IMessageHandler 实现 + 注册一行）----
builder.Services.AddSingleton<IMessageHandler, JoinRoomHandler>();
builder.Services.AddSingleton<IMessageHandler, LeaveRoomHandler>();
builder.Services.AddSingleton<IMessageHandler, ChatHandler>();
builder.Services.AddSingleton<IMessageHandler, PingHandler>();
builder.Services.AddSingleton<IMessageHandler, MoveHandler>();
builder.Services.AddSingleton<IMessageHandler, BattleJoinHandler>();
builder.Services.AddSingleton<IMessageHandler, BattleLeaveHandler>();
builder.Services.AddSingleton<IMessageHandler, BattleActionHandler>();
builder.Services.AddSingleton<IMessageHandler, WatchAllHandler>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// HTTP 端点（全部在 Endpoints/ 下统一管理；新增端点 = 新建 MapXxxEndpoints + 调用一行）
app.MapDebugEndpoints();
app.MapWeatherEndpoints();

// WebSocket 入口
app.Map("/ws", async (HttpContext context, [FromServices] WebSocketSession session) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("需要 WebSocket 连接");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await session.RunAsync(socket, context.RequestAborted);
});

app.Run();
