// AquaCulture.Net — 3D digital pond (Three.js). Made by Gravicode Studios, led by Kang Fadhil.
import * as THREE from './three/three.module.min.js';
import { OrbitControls } from './three/OrbitControls.js';

const scenes = new Map();
let nextId = 1;

function doColor(doMgL) {
  // hypoxic coral → amber → sea green → deep teal
  const stops = [[0, 0x7a1f1f], [2, 0xe0614a], [4, 0xe9b44c], [6, 0x7cc6a4], [8, 0x1fa3a8], [10, 0x0b4f6c]];
  const v = Math.max(0, Math.min(10, doMgL ?? 6));
  for (let i = 1; i < stops.length; i++) {
    if (v <= stops[i][0]) {
      const t = (v - stops[i - 1][0]) / (stops[i][0] - stops[i - 1][0]);
      return new THREE.Color(stops[i - 1][1]).lerp(new THREE.Color(stops[i][1]), t);
    }
  }
  return new THREE.Color(stops[stops.length - 1][1]);
}

function makeWheel(scene) {
  const g = new THREE.Group();
  const hub = new THREE.Mesh(new THREE.CylinderGeometry(0.15, 0.15, 3.2, 12), new THREE.MeshStandardMaterial({ color: 0x334048, metalness: 0.6, roughness: 0.4 }));
  hub.rotation.z = Math.PI / 2; g.add(hub);
  const rotor = new THREE.Group();
  for (let k = 0; k < 2; k++) {
    for (let i = 0; i < 8; i++) {
      const p = new THREE.Mesh(new THREE.BoxGeometry(0.25, 1.4, 0.06), new THREE.MeshStandardMaterial({ color: 0x1f6f8b, roughness: 0.6 }));
      p.position.x = k === 0 ? -0.9 : 0.9;
      const a = (i / 8) * Math.PI * 2;
      p.position.y = Math.sin(a) * 0.5; p.position.z = Math.cos(a) * 0.5; p.rotation.x = -a;
      rotor.add(p);
    }
  }
  g.add(rotor);
  const floatL = new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.28, 1.6, 16), new THREE.MeshStandardMaterial({ color: 0xe9e3d2, roughness: 0.8 }));
  floatL.rotation.x = Math.PI / 2; floatL.position.set(-1.7, -0.15, 0); g.add(floatL);
  const floatR = floatL.clone(); floatR.position.x = 1.7; g.add(floatR);
  const motor = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.45, 0.45), new THREE.MeshStandardMaterial({ color: 0xd9a13b, roughness: 0.5 }));
  motor.position.set(0, 0.35, 0); g.add(motor);
  g.userData.rotor = rotor;
  scene.add(g);
  return g;
}

