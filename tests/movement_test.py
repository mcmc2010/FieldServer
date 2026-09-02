#!/usr/bin/env python3
"""FieldServer 移动功能验证（Python 版）。

用法:
    venv/bin/python tests/movement_test.py [--url ws://127.0.0.1:5000/ws]

验证内容:
    1. 错误处理：未入房移动 / 非法参数
    2. 出生点：入房 joined 携带场地中心坐标
    3. 基本移动：move → 收到 moved 回执，同房间其他成员也收到广播
    4. 边界钳制：越界坐标收敛到场地边缘
    5. 速度校验：合法速度移动通过，瞬移被拒绝且位置不变
    6. 清理：退房/断连后位置被移除（GET /movement/{roomId}）
"""
import argparse
import asyncio
import json
import urllib.request

from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

FAILURES: list[str] = []

# 与 rooms.yaml 的 Movement 配置一致
FIELD_W, FIELD_H, MOVE_SPEED = 100.0, 100.0, 10.0


def check(name: str, ok: bool, detail: str = ""):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def get_movement(http_base: str, room_id: int) -> dict:
    try:
        with urllib.request.urlopen(f"{http_base}/movement/{room_id}", timeout=10) as resp:
            return json.load(resp)
    except urllib.error.HTTPError:
        return {"state": "none"}


class Player:
    def __init__(self, room_id: int):
        self.room_id = room_id
        self.ws = None
        self.id = ""
        self.pos: tuple[float, float] | None = None
        self.events: list[dict] = []
        self.errors: list[str] = []
        self._recv_task: asyncio.Task | None = None

    async def connect(self, url: str):
        self.ws = await connect(url, max_size=2**20)
        self._recv_task = asyncio.create_task(self._recv_loop())
        await self.send("join", {"roomId": self.room_id})
        joined = await self.wait_for("joined")
        self.id = joined["data"]["connectionId"]
        self.pos = (joined["data"]["x"], joined["data"]["y"])

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

    def last(self, msg_type: str) -> dict:
        for e in reversed(self.events):
            if e["type"] == msg_type:
                return e
        raise AssertionError(f"玩家 {self.id or '?'} 未收到 {msg_type}")

    async def wait_for(self, msg_type: str, timeout: float = 10) -> dict:
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            for e in self.events:
                if e["type"] == msg_type:
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


async def test_errors(url: str):
    print("[测试1] 错误处理")
    # 未入房直接 move
    p = Player(999)
    p.ws = await connect(url, max_size=2**20)
    p._recv_task = asyncio.create_task(p._recv_loop())
    await p.send("move", {"x": 1, "y": 1})
    await asyncio.sleep(0.3)
    check("未入房 move 报错", any("尚未加入房间" in e for e in p.errors))
    await p.close()

    # 非法参数
    p2 = Player(31)
    await p2.connect(url)
    await p2.send("move", {"x": "abc"})
    await p2.send("move", {})
    await asyncio.sleep(0.3)
    check("非法参数报错", sum("move 参数无效" in e for e in p2.errors) >= 2,
          f"errors={p2.errors}")
    await p2.close()


async def test_spawn_and_move(url: str, room_id: int = 32):
    print(f"[测试2] 出生点与基本移动（房间 {room_id}）")
    a, b = Player(room_id), Player(room_id)
    await a.connect(url)
    check("出生点为场地中心", a.pos == (FIELD_W / 2, FIELD_H / 2), f"pos={a.pos}")
    await b.connect(url)

    # A 移动：A 收到权威回执，B 收到广播
    await a.send("move", {"x": 55, "y": 45})
    moved_a = await a.wait_for("moved")
    moved_b = await b.wait_for("moved")
    ok = (moved_a["data"]["connectionId"] == a.id
          and moved_a["data"]["x"] == 55 and moved_a["data"]["y"] == 45)
    check("移动者收到 moved 回执", ok, json.dumps(moved_a["data"]))
    check("同房间成员收到 moved 广播",
          moved_b["data"]["connectionId"] == a.id and moved_b["data"]["x"] == 55)
    check("moved 携带时间戳", isinstance(moved_a["data"]["timestamp"], int))

    # B 不动，不应收到自己的 moved
    await asyncio.sleep(0.2)
    check("未移动者无多余 moved", all(e["data"]["connectionId"] != b.id for e in b.all("moved")))
    await a.close()
    await b.close()


