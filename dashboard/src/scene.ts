import * as THREE from "three";
import type { PlayerInfo, RoomInfo } from "./state";

export const FIELD_SIZE = 100; // 与 rooms.yaml Movement.FieldWidth/Height 一致
const SCALE = 0.1; // 场地单位 → 场景单位（每房间地块 10×10）
const TILE = FIELD_SIZE * SCALE;
const GAP = 2.5;
const COLS = 16;

const COLOR_BASE = new THREE.Color("#1a2233");
const COLOR_POPULATED = new THREE.Color("#2f7a4d");
const COLOR_BATTLE = new THREE.Color("#ff8c1a");
const COLOR_TEAM_A = new THREE.Color("#4da3ff");
const COLOR_TEAM_B = new THREE.Color("#ff5a4d");
const COLOR_NO_TEAM = new THREE.Color("#9aa7bd");
const COLOR_DEAD = new THREE.Color("#3a4150");

interface AttackLine {
  line: THREE.Line;
  ttl: number;
}

/** 房间号 → 地块中心世界坐标（场地原点为地块左上角）。 */
export function roomOrigin(roomId: number): { x: number; z: number } {
  const col = roomId % COLS;
  const row = Math.floor(roomId / COLS);
  return { x: (col - (COLS - 1) / 2) * (TILE + GAP), z: (row - 3.5) * (TILE + GAP) };
}

function fieldToWorld(roomId: number, fx: number, fy: number): THREE.Vector3 {
  const o = roomOrigin(roomId);
  return new THREE.Vector3(o.x - TILE / 2 + fx * SCALE, 0, o.z - TILE / 2 + fy * SCALE);
}

export class SceneManager {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene: THREE.Scene;
  private readonly camera: THREE.PerspectiveCamera;
  private readonly raycaster = new THREE.Raycaster();

  private readonly tiles: THREE.Mesh[] = [];
  private readonly tileMats: THREE.MeshStandardMaterial[] = [];
  private readonly players = new Map<string, THREE.Group>();
  private readonly attackLines: AttackLine[] = [];
  private readonly focusFrame: THREE.LineSegments;
  private readonly focusGrid: THREE.GridHelper;

  private selected: number | null = null;
  private onSelect: (roomId: number | null) => void = () => {};

  // 相机轨道（目标点 + 球坐标），带阻尼趋近
  private target = new THREE.Vector3(0, 0, 0);
  private targetGoal = new THREE.Vector3(0, 0, 0);
  private theta = -Math.PI / 2;
  private phi = 0.85;
  private radius = 170;
  private radiusGoal = 170;

  constructor(container: HTMLElement) {
    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    this.renderer.setSize(innerWidth, innerHeight);
    container.appendChild(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color("#0b0e14");

    this.camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.1, 1000);

    this.scene.add(new THREE.AmbientLight(0xffffff, 0.55));
    const dir = new THREE.DirectionalLight(0xffffff, 1.1);
    dir.position.set(60, 120, 40);
    this.scene.add(dir);

    // 128 个房间地块
    const tileGeo = new THREE.BoxGeometry(TILE, 0.3, TILE);
    for (let i = 0; i < 128; i++) {
      const mat = new THREE.MeshStandardMaterial({ color: COLOR_BASE.clone() });
      const tile = new THREE.Mesh(tileGeo, mat);
      const o = roomOrigin(i);
      tile.position.set(o.x, -0.15, o.z);
      tile.userData.roomId = i;
      this.scene.add(tile);
      this.tiles.push(tile);
      this.tileMats.push(mat);
    }

    // 聚焦框 + 聚焦房间的场地网格
    this.focusFrame = new THREE.LineSegments(
      new THREE.EdgesGeometry(new THREE.BoxGeometry(TILE + 0.6, 0.34, TILE + 0.6)),
      new THREE.LineBasicMaterial({ color: 0x6fb3ff }),
    );
    this.focusFrame.visible = false;
    this.scene.add(this.focusFrame);

    this.focusGrid = new THREE.GridHelper(TILE, 10, 0x3a4a6a, 0x2a3550);
    this.focusGrid.visible = false;
    this.scene.add(this.focusGrid);

    this.bindInput();
    addEventListener("resize", () => {
      this.camera.aspect = innerWidth / innerHeight;
      this.camera.updateProjectionMatrix();
      this.renderer.setSize(innerWidth, innerHeight);
    });
  }

