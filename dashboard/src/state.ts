import type {
  BattleActionEventPayload,
  BattleEndedPayload,
  BattlePlayerDiedPayload,
  BattlePlayerJoinedPayload,
  BattlePlayerLeftPayload,
  BattleStartedPayload,
  ChatEventPayload,
  Envelope,
  MovedPayload,
  SystemPayload,
} from "./types";

export interface PlayerInfo {
  id: string;
  roomId: number;
  x: number;
  y: number;
  team: "A" | "B" | null;
  hp: number;
  alive: boolean;
  lastSeen: number; // performance.now() 毫秒
}

export interface RoomInfo {
  id: number;
  members: number;
  battling: boolean;
}

export interface LogEntry {
  cls: string;
  text: string;
}

export interface AppliedFx {
  attack?: { roomId: number; from: { x: number; y: number }; to: { x: number; y: number } };
}

const PRUNE_MS = 60_000; // 60s 无任何行为的玩家视为不活跃，从视图移除
const SPAWN = { x: 50, y: 50 };

export class GameState {
  readonly players = new Map<string, PlayerInfo>();
  readonly rooms = new Map<number, RoomInfo>();
  totalMembers = 0;
  roomCount = 0;

  roomInfo(id: number): RoomInfo {
    let r = this.rooms.get(id);
    if (!r) {
      r = { id, members: 0, battling: false };
      this.rooms.set(id, r);
    }
    return r;
  }

  private player(roomId: number, id: string): PlayerInfo {
    let p = this.players.get(id);
    if (!p) {
      p = { id, roomId, x: SPAWN.x, y: SPAWN.y, team: null, hp: 100, alive: true, lastSeen: 0 };
      this.players.set(id, p);
    }
    p.lastSeen = performance.now();
    return p;
  }

  /** 应用一条服务器消息，返回需要上屏的日志与特效。 */
  apply(msg: Envelope): { log?: LogEntry; fx?: AppliedFx } {
    switch (msg.type) {
      case "moved": {
        const d = msg.data as MovedPayload;
        const p = this.player(d.roomId, d.connectionId);
        p.roomId = d.roomId;
        p.x = d.x;
        p.y = d.y;
        return {};
      }
      case "chat": {
        const d = msg.data as ChatEventPayload;
        this.player(d.roomId, d.from);
        return { log: { cls: "", text: `[房${d.roomId}] ${d.from}: ${d.content}` } };
      }
      case "system":
        return { log: { cls: "sys", text: (msg.data as SystemPayload).content } };

      case "battle_player_joined": {
        const d = msg.data as BattlePlayerJoinedPayload;
        this.player(d.roomId, d.playerId).team = d.team as "A" | "B";
        return { log: { cls: "sys", text: `[房${d.roomId}] ${d.playerId} 加入 ${d.team} 队（A:${d.teamACount} B:${d.teamBCount}）` } };
      }
      case "battle_player_left": {
        const d = msg.data as BattlePlayerLeftPayload;
        const p = this.player(d.roomId, d.playerId);
        p.team = null;
        return { log: { cls: "sys", text: `[房${d.roomId}] ${d.playerId} 退出对战` } };
      }
      case "battle_started": {
        const d = msg.data as BattleStartedPayload;
        this.roomInfo(d.roomId).battling = true;
        for (const info of d.players) {
          const p = this.player(d.roomId, info.id);
          p.team = info.team as "A" | "B";
          p.hp = info.hp;
          p.alive = info.alive;
        }
        return { log: { cls: "end", text: `[房${d.roomId}] ⚔ 对战开始（${d.players.length} 人）` } };
      }
      case "battle_action_event": {
        const d = msg.data as BattleActionEventPayload;
        const actor = this.player(d.roomId, d.actorId);
        const target = this.player(d.roomId, d.targetId);
        target.hp = d.targetHp;
        return {
          log: { cls: "atk", text: `[房${d.roomId}] ${d.actorId} → ${d.targetId} (-${d.damage}, 剩 ${d.targetHp})` },
          fx: { attack: { roomId: d.roomId, from: { x: actor.x, y: actor.y }, to: { x: target.x, y: target.y } } },
        };
      }
      case "battle_player_died": {
        const d = msg.data as BattlePlayerDiedPayload;
        const p = this.player(d.roomId, d.playerId);
        p.hp = 0;
        p.alive = false;
        const reason = d.reason === "leave" ? "离场" : "阵亡";
        return { log: { cls: "die", text: `[房${d.roomId}] ✝ ${d.playerId}（${d.team} 队）${reason}` } };
      }
      case "battle_ended": {
        const d = msg.data as BattleEndedPayload;
        this.roomInfo(d.roomId).battling = false;
        // 对战重置：清掉参战标记，下一场需重新 battle_join
        for (const p of this.players.values()) {
          if (p.roomId === d.roomId) {
            p.team = null;
            p.hp = 100;
            p.alive = true;
          }
        }
        const winner = d.winnerTeam ? `${d.winnerTeam} 队胜` : "平局";
        return { log: { cls: "end", text: `[房${d.roomId}] ★ 对战结束：${winner}（A 存活 ${d.teamAAlive} / B 存活 ${d.teamBAlive}）` } };
      }
      default:
        return {};
    }
  }

  /** 用 /rooms 轮询结果校准成员数；空房间的玩家直接移除。 */
  applyRoomsSnapshot(roomCount: number, totalMembers: number, members: Map<number, number>): void {
    this.roomCount = roomCount;
    this.totalMembers = totalMembers;
    for (const [id, count] of members) {
      this.roomInfo(id).members = count;
      if (count === 0) {
        for (const [pid, p] of this.players) {
          if (p.roomId === id) this.players.delete(pid);
        }
      }
    }
  }

  /** 移除长时间无行为的玩家（断连/闲置）。 */
  prune(): void {
    const now = performance.now();
    for (const [id, p] of this.players) {
      if (now - p.lastSeen > PRUNE_MS) this.players.delete(id);
    }
  }

  battlingCount(): number {
    let n = 0;
    for (const r of this.rooms.values()) if (r.battling) n++;
    return n;
  }
}
