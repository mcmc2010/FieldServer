#!/usr/bin/env python3
"""FieldServer WebSocket 压测/验证客户端（Python 版）。

用法:
    venv/bin/python tests/ws_bench.py [--url ws://127.0.0.1:5000/ws]
        [--rooms 128] [--clients-per-room 8] [--messages 100]

验证内容:
    1. /rooms 返回的房间数与 YAML 配置一致
    2. 全部客户端成功加入房间，服务器侧成员统计一致
    3. chat 广播零丢失（含发送者回执）
    4. 断开后服务器自动清理成员
"""
import argparse
import asyncio
import json
import time
import urllib.request

from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed


def get_rooms(http_base: str) -> dict:
    with urllib.request.urlopen(f"{http_base}/rooms", timeout=10) as resp:
        return json.load(resp)


class BenchClient:
    def __init__(self, room_id: int):
        self.room_id = room_id
        self.ws = None
        self.id = ""
        self.joined = asyncio.Event()
        self.chat_received = 0
        self._recv_task: asyncio.Task | None = None

    async def connect_and_join(self, url: str):
        self.ws = await connect(url, max_size=2**20)
        self._recv_task = asyncio.create_task(self._recv_loop())
        await self.ws.send(json.dumps({"type": "join", "data": {"roomId": self.room_id}}))
        await asyncio.wait_for(self.joined.wait(), timeout=30)

    async def _recv_loop(self):
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                t = msg.get("type")
                if t == "chat":
                    self.chat_received += 1
                elif t == "joined":
                    self.id = msg["data"]["connectionId"]
                    self.joined.set()
        except (ConnectionClosed, asyncio.CancelledError):
            pass

    async def send_chats(self, count: int):
        for i in range(count):
            await self.ws.send(json.dumps(
                {"type": "chat", "data": {"content": f"room{self.room_id} msg{i}"}}))

    async def close(self):
        if self._recv_task:
            self._recv_task.cancel()
        if self.ws:
            try:
                await self.ws.close()
            except ConnectionClosed:
                pass


async def main() -> int:
    parser = argparse.ArgumentParser(description="FieldServer WebSocket 压测")
    parser.add_argument("--url", default="ws://127.0.0.1:5000/ws")
    parser.add_argument("--rooms", type=int, default=128)
    parser.add_argument("--clients-per-room", type=int, default=8)
    parser.add_argument("--messages", type=int, default=100)
    args = parser.parse_args()

    http_base = args.url.replace("wss://", "https://").replace("ws://", "http://")
    http_base = http_base[: http_base.rindex("/")]

    # 0. 校验 YAML 配置的房间数
    data = get_rooms(http_base)
    print(f"[检查] 服务器房间数 = {data['roomCount']}（期望 {args.rooms}）")
    if data["roomCount"] != args.rooms:
        print("FAIL: YAML 配置的房间数未生效")
        return 1

    total_clients = args.rooms * args.clients_per_room
    print(f"[阶段1] 建立 {total_clients} 个连接并加入房间"
          f"（{args.rooms} 房间 × {args.clients_per_room} 客户端）...")

    clients = [BenchClient(r) for r in range(args.rooms) for _ in range(args.clients_per_room)]

    t0 = time.perf_counter()
    batch = 128
    for i in range(0, len(clients), batch):
        await asyncio.gather(*(c.connect_and_join(args.url) for c in clients[i : i + batch]))
    print(f"[阶段1] 完成，用时 {time.perf_counter() - t0:.1f}s")

    members = get_rooms(http_base)["totalMembers"]
    print(f"[检查] 服务器侧房间成员总数 = {members}（期望 {total_clients}）")
    if members != total_clients:
        print("FAIL: 房间成员统计不一致")
        return 1

    # 广播含发送者本人：每客户端应收 = 房间人数 × 每人消息数
    expected_total = total_clients * args.clients_per_room * args.messages
    print(f"[阶段2] 全部客户端发送 {args.messages} 条 chat"
          f"（总发送 {total_clients * args.messages:,} 条，预期投递 {expected_total:,} 条）...")

    t0 = time.perf_counter()
    await asyncio.gather(*(c.send_chats(args.messages) for c in clients))

    deadline = time.perf_counter() + 120
    received = 0
    while time.perf_counter() < deadline:
        received = sum(c.chat_received for c in clients)
        if received >= expected_total:
            break
        await asyncio.sleep(0.2)
    elapsed = time.perf_counter() - t0

    missing = expected_total - received
    print()
    print(f"预期投递:   {expected_total:,}")
    print(f"实际收到:   {received:,}")
    print(f"丢失:       {missing:,}")
    print(f"总耗时:     {elapsed:.2f}s")
    print(f"投递吞吐:   {received / elapsed:,.0f} msg/s")

    # 3. 断开后验证成员清理
    await asyncio.gather(*(c.close() for c in clients))
    await asyncio.sleep(1.5)
    members = get_rooms(http_base)["totalMembers"]
    print(f"[检查] 断开后服务器成员数 = {members}（期望 0）")
    if members != 0:
        print("FAIL: 断连清理不完整")
        return 1

    print("PASS: 128 房间 WebSocket 通信正常，无消息丢失" if missing == 0
          else "FAIL: 有消息丢失")
    return 0 if missing == 0 else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