  setSelectHandler(cb: (roomId: number | null) => void): void {
    this.onSelect = cb;
  }

  get selectedRoom(): number | null {
    return this.selected;
  }

  selectRoom(roomId: number | null): void {
    this.selected = roomId;
    if (roomId === null) {
      this.targetGoal.set(0, 0, 0);
      this.radiusGoal = 170;
      this.focusFrame.visible = false;
      this.focusGrid.visible = false;
    } else {
      const o = roomOrigin(roomId);
      this.targetGoal.set(o.x, 0, o.z);
      this.radiusGoal = 26;
      this.focusFrame.position.set(o.x, 0, o.z);
      this.focusFrame.visible = true;
      this.focusGrid.position.set(o.x, 0.02, o.z);
      this.focusGrid.visible = true;
    }
    this.onSelect(roomId);
  }

  /** 同步玩家实体：新建/更新/删除，位置平滑插值。 */
  syncPlayers(players: Map<string, PlayerInfo>): void {
    for (const [id, mesh] of this.players) {
      if (!players.has(id)) {
        this.scene.remove(mesh);
        this.players.delete(id);
      }
    }
    for (const p of players.values()) {
      let mesh = this.players.get(p.id);
      if (!mesh) {
        mesh = this.buildPlayerMesh();
        this.players.set(p.id, mesh);
        this.scene.add(mesh);
        const pos = fieldToWorld(p.roomId, p.x, p.y);
        mesh.position.set(pos.x, mesh.position.y, pos.z);
      }
      mesh.userData.target = fieldToWorld(p.roomId, p.x, p.y);

      const body = mesh.children[0] as THREE.Mesh<THREE.CapsuleGeometry, THREE.MeshStandardMaterial>;
      const ring = mesh.children[1] as THREE.Mesh<THREE.TorusGeometry, THREE.MeshBasicMaterial>;
      const color = !p.alive ? COLOR_DEAD : p.team === "A" ? COLOR_TEAM_A : p.team === "B" ? COLOR_TEAM_B : COLOR_NO_TEAM;
      // 血量影响明暗：满血亮、残血暗
      const dim = p.alive ? 0.45 + 0.55 * (p.hp / 100) : 1;
      body.material.color.copy(color).multiplyScalar(dim);
      ring.material.color.copy(p.team === "A" ? COLOR_TEAM_A : p.team === "B" ? COLOR_TEAM_B : COLOR_NO_TEAM);
      ring.visible = p.team !== null;
      body.rotation.z = p.alive ? 0 : Math.PI / 2; // 阵亡者倒地
    }
  }

  /** 更新地块着色：成员数 → 绿色深浅；对战中 → 橙色脉动。 */
  updateRooms(rooms: Map<number, RoomInfo>, timeSec: number): void {
    for (const r of rooms.values()) {
      const mat = this.tileMats[r.id];
      if (!mat) continue;
      if (r.battling) {
        const pulse = 0.5 + 0.5 * Math.sin(timeSec * 4);
        mat.color.copy(COLOR_BATTLE).multiplyScalar(0.55 + 0.45 * pulse);
        mat.emissive.copy(COLOR_BATTLE).multiplyScalar(0.25 * pulse);
      } else {
        const t = Math.min(1, r.members / 12);
        mat.color.copy(COLOR_BASE).lerp(COLOR_POPULATED, t);
        mat.emissive.setScalar(0);
      }
    }
  }

  addAttackLine(roomId: number, from: { x: number; y: number }, to: { x: number; y: number }): void {
    const a = fieldToWorld(roomId, from.x, from.y).setY(1);
    const b = fieldToWorld(roomId, to.x, to.y).setY(1);
    const geo = new THREE.BufferGeometry().setFromPoints([a, b]);
    const mat = new THREE.LineBasicMaterial({ color: 0xffd54d, transparent: true, opacity: 1 });
    const line = new THREE.Line(geo, mat);
    this.scene.add(line);
    this.attackLines.push({ line, ttl: 0.5 });
  }