async def test_bounds_clamp(url: str, room_id: int = 33):
    print(f"[测试3] 边界钳制（房间 {room_id}）")
    p = Player(room_id)
    await p.connect(url)
    await p.send("move", {"x": 9999, "y": -5})
    moved = await p.wait_for("moved")
    ok = moved["data"]["x"] == FIELD_W and moved["data"]["y"] == 0
    check("越界坐标被钳制到场地边缘", ok, json.dumps(moved["data"]))
    await p.close()


async def test_speed_limit(url: str, room_id: int = 34):
    print(f"[测试4] 速度校验（房间 {room_id}）")
    p = Player(room_id)
    await p.connect(url)  # 出生 (50, 50)

    async def wait_new_moved(before: int, timeout: float = 5) -> dict:
        deadline = asyncio.get_event_loop().time() + timeout
        while len(p.all("moved")) <= before:
            if asyncio.get_event_loop().time() > deadline:
                raise TimeoutError("等待新 moved 超时")
            await asyncio.sleep(0.01)
        return p.all("moved")[-1]

    # 首次移动不限速（进场定位）
    await p.send("move", {"x": 55, "y": 50})
    moved = await wait_new_moved(0)
    check("首次移动通过", moved["data"]["x"] == 55)

    # 合法速度：0.6s 移动 5 单位（上限 10×0.6×1.5=9）
    await asyncio.sleep(0.6)
    await p.send("move", {"x": 60, "y": 50})
    moved = await wait_new_moved(1)
    check("合法速度移动通过", moved["data"]["x"] == 60)

    # 瞬移：立刻移动 40 单位，远超上限
    await p.send("move", {"x": 100, "y": 50})
    await asyncio.sleep(0.3)
    check("瞬移被拒绝", any("速度超限" in e for e in p.errors), f"errors={p.errors}")
    check("瞬移后位置不变", all(e["data"]["x"] != 100 for e in p.all("moved")))

    # 被拒绝后位置仍是 60：等待足够时间后可继续移动（上限 10×3×1.5=45 > 40）
    await asyncio.sleep(3.0)
    before = len(p.all("moved"))
    await p.send("move", {"x": 100, "y": 50})
    moved = await wait_new_moved(before)
    check("冷却后可继续移动", moved["data"]["x"] == 100)
    await p.close()


async def test_cleanup(url: str, http_base: str, room_id: int = 35):
    print(f"[测试5] 退房/断连清理（房间 {room_id}）")
    a, b = Player(room_id), Player(room_id)
    await a.connect(url)
    await b.connect(url)
    players = get_movement(http_base, room_id).get("players", [])
    check("入房后位置表有 2 人", len(players) == 2, f"players={len(players)}")

    await a.send("leave", {})
    await a.wait_for("left")
    await asyncio.sleep(0.2)
    players = get_movement(http_base, room_id).get("players", [])
    check("主动退房后位置被清理", len(players) == 1 and players[0]["connectionId"] == b.id)

    await b.close()  # 断连
    await asyncio.sleep(0.5)
    players = get_movement(http_base, room_id).get("players", [])
    check("断连后位置被清理", len(players) == 0, f"players={players}")
    await a.close()


async def main() -> int:
    parser = argparse.ArgumentParser(description="FieldServer 移动功能验证")
    parser.add_argument("--url", default="ws://127.0.0.1:5000/ws")
    args = parser.parse_args()

    http_base = args.url.replace("wss://", "https://").replace("ws://", "http://")
    http_base = http_base[: http_base.rindex("/")]

    await test_errors(args.url)
    await test_spawn_and_move(args.url)
    await test_bounds_clamp(args.url)
    await test_speed_limit(args.url)
    await test_cleanup(args.url, http_base)

    print()
    if FAILURES:
        print(f"FAIL: {len(FAILURES)} 项未通过: {FAILURES}")
        return 1
    print("全部移动测试 PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
