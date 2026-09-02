# FieldServer 开发记录

## 项目目标

基于 .NET 10 的可扩展游戏服务器：YAML 配置驱动、WebSocket 房间通信、玩家移动、5v5 玩家对战。
测试端使用 Python（venv + websockets 库），位于 `tests/`。
另附 3D 实时监控台 dashboard（Vite + TS + Three.js），位于 `dashboard/`。

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
IMessageHandler ◄──── 业务层：join/leave/chat/ping/move/battle_*/watch_all（★扩展点）
      │
      ├──────────────► IRoomManager / IRoom          房间：成员管理、广播
      ├──────────────► IMovementManager / IRoomMovement  移动：位置表+校验（挂载在房间上）
      ├──────────────► IBattleManager / IBattle      对战：5v5 状态机（挂载在房间上）
      └──────────────► IGlobalObserver               全局观察者：dashboard 事件总线
                        （Room/Battle 广播出口处 Fanout，只读不影响游戏逻辑）
```

目录：

```
FieldServer/
├── rooms.yaml                  # 配置入口：房间数/容量/对战参数
├── Configuration/              # FieldServerOptions（含 BattleOptions）
├── Connections/                # IClientConnection / WebSocketClientConnection
│                               #   每连接独立 Channel 发送队列，慢消费者自动断开
├── Endpoints/                  # HTTP 端点统一管理（DebugEndpoints：服务信息/房间/对战/移动状态；
│                               #   WeatherEndpoints：示例 API；新增 = MapXxxEndpoints + Program.cs 调一行）
├── Messaging/                  # 消息契约 / 分发器 / 处理器
│   └── Handlers/               #   join/leave/chat/ping/move + battle_join/leave/action
├── Rooms/                      # 房间领域层（ConcurrentDictionary，无全局锁）
├── Movement/                   # 移动领域层（位置表+边界/速度校验，挂载在房间上）
├── Battle/                     # 对战领域层（状态机，独立于房间可挂载）
└── Services/                   # WebSocket 会话层
tests/
├── ws_bench.py                 # 128 房间通信压测/验证
├── movement_test.py            # 移动功能验证
├── watch_test.py               # 全局观察者（dashboard 通道）验证
└── battle_test.py              # 5v5 对战功能验证
dashboard/                      # 3D 实时监控台（Vite + TS + Three.js）
├── vite.config.ts              #   /ws 与调试端点代理到 127.0.0.1:5000
└── src/                        #   net（观察者WS）/ state（事件应用）/ scene（3D）/ hud（面板）
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
   该房对战并清理位置；对战中退出按阵亡处理并广播 `battle_player_died(reason=leave)`。
8. **移动与房间组合**：与 Battle 同一模式，`MovementManager.GetOrCreateMovement(roomId)`
   懒创建，房间层零修改。入房在场地中心生成出生点（`joined` 载荷携带 x/y）。
9. **服务器权威移动**：边界钳制（越界收敛到场地边缘）+ 速度上限校验
   （距离 > MoveSpeed × 间隔 × 容差视为瞬移，拒绝且位置不变）。
   出生后首次移动不限速（视为进场定位）。moved 广播含发送者，作为权威位置回执。
10. **全局观察者零侵入**：广播只有一个出口（Room.Broadcast / Battle.Broadcast），
    在该处把已序列化载荷 Fanout 给观察者连接——复用同一份字节，无二次序列化。
    观察者不入房、只收不发；断连时随会话清理注销。这是"全服观战"，
    也为阶段 3（单房观战）铺好了路。

## 消息协议

客户端 → 服务器：`{ "type": "...", "data": { ... } }`

| type | data | 说明 |
|---|---|---|
| `join` | `{roomId}` | 加入房间（自动离开旧房间） |
| `leave` | `{}` | 离开当前房间 |
| `chat` | `{content}` | 房间广播（含发送者回执） |
| `ping` | `{timestamp}` | 延迟测量 |
| `move` | `{x, y}` | 移动（边界钳制 + 速度校验，x/y 必填且须为数字） |
| `battle_join` | `{team?}` | 加入当前房间对战（team 可省略=自动分配） |
| `battle_leave` | `{}` | 退出对战（不退出房间） |
| `battle_action` | `{action:"attack", targetId}` | 战斗动作 |
| `watch_all` | `{}` | 注册为全局观察者（dashboard），接收全服房间/对战广播 |

