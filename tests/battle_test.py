#!/usr/bin/env python3
"""FieldServer 5v5 对战功能验证（Python 版）。

用法:
    venv/bin/python tests/battle_test.py [--url ws://127.0.0.1:5000/ws]

验证内容:
    1. 错误处理：未入房参战 / 未开战攻击 / 攻击队友 / 攻击阵亡者
    2. 完整对局：5v5 组队 → 自动开战 → 攻击 → 阵亡 → 分胜负 → 自动重置
    3. 对战中断连：按阵亡处理并广播 battle_player_died(reason=leave)
    4. 并行对战：16 个房间同时打完一场
"""
import argparse
import asyncio
import json
import urllib.request

from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = ""):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def get_battle(http_base: str, room_id: int) -> dict:
    try:
        with urllib.request.urlopen(f"{http_base}/battles/{room_id}", timeout=10) as resp:
            return json.load(resp)
    except urllib.error.HTTPError:
        return {"state": "none"}


class Player:
    def __init__(self, room_id: int):
        self.room_id = room_id
        self.ws = None
        self.id = ""
        self.team = ""
        self.events: list[dict] = []
        self.errors: list[str] = []
        self._recv_task: asyncio.Task | None = None

    async def connect(self, url: str, team: str | None = None):
        self.ws = await connect(url, max_size=2**20)
        self._recv_task = asyncio.create_task(self._recv_loop())
        await self.send("join", {"roomId": self.room_id})
        joined = await self.wait_for("joined")
        self.id = joined["data"]["connectionId"]
        await self.send("battle_join", {"team": team} if team else {})
        bj = await self.wait_for("battle_joined")
        self.team = bj["data"]["team"]

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

    def last(self, msg_type: str) -> dict:
        for e in reversed(self.events):
            if e["type"] == msg_type:
                return e
        raise AssertionError(f"玩家 {self.id or '?'} 未收到 {msg_type}")

    def all(self, msg_type: str) -> list[dict]:
        return [e for e in self.events if e["type"] == msg_type]

    async def wait_for(self, msg_type: str, timeout: float = 10) -> dict:
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            for e in self.events:
                if e["type"] == msg_type:
                    return e
            if asyncio.get_event_loop().time() > deadline:
                raise TimeoutError(f"等待 {msg_type} 超时")
            await asyncio.sleep(0.01)

    async def wait_for_n(self, msg_type: str, n: int, timeout: float = 30) -> list[dict]:
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            got = self.all(msg_type)
            if len(got) >= n:
                return got
            if asyncio.get_event_loop().time() > deadline:
                raise TimeoutError(f"等待 {msg_type}×{n} 超时（当前 {len(got)}）")
            await asyncio.sleep(0.01)

    async def close(self):
        if self._recv_task:
            self._recv_task.cancel()
        if self.ws:
            try:
                await self.ws.close()
            except ConnectionClosed:
                pass


async def fight_until_end(attackers: list[Player], defenders: list[Player]):
    """攻击方轮流攻击存活防守方，直到收到 battle_ended。"""
    async def attack_loop(p: Player):
        while True:
            alive = [d for d in defenders
                     if not any(e["data"]["playerId"] == d.id for e in p.all("battle_player_died"))]
            if not alive:
                return
            await p.send("battle_action", {"action": "attack", "targetId": alive[0].id})
            await asyncio.sleep(0.02)

    await asyncio.gather(*(attack_loop(p) for p in attackers),
                         *(p.wait_for("battle_ended", timeout=60) for p in attackers))


async def test_errors(url: str):
    print("[测试1] 错误处理")
    # 未入房直接 battle_join
    p = Player(999)
    p.ws = await connect(url, max_size=2**20)
    p._recv_task = asyncio.create_task(p._recv_loop())
    await p.send("battle_join", {})
    await asyncio.sleep(0.3)
    check("未入房 battle_join 报错", any("尚未加入房间" in e for e in p.errors))

    # 入房但未开战时攻击
    p2 = Player(1)
    await p2.connect(url)
    await p2.send("battle_action", {"action": "attack", "targetId": "xxx"})
    await asyncio.sleep(0.3)
    check("未开战 battle_action 报错", any("对战未开始" in e for e in p2.errors))
    await p.close()
    await p2.close()
    return p2  # 留给后续清理（无对战状态）