export function create(canvas, options) {
  const o = Object.assign({ width: 40, length: 50, depth: 1.3, wheels: 2, sensors: 3, background: 0xe9f1ee, animals: 'shrimp' }, options || {});
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.shadowMap.enabled = true;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(o.background);
  scene.fog = new THREE.Fog(o.background, 90, 220);
  const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 500);
  camera.position.set(o.width * 0.95, o.length * 0.75, o.length * 1.05);
  const controls = new OrbitControls(camera, canvas);
  controls.enableDamping = true; controls.maxPolarAngle = Math.PI * 0.47; controls.target.set(0, -0.5, 0);

  scene.add(new THREE.HemisphereLight(0xf4fbff, 0x6b5a3a, 1.1));
  const sun = new THREE.DirectionalLight(0xffffff, 2.2);
  sun.position.set(40, 60, 25); sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048);
  Object.assign(sun.shadow.camera, { left: -60, right: 60, top: 60, bottom: -60 });
  scene.add(sun);

  // ground and dikes
  // ground with a hole where the pond is, so the water surface stays visible
  const shape = new THREE.Shape();
  shape.moveTo(-130, -130); shape.lineTo(130, -130); shape.lineTo(130, 130); shape.lineTo(-130, 130); shape.lineTo(-130, -130);
  const hole = new THREE.Path();
  hole.moveTo(-o.width / 2, -o.length / 2); hole.lineTo(-o.width / 2, o.length / 2); hole.lineTo(o.width / 2, o.length / 2); hole.lineTo(o.width / 2, -o.length / 2); hole.lineTo(-o.width / 2, -o.length / 2);
  shape.holes.push(hole);
  const ground = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshStandardMaterial({ color: 0xb9c79a, roughness: 1 }));
  ground.rotation.x = -Math.PI / 2; ground.position.y = 0.6; ground.receiveShadow = true; scene.add(ground);
  const dikeMat = new THREE.MeshStandardMaterial({ color: 0x9c8460, roughness: 0.95 });
  const dikeH = 1.8, dw = 3;
  const mk = (w, l, x, z) => { const m = new THREE.Mesh(new THREE.BoxGeometry(w, dikeH, l), dikeMat); m.position.set(x, 0.6 + dikeH / 2 - 0.9, z); m.castShadow = m.receiveShadow = true; scene.add(m); };
  mk(o.width + 2 * dw, dw, 0, -(o.length / 2 + dw / 2)); mk(o.width + 2 * dw, dw, 0, o.length / 2 + dw / 2);
  mk(dw, o.length, -(o.width / 2 + dw / 2), 0); mk(dw, o.length, o.width / 2 + dw / 2, 0);
  // pond liner (bottom)
  const liner = new THREE.Mesh(new THREE.BoxGeometry(o.width, 0.2, o.length), new THREE.MeshStandardMaterial({ color: 0x2b3236, roughness: 0.9 }));
  liner.position.y = -o.depth - 0.4; scene.add(liner);
  const walls = new THREE.Mesh(new THREE.BoxGeometry(o.width, o.depth + 1, o.length), new THREE.MeshStandardMaterial({ color: 0x23292c, side: THREE.BackSide, roughness: 0.9 }));
  walls.position.y = -o.depth / 2; scene.add(walls);

  // water
  const wGeo = new THREE.PlaneGeometry(o.width, o.length, 64, 64);
  const water = new THREE.Mesh(wGeo, new THREE.MeshPhysicalMaterial({ color: doColor(6), transparent: true, opacity: 0.72, roughness: 0.15, metalness: 0.05, clearcoat: 0.6 }));
  water.rotation.x = -Math.PI / 2; water.position.y = 0; water.receiveShadow = true; scene.add(water);
  const base = wGeo.attributes.position.array.slice();

  // aerators
  const wheels = [];
  for (let i = 0; i < o.wheels; i++) {
    const g = makeWheel(scene);
    const side = i % 2 === 0 ? -1 : 1;
    g.position.set(side * o.width * 0.28, 0.25, (i - (o.wheels - 1) / 2) * (o.length / Math.max(1, o.wheels)) * 0.9);
    g.rotation.y = side > 0 ? Math.PI : 0;
    wheels.push(g);
  }

  // sensor buoys
  const buoys = [];
  for (let i = 0; i < o.sensors; i++) {
    const b = new THREE.Group();
    const body = new THREE.Mesh(new THREE.SphereGeometry(0.45, 20, 14), new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.4 }));
    const band = new THREE.Mesh(new THREE.TorusGeometry(0.46, 0.08, 8, 24), new THREE.MeshStandardMaterial({ color: 0x12a4b6, emissive: 0x12a4b6, emissiveIntensity: 0.6 }));
    band.rotation.x = Math.PI / 2;
    const mast = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 1.2, 6), new THREE.MeshStandardMaterial({ color: 0x334048 }));
    mast.position.y = 0.8;
    const led = new THREE.Mesh(new THREE.SphereGeometry(0.12, 10, 8), new THREE.MeshStandardMaterial({ color: 0x5fe0a0, emissive: 0x5fe0a0, emissiveIntensity: 1.5 }));
    led.position.y = 1.45;
    b.add(body, band, mast, led);
    b.position.set((i - (o.sensors - 1) / 2) * o.width * 0.3, 0.1, o.length * (i % 2 ? 0.25 : -0.2));
    b.userData.led = led;
    scene.add(b); buoys.push(b);
  }

  // stock (instanced)
  const maxAnimals = 1600;
  const aGeo = o.animals === 'shrimp' ? new THREE.CapsuleGeometry(0.16, 0.7, 3, 8) : new THREE.CapsuleGeometry(0.22, 0.9, 3, 8);
  aGeo.rotateZ(Math.PI / 2);
  const animals = new THREE.InstancedMesh(aGeo, new THREE.MeshStandardMaterial({ color: o.animals === 'shrimp' ? 0xd8c3a5 : 0xa7b0a8, roughness: 0.5 }), maxAnimals);
  const seeds = [];
  for (let i = 0; i < maxAnimals; i++) seeds.push({ x: (Math.random() - 0.5) * o.width * 0.92, z: (Math.random() - 0.5) * o.length * 0.92, y: -Math.random() * o.depth * 0.6 - 0.15, a: Math.random() * 6.28, s: 0.5 + Math.random() });
  animals.count = 400;
  scene.add(animals);

  const state = { do: 6, aerator: true, animalCount: 400, speed: 1, alert: false };
  const dummy = new THREE.Object3D();
  const clock = new THREE.Clock();
  let disposed = false;

  function resize() {
    const w = canvas.clientWidth || 600, h = canvas.clientHeight || 400;
    renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix();
  }
  const ro = new ResizeObserver(resize); ro.observe(canvas); resize();

  function frame() {
    if (disposed) return;
    const t = clock.getElapsedTime();
    const pos = wGeo.attributes.position.array;
    for (let i = 0; i < pos.length; i += 3) {
      const x = base[i], y = base[i + 1];
      let h = 0.04 * Math.sin(x * 0.35 + t * 1.3) + 0.03 * Math.cos(y * 0.45 + t * 1.1);
      if (state.aerator) for (const wh of wheels) { const dx = x - wh.position.x, dz = -y - wh.position.z; const d = Math.sqrt(dx * dx + dz * dz); h += 0.12 * Math.exp(-d * 0.35) * Math.sin(d * 2.2 - t * 6); }
      pos[i + 2] = h;
    }
    wGeo.attributes.position.needsUpdate = true; wGeo.computeVertexNormals();
    for (const wh of wheels) if (state.aerator) wh.userData.rotor.rotation.x -= 0.12 * state.speed;
    for (const b of buoys) { b.position.y = 0.1 + 0.05 * Math.sin(t * 2 + b.position.x); b.userData.led.material.emissive.setHex(state.alert && Math.sin(t * 8) > 0 ? 0xff4a3a : 0x5fe0a0); }
    for (let i = 0; i < animals.count; i++) {
      const s = seeds[i]; s.a += 0.01 * s.s * (state.do < 3 ? 0.4 : 1);
      const x = s.x + Math.cos(s.a + i) * 2, z = s.z + Math.sin(s.a * 0.8 + i) * 2;
      dummy.position.set(Math.max(-o.width / 2 + 0.5, Math.min(o.width / 2 - 0.5, x)), state.do < 3 ? -0.15 : s.y, Math.max(-o.length / 2 + 0.5, Math.min(o.length / 2 - 0.5, z)));
      dummy.rotation.y = -s.a - i; dummy.updateMatrix(); animals.setMatrixAt(i, dummy.matrix);
    }
    animals.instanceMatrix.needsUpdate = true;
    controls.update();
    renderer.render(scene, camera);
    requestAnimationFrame(frame);
  }
  frame();

  const id = nextId++;
  scenes.set(id, { renderer, scene, water, state, ro, dispose: () => { disposed = true; ro.disconnect(); renderer.dispose(); } });
  return id;
}

/** state: { do, aerator, biomassRatio (0–1), alert } */
export function update(id, s) {
  const h = scenes.get(id); if (!h) return;
  Object.assign(h.state, s || {});
  h.water.material.color = doColor(h.state.do);
  if (s && s.biomassRatio !== undefined) h.scene.children.filter(c => c.isInstancedMesh).forEach(m => m.count = Math.max(30, Math.min(1600, Math.round(1600 * s.biomassRatio))));
}

export function snapshot(id) {
  const h = scenes.get(id); if (!h) return null;
  return h.renderer.domElement.toDataURL('image/png');
}

export function dispose(id) {
  const h = scenes.get(id); if (!h) return;
  h.dispose(); scenes.delete(id);
}
