import type { Envelope, RoomsResponse } from "./types";

export type MessageHandler = (msg: Envelope) => void;
export type ConnStateHandler = (connected: boolean) => void;

// 连接 FieldServer 并注册为全局观察者（watch_all），自动重连。
export function connectWatcher(onMessage: MessageHandler, onConnState: ConnStateHandler): void {
  let attempt = 0;

  const open = () => {
    const proto = location.protocol === "https:" ? "wss" : "ws";
    const ws = new WebSocket(`${proto}://${location.host}/ws`);

    ws.onopen = () => {
      attempt = 0;
      ws.send(JSON.stringify({ type: "watch_all", data: {} }));
      onConnState(true);
    };
    ws.onmessage = (ev) => {
      try {
        onMessage(JSON.parse(ev.data as string) as Envelope);
      } catch {
        /* 忽略非 JSON 帧 */
      }
    };
    ws.onclose = () => {
      onConnState(false);
      const delay = Math.min(5000, 500 * 2 ** attempt++);
      setTimeout(open, delay);
    };
    ws.onerror = () => ws.close();
  };

  open();
}

// 轮询房间成员数（成员关系的权威来源；WS 事件流只携带活跃行为）。
export async function fetchRooms(): Promise<RoomsResponse | null> {
  try {
    const resp = await fetch("/rooms");
    if (!resp.ok) return null;
    return (await resp.json()) as RoomsResponse;
  } catch {
    return null;
  }
}