async def test_full_battle(url: str, http_base: str, room_id: int = 0):
    print(f"[测试2] 完整 5v5 对局（房间 {room_id}）")
    team_a = [Player(room_id) for _ in range(5)]
    team_b = [Player(room_id) for _ in range(5)]
    all_p = team_a + team_b
    await asyncio.gather(*(p.connect(url, "A") for p in team_a),
                         *(p.connect(url, "B") for p in team_b))

    # 满员自动开战
    started = await asyncio.gather(*(p.wait_for("battle_started") for p in all_p))
    check("满员自动开战", all(s["data"]["roomId"] == room_id for s in started))
    check("开战时 10 名玩家信息齐全", len(started[0]["data"]["players"]) == 10)
    a_ids = {p.id for p in team_a}
    check("队伍分配正确",
          all(pl["team"] == "A" for pl in started[0]["data"]["players"] if pl["id"] in a_ids))

    # 攻击队友报错
    await team_a[0].send("battle_action", {"action": "attack", "targetId": team_a[1].id})
    await asyncio.sleep(0.3)
    check("攻击队友报错", any("队友" in e for e in team_a[0].errors))

    # A 队歼灭 B 队
    await fight_until_end(team_a, team_b)
    ended_a = team_a[0].last("battle_ended")["data"]
    check("A 队获胜", ended_a["winnerTeam"] == "A",
          f"winner={ended_a['winnerTeam']}, A存活={ended_a['teamAAlive']}, B存活={ended_a['teamBAlive']}")
    check("B 队全灭", ended_a["teamBAlive"] == 0)
    check("阵亡广播 5 次", len(team_a[0].all("battle_player_died")) == 5)
    check("观战方(阵亡B队员)也收到结局",
          team_b[0].last("battle_ended")["data"]["winnerTeam"] == "A")

    # 结束后自动重置
    state = get_battle(http_base, room_id)["state"]
    check("结束后重置为 Waiting", state == "Waiting", f"state={state}")

    # 重置后可开新一场
    await team_a[0].send("battle_join", {})
    joined = await team_a[0].wait_for("battle_joined")
    check("重置后可再次加入对战", joined["data"]["state"] == "Waiting")
    await asyncio.gather(*(p.close() for p in all_p))


async def test_disconnect_mid_battle(url: str, room_id: int = 2):
    print(f"[测试3] 对战中断连按阵亡处理（房间 {room_id}）")
    team_a = [Player(room_id) for _ in range(5)]
    team_b = [Player(room_id) for _ in range(5)]
    await asyncio.gather(*(p.connect(url, "A") for p in team_a),
                         *(p.connect(url, "B") for p in team_b))
    await asyncio.gather(*(p.wait_for("battle_started") for p in team_a + team_b))

    quitter = team_a[0]
    quitter_id = quitter.id
    await quitter.close()  # 对战中直接断开

    died = await team_b[0].wait_for("battle_player_died")
    check("断连广播阵亡", died["data"]["playerId"] == quitter_id
          and died["data"]["reason"] == "leave", json.dumps(died["data"]))

    # 剩余 A 队继续打，B 队反击歼灭 A
    await fight_until_end(team_b, team_a[1:])
    ended = team_b[0].last("battle_ended")["data"]
    check("B 队获胜", ended["winnerTeam"] == "B")
    await asyncio.gather(*(p.close() for p in team_a[1:] + team_b))


async def test_parallel_battles(url: str, room_base: int = 10, count: int = 16):
    print(f"[测试4] {count} 个房间并行 5v5（房间 {room_base}~{room_base + count - 1}）")

    async def run_room(room_id: int) -> str | None:
        team_a = [Player(room_id) for _ in range(5)]
        team_b = [Player(room_id) for _ in range(5)]
        try:
            await asyncio.gather(*(p.connect(url, "A") for p in team_a),
                                 *(p.connect(url, "B") for p in team_b))
            await asyncio.gather(*(p.wait_for("battle_started") for p in team_a + team_b))
            await fight_until_end(team_a, team_b)
            winner = team_a[0].last("battle_ended")["data"]["winnerTeam"]
            return None if winner == "A" else f"房间{room_id} 获胜方异常: {winner}"
        except Exception as e:  # noqa: BLE001
            return f"房间{room_id}: {e}"
        finally:
            await asyncio.gather(*(p.close() for p in team_a + team_b))

    results = await asyncio.gather(*(run_room(room_base + i) for i in range(count)))
    errors = [r for r in results if r]
    check(f"{count} 场并行对战全部正常结束", not errors, "; ".join(errors[:3]))


async def main() -> int:
    parser = argparse.ArgumentParser(description="FieldServer 5v5 对战验证")
    parser.add_argument("--url", default="ws://127.0.0.1:5000/ws")
    args = parser.parse_args()

    http_base = args.url.replace("wss://", "https://").replace("ws://", "http://")
    http_base = http_base[: http_base.rindex("/")]

    await test_errors(args.url)
    await test_full_battle(args.url, http_base)
    await test_disconnect_mid_battle(args.url)
    await test_parallel_battles(args.url)

    print()
    if FAILURES:
        print(f"FAIL: {len(FAILURES)} 项未通过: {FAILURES}")
        return 1
    print("全部对战测试 PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