  /** 每帧：相机阻尼、玩家位置插值、攻击线淡出。 */
  tick(dt: number): void {
    const k = 1 - Math.exp(-dt * 5);
    this.target.lerp(this.targetGoal, k);
    this.radius += (this.radiusGoal - this.radius) * k;

    const sp = Math.sin(this.phi);
    this.camera.position.set(
      this.target.x + this.radius * sp * Math.cos(this.theta),
      this.target.y + this.radius * Math.cos(this.phi),
      this.target.z + this.radius * sp * Math.sin(this.theta),
    );
    this.camera.lookAt(this.target);

    for (const mesh of this.players.values()) {
      const t = mesh.userData.target as THREE.Vector3 | undefined;
      if (!t) continue;
      mesh.position.x += (t.x - mesh.position.x) * k;
      mesh.position.z += (t.z - mesh.position.z) * k;
    }

    for (let i = this.attackLines.length - 1; i >= 0; i--) {
      const al = this.attackLines[i];
      al.ttl -= dt;
      const mat = al.line.material as THREE.LineBasicMaterial;
      mat.opacity = Math.max(0, al.ttl / 0.5);
      if (al.ttl <= 0) {
        this.scene.remove(al.line);
        al.line.geometry.dispose();
        mat.dispose();
        this.attackLines.splice(i, 1);
      }
    }

    this.renderer.render(this.scene, this.camera);
  }

  private buildPlayerMesh(): THREE.Group {
    const group = new THREE.Group();
    const body = new THREE.Mesh(
      new THREE.CapsuleGeometry(0.34, 0.75, 4, 12),
      new THREE.MeshStandardMaterial({ color: COLOR_NO_TEAM.clone() }),
    );
    body.position.y = 0.9;
    const ring = new THREE.Mesh(
      new THREE.TorusGeometry(0.5, 0.06, 8, 24),
      new THREE.MeshBasicMaterial({ color: COLOR_NO_TEAM.clone() }),
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.y = 0.05;
    ring.visible = false;
    group.add(body, ring);
    return group;
  }

  private bindInput(): void {
    const el = this.renderer.domElement;
    let dragging = false;
    let moved = 0;
    let lastX = 0;
    let lastY = 0;

    el.addEventListener("pointerdown", (e) => {
      dragging = true;
      moved = 0;
      lastX = e.clientX;
      lastY = e.clientY;
      el.setPointerCapture(e.pointerId);
    });
    el.addEventListener("pointermove", (e) => {
      if (!dragging) return;
      const dx = e.clientX - lastX;
      const dy = e.clientY - lastY;
      lastX = e.clientX;
      lastY = e.clientY;
      moved += Math.abs(dx) + Math.abs(dy);
      this.theta -= dx * 0.005;
      this.phi = THREE.MathUtils.clamp(this.phi - dy * 0.005, 0.12, 1.45);
    });
    el.addEventListener("pointerup", (e) => {
      dragging = false;
      if (moved < 6) this.handleClick(e);
    });
    el.addEventListener("wheel", (e) => {
      e.preventDefault();
      this.radiusGoal = THREE.MathUtils.clamp(this.radiusGoal * (1 + e.deltaY * 0.001), 10, 220);
    }, { passive: false });
    addEventListener("keydown", (e) => {
      if (e.key === "Escape") this.selectRoom(null);
    });
  }

  private handleClick(e: PointerEvent): void {
    const ndc = new THREE.Vector2((e.clientX / innerWidth) * 2 - 1, -(e.clientY / innerHeight) * 2 + 1);
    this.raycaster.setFromCamera(ndc, this.camera);
    const hits = this.raycaster.intersectObjects(this.tiles, false);
    if (hits.length > 0) {
      const roomId = hits[0].object.userData.roomId as number;
      this.selectRoom(roomId === this.selected ? null : roomId);
    } else {
      this.selectRoom(null);
    }
  }
}
