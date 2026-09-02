#!/usr/bin/env python3
"""FieldServer 活动模拟器：为 dashboard 演示制造持续活动。

房间 3/7：各 4 个 walker 随机移动 + 偶尔聊天
房间 9：  5v5 循环对战（A 队 bot 自动攻击，打完自动再开）
"""
import asyncio
import json
import random
import traceback

from websockets.asyncio.client import connect

URL = "ws://127.0.0.1:5000/ws"
RUN_SECONDS = 600


async def drain(ws):
    try:
        async for _ in ws:
            pass
    except Exception:
        pass


async def walker(room: int, stop: asyncio.Event):
    ws = await connect(URL)
    asyncio.create_task(drain(ws))
    await ws.send(json.dumps({"type": "join", "data": {"roomId": room}}))
    x, y = 50.0, 50.0
    while not stop.is_set():
        x = min(100, max(0, x + random.uniform(-3, 3)))
        y = min(100, max(0, y + random.uniform(-3, 3)))
        await ws.send(json.dumps({"type": "move", "data": {"x": x, "y": y}}))
        if random.random() < 0.06:
            await ws.send(json.dumps({"type": "chat", "data": {"content": f"room{room} 巡逻中"}}))
        await asyncio.sleep(0.3)


async def fighter(room: int, team: str, stop: asyncio.Event):
    """参战队员：A 队自动攻击，B 队站桩；对战结束后 3s 重新加入。"""
    ws = await connect(URL)
    inbox = asyncio.Queue()

    async def pump():
        try:
            async for raw in ws:
                await inbox.put(json.loads(raw))
        except Exception:
            pass

    asyncio.create_task(pump())
    await ws.send(json.dumps({"type": "join", "data": {"roomId": room}}))

    while not stop.is_set():
        # 清空上一场积压的事件，避免按陈旧状态行动
        while not inbox.empty():
            inbox.get_nowait()
        await ws.send(json.dumps({"type": "battle_join", "data": {"team": team}}))
        enemies: list[str] = []
        # 等待开战
        while not stop.is_set():
            msg = await inbox.get()
            if msg["type"] == "battle_started":
                enemies = [p["id"] for p in msg["data"]["players"] if p["team"] != team]
                break
            if msg["type"] == "battle_ended":
                break
        # 战斗中：A 队攻击
        while not stop.is_set() and enemies:
            if team == "A":
                await ws.send(json.dumps({
                    "type": "battle_action",
                    "data": {"action": "attack", "targetId": random.choice(enemies)},
                }))
                await asyncio.sleep(1.2)  # 放慢节奏，让 dashboard 能看清对战过程
            try:
                msg = await asyncio.wait_for(inbox.get(), timeout=0.35)
                if msg["type"] == "battle_player_died":
                    enemies = [e for e in enemies if e != msg["data"]["playerId"]]
                elif msg["type"] == "battle_ended":
                    break
            except asyncio.TimeoutError:
                pass
            if team == "B":
                await asyncio.sleep(0.35)
        await asyncio.sleep(3)


async def main():
    stop = asyncio.Event()
    tasks: list[asyncio.Task] = []

    def watch(t: asyncio.Task):
        if not t.cancelled() and (exc := t.exception()):
            traceback.print_exception(exc)

    for room in (3, 7):
        for _ in range(4):
            t = asyncio.create_task(walker(room, stop))
            t.add_done_callback(watch)
            tasks.append(t)
    for i in range(10):
        t = asyncio.create_task(fighter(9, "A" if i < 5 else "B", stop))
        t.add_done_callback(watch)
        tasks.append(t)

    print(f"模拟器启动：房间3/7 各 4 walker，房间9 循环 5v5，运行 {RUN_SECONDS}s", flush=True)
    try:
        await asyncio.sleep(RUN_SECONDS)
    finally:
        stop.set()
        await asyncio.gather(*tasks, return_exceptions=True)


if __name__ == "__main__":
    asyncio.run(main())
