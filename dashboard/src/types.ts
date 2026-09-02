// 与 FieldServer 消息契约（MessageContracts.cs）对应的类型定义。

export interface Envelope {
  type: string;
  data: any;
}

export interface MovedPayload {
  roomId: number;
  connectionId: string;
  x: number;
  y: number;
  timestamp: number;
}

export interface ChatEventPayload {
  roomId: number;
  from: string;
  content: string;
  timestamp: number;
}

export interface SystemPayload {
  content: string;
}

export interface BattlePlayerInfo {
  id: string;
  team: string;
  hp: number;
  alive: boolean;
}

export interface BattlePlayerJoinedPayload {
  roomId: number;
  playerId: string;
  team: string;
  teamACount: number;
  teamBCount: number;
}

export interface BattlePlayerLeftPayload {
  roomId: number;
  playerId: string;
  team: string;
}

export interface BattleStartedPayload {
  roomId: number;
  players: BattlePlayerInfo[];
}

export interface BattleActionEventPayload {
  roomId: number;
  actorId: string;
  action: string;
  targetId: string;
  damage: number;
  targetHp: number;
}

export interface BattlePlayerDiedPayload {
  roomId: number;
  playerId: string;
  team: string;
  reason: string;
}

export interface BattleEndedPayload {
  roomId: number;
  winnerTeam: string | null;
  teamAAlive: number;
  teamBAlive: number;
}

export interface WatchingPayload {
  roomCount: number;
  watcherCount: number;
}

export interface RoomsResponse {
  roomCount: number;
  totalMembers: number;
  rooms: { id: number; name: string; memberCount: number }[];
}
