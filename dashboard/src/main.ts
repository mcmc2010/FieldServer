import { connectWatcher, fetchRooms } from "./net";
import { GameState } from "./state";
import { SceneManager } from "./scene";
import { Hud, bindRoomListClick } from "./hud";

const state = new GameState();
const scene = new SceneManager(document.getElementById("app")!);
const hud = new Hud(state);

scene.setSelectHandler((roomId) => hud.setSelected(roomId));
bindRoomListClick((roomId) => scene.selectRoom(roomId));

// 观察者事件流：驱动状态、特效、日志
connectWatcher(
  (msg) => {
    const { log, fx } = state.apply(msg);
    if (log) hud.appendLog(log);
    if (fx?.attack) scene.addAttackLine(fx.attack.roomId, fx.attack.from, fx.attack.to);
  },
  (connected) => hud.setConnState(connected),
);

// 成员数轮询（每 3s）：房间人数与在线总数的权威来源
async function pollRooms(): Promise<void> {
  const snap = await fetchRooms();
  if (snap) {
    state.applyRoomsSnapshot(
      snap.roomCount,
      snap.totalMembers,
      new Map(snap.rooms.map((r) => [r.id, r.memberCount])),
    );
  }
}
void pollRooms();
setInterval(() => void pollRooms(), 3000);

// 低频 HUD 刷新
setInterval(() => {
  state.prune();
  hud.refresh();
}, 1000);

// 渲染循环
let last = performance.now();
function frame(now: number): void {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  scene.syncPlayers(state.players);
  scene.updateRooms(state.rooms, now / 1000);
  scene.tick(dt);
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);
