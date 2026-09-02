import type { GameState, LogEntry, RoomInfo } from "./state";

const MAX_LOG = 120;

function $(id: string): HTMLElement {
  const el = document.getElementById(id);
  if (!el) throw new Error(`缺少元素 #${id}`);
  return el;
}

export class Hud {
  private readonly roomList = $("roomlist");
  private readonly logLines = $("loglines");
  private readonly detailTitle = $("detailTitle");
  private readonly detailBody = $("detailBody");
  private selected: number | null = null;
  private roomItems = new Map<number, HTMLElement>();
  private roomListBuilt = false;

  constructor(private readonly state: GameState) {}

  setConnState(connected: boolean): void {
    $("conn").className = connected ? "ok" : "";
    $("connText").textContent = connected ? "已连接（观察模式）" : "连接断开，重连中…";
  }

  setSelected(roomId: number | null): void {
    this.selected = roomId;
    for (const [id, el] of this.roomItems) el.classList.toggle("active", id === roomId);
    this.refreshDetail();
  }

  appendLog(entry: LogEntry): void {
    const div = document.createElement("div");
    if (entry.cls) div.className = entry.cls;
    div.textContent = entry.text;
    this.logLines.appendChild(div);
    while (this.logLines.childElementCount > MAX_LOG) this.logLines.firstElementChild?.remove();
    this.logLines.scrollTop = this.logLines.scrollHeight;
  }

  /** 低频刷新（每 ~1s）：统计数字、房间列表、详情面板。 */
  refresh(): void {
    $("stRooms").textContent = String(this.state.roomCount || "-");
    $("stMembers").textContent = String(this.state.totalMembers);
    $("stBattles").textContent = String(this.state.battlingCount());
    $("stPlayers").textContent = String(this.state.players.size);
    this.refreshRoomList();
    this.refreshDetail();
  }

  private refreshRoomList(): void {
    if (!this.roomListBuilt && this.state.roomCount > 0) {
      const frag = document.createDocumentFragment();
      for (let i = 0; i < this.state.roomCount; i++) {
        const el = document.createElement("div");
        el.className = "room-item";
        el.dataset.roomId = String(i);
        frag.appendChild(el);
        this.roomItems.set(i, el);
      }
      this.roomList.appendChild(frag);
      this.roomListBuilt = true;
    }
    for (const [id, el] of this.roomItems) {
      const r: RoomInfo | undefined = this.state.rooms.get(id);
      const members = r?.members ?? 0;
      const battling = r?.battling ?? false;
      el.innerHTML = `<span>#${id}</span><span>${battling ? '<span class="battle">⚔</span> ' : ""}${members}人</span>`;
      el.classList.toggle("has-members", members > 0);
      el.classList.toggle("active", id === this.selected);
    }
  }

  private refreshDetail(): void {
    if (this.selected === null) {
      this.detailTitle.textContent = "未选中房间（点击地块）";
      this.detailBody.innerHTML = "";
      return;
    }
    const id = this.selected;
    const r = this.state.rooms.get(id);
    this.detailTitle.textContent = `房间 #${id}${r?.battling ? " · ⚔ 对战中" : ""} · ${r?.members ?? 0} 人在线`;

    const players = [...this.state.players.values()].filter((p) => p.roomId === id);
    if (players.length === 0) {
      this.detailBody.innerHTML = `<div style="color:#66788f">暂无活跃玩家（有移动/对战行为才显示）</div>`;
      return;
    }
    players.sort((a, b) => (a.team ?? "~").localeCompare(b.team ?? "~") || a.id.localeCompare(b.id));
    const rows = players
      .map((p) => {
        const team = p.team ? `<span class="tag tag-${p.team.toLowerCase()}">${p.team}</span>` : "-";
        const cls = p.alive ? "" : ' class="dead"';
        return `<tr${cls}><td>${p.id}</td><td>${team}</td><td>${p.alive ? p.hp : "✝"}</td><td>${p.x.toFixed(0)},${p.y.toFixed(0)}</td></tr>`;
      })
      .join("");
    this.detailBody.innerHTML = `<table><tr><th>玩家</th><th>队</th><th>HP</th><th>坐标</th></tr>${rows}</table>`;
  }
}

export function bindRoomListClick(onSelect: (roomId: number) => void): void {
  $("roomlist").addEventListener("click", (e) => {
    const item = (e.target as HTMLElement).closest(".room-item") as HTMLElement | null;
    if (item?.dataset.roomId !== undefined) onSelect(Number(item.dataset.roomId));
  });
}
