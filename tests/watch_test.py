#!/usr/bin/env python3
"""FieldServer 全局观察者（dashboard 通道）功能验证。

用法:
    venv/bin/python tests/watch_test.py [--url ws://127.0.0.1:5000/ws]

验证内容:
    1. watch_all 注册成功，收到 watching 回执
    2. 观察者收到多个房间的 moved/chat 广播（本人不入房）
    3. 观察者收到对战事件（battle_started/action/ended）
    4. 非观察者收不到其他房间事件（隔离性对照）
    5. 观察者断连后不影响房间通信
"""
import argparse
import asyncio
import json

from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = ""):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


class Client:
    def __init__(self):
        self.ws = None
        self.id = ""
        self.events: list[dict] = []
        self.errors: list[str] = []
        self._recv_task: asyncio.Task | None = None

    async def open(self, url: str):
        self.ws = await connect(url, max_size=2**20)
        self._recv_task = asyncio.create_task(self._recv_loop())

    async def _recv_loop(self):
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                if msg.get("type") == "error":
                    self.errors.append(msg["data"]["content"])
                else:
                    self.events.append(msg)
        except (ConnectionClosed, asyncio.CancelledError):
            pass

    async def send(self, msg_type: str, data: dict):
        await self.ws.send(json.dumps({"type": msg_type, "data": data}))

    def all(self, msg_type: str) -> list[dict]:
        return [e for e in self.events if e["type"] == msg_type]

    async def wait_for(self, msg_type: str, timeout: float = 10, pred=None) -> dict:
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            for e in self.events:
                if e["type"] == msg_type and (pred is None or pred(e)):
                    return e
            if asyncio.get_event_loop().time() > deadline:
                raise TimeoutError(f"等待 {msg_type} 超时")
            await asyncio.sleep(0.01)

    async def close(self):
        if self._recv_task:
            self._recv_task.cancel()
        if self.ws:
            try:
                await self.ws.close()
            except ConnectionClosed:
                pass


async def join_room(client: Client, room_id: int):
    await client.send("join", {"roomId": room_id})
    joined = await client.wait_for("joined")
    client.id = joined["data"]["connectionId"]


async def main() -> int:
    parser = argparse.ArgumentParser(description="FieldServer 观察者验证")
    parser.add_argument("--url", default="ws://127.0.0.1:5000/ws")
    args = parser.parse_args()

    print("[测试1] watch_all 注册")
    watcher = Client()
    await watcher.open(args.url)
    await watcher.send("watch_all", {})
    watching = await watcher.wait_for("watching")
    check("收到 watching 回执", watching["data"]["roomCount"] == 128,
          json.dumps(watching["data"]))

    print("[测试2] 观察者收到多房间 moved/chat（不入房）")
    p1, p2 = Client(), Client()
    await p1.open(args.url)
    await p2.open(args.url)
    await join_room(p1, 50)
    await join_room(p2, 51)
    await p1.send("move", {"x": 60, "y": 60})
    await p1.send("chat", {"content": "hello from room 50"})
    await p2.send("move", {"x": 30, "y": 30})

    m50 = await watcher.wait_for("moved", pred=lambda e: e["data"]["roomId"] == 50)
    m51 = await watcher.wait_for("moved", pred=lambda e: e["data"]["roomId"] == 51)
    check("收到房间50的 moved", m50["data"]["connectionId"] == p1.id)
    check("收到房间51的 moved", m51["data"]["connectionId"] == p2.id)
    c50 = await watcher.wait_for("chat", pred=lambda e: e["data"]["roomId"] == 50)
    check("收到房间50的 chat", c50["data"]["content"] == "hello from room 50")

    print("[测试3] 非观察者隔离性")
    check("p2(房间51) 收不到房间50的 chat",
          all(e["data"].get("roomId") != 51 or True for e in p2.all("chat")) and len(p2.all("chat")) == 0)

    print("[测试4] 观察者收到对战事件")
    room_id = 52
    team = [Client() for _ in range(10)]
    for c in team:
        await c.open(args.url)
    await asyncio.gather(*(join_room(team[i], room_id) for i in range(10)))
    await asyncio.gather(*(team[i].send("battle_join", {"team": "A" if i < 5 else "B"})
                           for i in range(10)))
    started = await watcher.wait_for("battle_started", timeout=15,
                                     pred=lambda e: e["data"]["roomId"] == room_id)
    check("收到 battle_started", len(started["data"]["players"]) == 10)

    a0, b0 = team[0], team[5]
    await a0.send("battle_action", {"action": "attack", "targetId": b0.id})
    act = await watcher.wait_for("battle_action_event",
                                 pred=lambda e: e["data"]["roomId"] == room_id)
    check("收到 battle_action_event", act["data"]["actorId"] == a0.id and act["data"]["damage"] == 20)

    # A 队歼灭 B 队
    async def attack_loop(attacker: Client, targets: list[Client]):
        while True:
            died = {e["data"]["playerId"] for e in attacker.all("battle_player_died")}
            alive = [t for t in targets if t.id not in died]
            if not alive:
                return
            await attacker.send("battle_action", {"action": "attack", "targetId": alive[0].id})
            await asyncio.sleep(0.02)

    await asyncio.gather(*(attack_loop(team[i], team[5:]) for i in range(5)),
                         watcher.wait_for("battle_ended", timeout=60,
                                          pred=lambda e: e["data"]["roomId"] == room_id))
    ended = [e for e in watcher.all("battle_ended") if e["data"]["roomId"] == room_id][-1]
    check("收到 battle_ended", ended["data"]["winnerTeam"] == "A")

    print("[测试5] 观察者断连不影响房间通信")
    await watcher.close()
    await asyncio.sleep(0.3)
    await p1.send("chat", {"content": "still alive"})
    reply = await p1.wait_for("chat", pred=lambda e: e["data"]["content"] == "still alive")
    check("观察者断开后房间通信正常", reply["data"]["from"] == p1.id)

    await asyncio.gather(p1.close(), p2.close(), *(c.close() for c in team))

    print()
    if FAILURES:
        print(f"FAIL: {len(FAILURES)} 项未通过: {FAILURES}")
        return 1
    print("全部观察者测试 PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
