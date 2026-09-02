# FieldServer 开发路线图

> 当前状态：通信层 + 房间 + 移动 + 5v5 对战 + 全局观察者通道（3D 监控台 dashboard）
> 已完成并全部验证通过（见 dev-log.md）。
> 以下按优先级分阶段，每个阶段都可独立交付、独立验证，不破坏既有测试。

## 阶段 1：移动与对战联动（攻击距离判定）★ 推荐下一步

**价值**：让"移动"真正影响"对战"，从演示走向玩法。

- `Battle` 注入 `IMovementManager`，攻击时校验攻击者与目标的距离：
  - `BattleOptions` 增加 `AttackRange`（如 15 单位）
  - `battle_action attack` 超出距离 → 报错"目标超出攻击范围"
  - 目标位置不存在（未入房/已断连）→ 按现有"目标不存在"处理
- 双方都在同一房间的移动状态表中，查询是内存操作，零额外成本
- 测试：`battle_test.py` 增加距离用例——拉开距离攻击报错、靠近后攻击成功、
  追打/走位的完整对局（进攻方需边移动边攻击）

**改动面**：`Battle.cs`（构造+Action）、`BattleManager.cs`（注入）、
`FieldServerOptions.cs`（+AttackRange）、`rooms.yaml`、`battle_test.py`

## 阶段 2：AOI 区域广播（按距离圈定广播目标）

**价值**：大房间/高人数下降低带宽，是 MMO 式服务器的基础能力。

- `Room.Broadcast` 增加按位置过滤的重载：广播时查询 `IRoomMovement`，
  只投递给半径 N 内的成员（moved 事件优先应用）
- 配置：`Movement.AoiRadius`（0 = 关闭，全房间广播，保持现有行为）
- 注意点：位置表与成员表是两个并发结构，遍历时容忍快照不一致
  （宁可多发，不可漏发：查不到位置的成员默认投递）
- 测试：两个玩家分别在对角，互相收不到 moved；靠近后能收到；
  chat 保持全房间可达（chat 不走 AOI）

## 阶段 3：观战模式（已完成全服版基础）

**价值**：Battle 扩展点已预留（旁观者列表），验证架构扩展性。

- ~~全服观察者~~ ✅ 已完成：`watch_all` + `IGlobalObserver`，dashboard 已接入
- 剩余单房观战：`battle_watch` / `battle_unwatch` 消息，旁观者只收该房对战事件
- `Battle` 增加 `_watchers` 列表：广播目标 = 玩家 + 旁观者；
  `battle_action` 等操作仅玩家可用
- 对战中玩家阵亡后自动转为旁观者（现状是阵亡者留在玩家表，可复用）
- 测试：旁观者收到 `battle_started/ended` 全部事件、操作被拒、中途加入旁观

## 阶段 4：资源回收与动态开房

**价值**：长跑服务器的必要能力，目前三个 Manager 都只增不减。

- `BattleManager` / `MovementManager`：空闲实例定时回收
  （空表 + 超过 N 分钟无活动 → 移除）
- `RoomManager` 扩展动态开房：`POST /rooms` 创建房间（受上限保护）
- 后台服务用 `BackgroundService` 周期扫描，接口已隔离无需改调用方
- 测试：打一场对战后等待回收，`/battles/{roomId}` 回到 404；
  动态开房后可正常加入/移动/对战

## 阶段 5：匹配与认证（接入真实客户端前的最后一块）

- 认证：连接首条消息 `auth {token}` → Handler 注入验证服务，
  未认证连接仅允许 auth/ping；connectionId 换成账号 ID
- 匹配：`matchmake {mode:"5v5"}` → 匹配服务凑满 10 人自动拉进房间并 `battle_join`
- 持久化：战绩/段位落库（Handler 构造函数注入仓储，架构已支持）

## 排期建议

| 阶段 | 内容 | 预估工作量 | 依赖 |
|---|---|---|---|
| 1 | 攻击距离判定 | 0.5 天 | 无 |
| 2 | AOI 区域广播 | 1 天 | 无（建议阶段 1 之后，先让移动有意义） |
| 3 | 观战模式 | 1 天 | 无 |
| 4 | 回收与动态开房 | 1 天 | 无 |
| 5 | 匹配与认证 | 2-3 天 | 依赖外部账号/存储决策 |

原则：每阶段完成 = 新测试 PASS + 既有三套回归（movement/battle/ws_bench）全绿 + dev-log 更新。