服务器 → 客户端：`joined`（含出生坐标 x/y）`left` `chat` `pong` `system` `error`
`watching`（观察者回执，含房间数/观察者数）+
移动事件 `moved`（房间广播，含发送者作为权威回执）+
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

## 移动规则（rooms.yaml 可配）

```yaml
FieldServer:
  Movement:
    FieldWidth: 100      # 场地 [0,100] × [0,100]
    FieldHeight: 100
    MoveSpeed: 10        # 速度上限（单位/秒）
    SpeedTolerance: 1.5  # 校验容差倍率（容忍客户端 tick 抖动）
```

- 出生点 = 场地中心；出生后首次移动不限速（进场定位）。
- 越界坐标钳制到场地边缘；超速移动拒绝（位置不变）并回报 error。

## 踩坑记录

**并发释放竞态（已修复）**：A 断连清理时向 B 广播，B 恰好也在释放，
`EnqueueRaw` 中 `_closed.Cancel()` 抛出 `ObjectDisposedException` 导致 Kestrel
连接级未处理异常。修复：`Cancel()` 加容错；同时对战中离开的玩家立即移出
成员表，不再向已断开连接广播（顺带消除"队列溢出"噪音告警）。

**JsonElement.TryGetDouble 陷阱（已修复）**：非 Number 元素调用 `TryGetDouble`
抛 `InvalidOperationException`（而非返回 false），异常穿透 Handler 直接杀死会话。
修复：先判 `ValueKind == JsonValueKind.Number`；同时在会话层给 DispatchAsync
加了兜底 catch——单条消息的处理器异常只回 error 并记日志，不再断开连接。

**"{}" 不等于 (0,0)**：STJ 反序列化 `MovePayload(double X, double Y)` 时缺字段
静默取默认值 0，`{}` 会变成合法移动到原点。修复：move 改为直接从 JsonElement
逐字段校验，缺字段/非数字/非有限值一律拒绝。

## 验证结果（Apple M1 Max / .NET 10.0.400 / Python 3.12）

### 全局观察者（tests/watch_test.py）—— 全部 PASS

| 测试 | 内容 | 结果 |
|---|---|---|
| 注册 | watch_all → watching 回执（房间数/观察者数） | 正确 |
| 多房间事件 | 不入房收到房间50/51的 moved/chat | 正确 |
| 隔离性 | 非观察者收不到他房间事件 | 正确 |
| 对战事件 | battle_started/action/ended 全收到 | 正确 |
| 断连 | 观察者断开不影响房间通信 | 正确 |

dashboard 端到端（经 vite 代理）：moved/chat/battle_* 全类型事件流验证通过。

### 移动（tests/movement_test.py）—— 全部 PASS

| 测试 | 内容 | 结果 |
|---|---|---|
| 错误处理 | 未入房移动 / 非法参数（缺字段、非数字） | 正确报错且不断连 |
| 出生点 | joined 携带场地中心坐标 (50, 50) | 正确 |
| 基本移动 | move → 本人收 moved 回执 + 房间广播 | 正确 |
| 边界钳制 | (9999, -5) → 钳制到 (100, 0) | 正确 |
| 速度校验 | 合法速度通过；瞬移拒绝且位置不变；冷却后可继续 | 正确 |
| 清理 | 退房/断连后位置表清空 | 正确 |

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
venv/bin/python tests/movement_test.py                                       # 移动功能回归
venv/bin/python tests/watch_test.py                                          # 观察者通道回归
venv/bin/python tests/battle_test.py                                         # 对战功能回归

# dashboard 实时监控台（需先启动 FieldServer）
cd dashboard && npm install && npm run dev                                   # 打开 http://localhost:5173
venv/bin/python tests/sim_activity.py                                        # 可选：制造演示活动（房间3/7移动+房间9循环5v5）
```

HTTP 调试端点：`GET /rooms`（房间状态）、`GET /battles/{roomId}`（对战状态）、
`GET /movement/{roomId}`（房间内玩家位置）。

## 后续计划

见 [roadmap.md](roadmap.md)：阶段 1 攻击距离判定 → 阶段 2 AOI 区域广播 →
阶段 3 观战模式 → 阶段 4 回收与动态开房 → 阶段 5 匹配与认证。
