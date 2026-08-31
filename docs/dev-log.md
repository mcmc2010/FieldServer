# FieldServer 开发记录

## 项目目标

基于 .NET 10 的可扩展游戏服务器：YAML 配置驱动、WebSocket 房间通信、5v5 玩家对战。
测试端使用 Python（venv + websockets 库），位于 `tests/`。

## 架构总览

```
rooms.yaml ──► FieldServerOptions (IOptions)
                      │
Program.cs (组合根/DI) │
                      ▼
WebSocketSession      协议层：WS 帧读取、JSON 解析、断连清理
      │
      ▼
MessageDispatcher     路由层：按消息 type 分发
      │
      ▼
IMessageHandler ◄──── 业务层：join/leave/chat/ping/battle_*（★扩展点）
      │
      ├──────────────► IRoomManager / IRoom      房间：成员管理、广播
      └──────────────► IBattleManager / IBattle  对战：5v5 状态机（挂载在房间上）
```

目录：

```
FieldServer/
├── rooms.yaml                  # 配置入口：房间数/容量/对战参数
├── Configuration/              # FieldServerOptions（含 BattleOptions）
├── Connections/                # IClientConnection / WebSocketClientConnection
│                               #   每连接独立 Channel 发送队列，慢消费者自动断开
├── Endpoints/                  # HTTP API（weatherforecast；新端点在此扩展）
├── Messaging/                  # 消息契约 / 分发器 / 处理器
│   └── Handlers/               #   join/leave/chat/ping + battle_join/leave/action
├── Rooms/                      # 房间领域层（ConcurrentDictionary，无全局锁）
├── Battle/                     # 对战领域层（状态机，独立于房间可挂载）
└── Services/                   # WebSocket 会话层
tests/
├── ws_bench.py                 # 128 房间通信压测/验证
└── battle_test.py              # 5v5 对战功能验证
```

## 关键设计决策

1. **扩展点 = IMessageHandler**：新功能 = 新建 Handler 类 + Program.cs 注册一行 DI。
   无需改动分发器、会话层等既有代码。
2. **每连接有界发送队列（Channel）**：广播只入队不阻塞；队列持续满 = 慢消费者，
   主动断开以保护服务器与房间其他成员。
3. **广播只序列化一次**：`EnqueueRaw(byte[])` 共享载荷。
4. **Battle 与 Room 组合而非侵入**：房间是通信容器，对战按需挂载
   （`BattleManager.GetOrCreateBattle(roomId)`），房间层零修改即获得对战能力。
5. **对战状态机**：`Waiting`（组队）→ 满员自动 `InProgress` → 团灭 `Finished`
   → 广播结果后立即重置回 `Waiting`，可无缝开新一场。
6. **线程安全**：Battle 内所有状态修改在 `_gate` 锁内完成；因发送均为入队操作
   （非阻塞），锁内广播安全。房间用 ConcurrentDictionary + Interlocked 计数。
7. **断连/退房联动**：`RoomHelper.LeaveRoom` 统一处理——离开房间时自动退出
   该房对战；对战中退出按阵亡处理并广播 `battle_player_died(reason=leave)`。

## 消息协议

客户端 → 服务器：`{ "type": "...", "data": { ... } }`

| type | data | 说明 |
|---|---|---|
| `join` | `{roomId}` | 加入房间（自动离开旧房间） |
| `leave` | `{}` | 离开当前房间 |
| `chat` | `{content}` | 房间广播（含发送者回执） |
| `ping` | `{timestamp}` | 延迟测量 |
| `battle_join` | `{team?}` | 加入当前房间对战（team 可省略=自动分配） |
| `battle_leave` | `{}` | 退出对战（不退出房间） |
| `battle_action` | `{action:"attack", targetId}` | 战斗动作 |

服务器 → 客户端：`joined` `left` `chat` `pong` `system` `error` +
对战事件 `battle_joined` `battle_player_joined` `battle_player_left`
`battle_started` `battle_action_event` `battle_player_died` `battle_ended`。

## 对战规则（rooms.yaml 可配）

```yaml
FieldServer:
  Battle:
    TeamSize: 5        # 每队人数（5 即 5v5，改 3 即 3v3）
    PlayerHp: 100      # 初始血量
    AttackDamage: 20   # 单次攻击伤害
```

- 两队均满员自动开战；全队阵亡判负；对战中断连/退赛按阵亡处理。
- 约束：不能攻击队友/阵亡者/未开战时攻击；未加入房间不能参战。

## 踩坑记录

**并发释放竞态（已修复）**：A 断连清理时向 B 广播，B 恰好也在释放，
`EnqueueRaw` 中 `_closed.Cancel()` 抛出 `ObjectDisposedException` 导致 Kestrel
连接级未处理异常。修复：`Cancel()` 加容错；同时对战中离开的玩家立即移出
成员表，不再向已断开连接广播（顺带消除"队列溢出"噪音告警）。

## 验证结果（Apple M1 Max / .NET 10.0.400 / Python 3.12）

### 5v5 对战（tests/battle_test.py）—— 全部 PASS

| 测试 | 内容 | 结果 |
|---|---|---|
| 错误处理 | 未入房参战 / 未开战攻击 / 攻击队友 | 正确报错 |
| 完整对局 | 5v5 自动开战→攻击→团灭→A 胜→自动重置→可再开 | 全链路正确 |
| 断连处理 | 对战中断连广播 `reason=leave`，B 队获胜 | 正确 |
| 并行对战 | 16 房间 × 5v5 同时打完 | 全部正常 |

### 128 房间通信（tests/ws_bench.py）—— PASS

1024 连接，819,200 条投递 0 丢失，吞吐 ~170k msg/s（Python 客户端侧瓶颈），
断连后服务器成员自动清零，服务器日志 0 异常。

## 常用命令

```bash
dotnet build FieldServer -c Release                                          # 构建
dotnet FieldServer/bin/Release/net10.0/FieldServer.dll --urls http://127.0.0.1:5000  # 运行
venv/bin/python tests/ws_bench.py                                            # 房间通信回归
venv/bin/python tests/battle_test.py                                         # 对战功能回归
```

HTTP 调试端点：`GET /rooms`（房间状态）、`GET /battles/{roomId}`（对战状态）。

## 后续可扩展方向

- 新消息类型（私聊/观战/游戏指令）→ 新 Handler + 一行 DI
- 动态开房/空闲对战回收 → 扩展 RoomManager / BattleManager（接口已隔离）
- 认证/持久化/匹配 → Handler 构造函数注入服务
- 观战模式 → Battle 增加旁观者列表（广播目标 + 权限区分）
