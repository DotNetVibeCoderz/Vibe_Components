// Medical.Net Gallery — 3D volume (three.js r170). Made by Gravicode Studios, led by Kang Fadhil.
// Meshes come from /mesh/{name}.bin: int32 vertexCount, int32 indexCount, float32 positions[3n], float32 normals[3n], uint32 indices[m].
import * as THREE from './three/three.module.min.js';
import { OrbitControls } from './three/OrbitControls.js';

const views = new Map();
let nextId = 1;

async function loadMesh(url) {
  const buf = await (await fetch(url)).arrayBuffer();
  const dv = new DataView(buf);
  const nv = dv.getInt32(0, true), ni = dv.getInt32(4, true);
  let o = 8;
  const pos = new Float32Array(buf, o, nv * 3); o += nv * 12;
  const nrm = new Float32Array(buf, o, nv * 3); o += nv * 12;
  const idx = new Uint32Array(buf, o, ni);
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
  g.setIndex(new THREE.BufferAttribute(idx, 1));
  return g;
}

/** layers: [{ url, color, opacity }]; radius: scene radius in mm. Returns a view id. */
export async function create(host, layers, radius, background) {
  if (host.dataset.viewId) return Number(host.dataset.viewId);   // already created (still mode or a re-render)
  const id = nextId++;
  host.dataset.viewId = String(id);
  const width = host.clientWidth || 640, height = host.clientHeight || 420;
  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.setSize(width, height);
  host.appendChild(renderer.domElement);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(background || '#eef3f4');
  scene.add(new THREE.HemisphereLight(0xffffff, 0x8899aa, 1.1));
  const key = new THREE.DirectionalLight(0xffffff, 1.6); key.position.set(1.5, 2, 2.5); scene.add(key);
  const rim = new THREE.DirectionalLight(0xfff0e8, 0.7); rim.position.set(-2, 1, -2); scene.add(rim);
  const camera = new THREE.PerspectiveCamera(35, width / height, radius / 50, radius * 20);
  camera.position.set(radius * 1.05, radius * 0.55, radius * 1.65);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.autoRotate = true;
  controls.autoRotateSpeed = 1.2;
  // opaque layers first, translucent last, so the lesion shows through the skull
  const sorted = [...layers].sort((a, b) => (b.opacity ?? 1) - (a.opacity ?? 1));
  for (const l of sorted) {
    const geo = await loadMesh(l.url);
    const translucent = (l.opacity ?? 1) < 1;
    const mat = new THREE.MeshStandardMaterial({
      color: l.color, roughness: 0.55, metalness: 0.05, transparent: translucent, opacity: l.opacity ?? 1,
      depthWrite: !translucent, side: translucent ? THREE.FrontSide : THREE.DoubleSide,
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.renderOrder = translucent ? 2 : 1;
    scene.add(mesh);
  }
  let alive = true;
  if (new URLSearchParams(location.search).has('still')) {
    // Documentation screenshots: render one frame and swap the canvas for an image the headless browser always captures.
    renderer.render(scene, camera);
    const img = new Image();
    img.src = renderer.domElement.toDataURL('image/png');
    img.style.width = '100%'; img.style.height = '100%'; img.style.display = 'block';
    renderer.domElement.replaceWith(img);
    host.dataset.ready = '1';
    alive = false;
  }
  const tick = () => { if (!alive) return; controls.update(); renderer.render(scene, camera); requestAnimationFrame(tick); };
  tick();
  const ro = new ResizeObserver(() => {
    const w = host.clientWidth, h = host.clientHeight;
    if (w > 0 && h > 0) { renderer.setSize(w, h); camera.aspect = w / h; camera.updateProjectionMatrix(); }
  });
  ro.observe(host);
  views.set(id, { renderer, controls, ro, stop: () => { alive = false; } });
  return id;
}

export function setRotate(id, on) { const v = views.get(id); if (v) v.controls.autoRotate = on; }

export function dispose(id) {
  const v = views.get(id);
  if (!v) return;
  v.stop(); v.ro.disconnect(); v.controls.dispose(); v.renderer.dispose();
  v.renderer.domElement.remove();
  views.delete(id);
}

// Still mode (?still=1, documentation screenshots): build the view from the prerendered markup without waiting for the
// Blazor circuit, so headless browsers with virtual time can capture it.
if (new URLSearchParams(location.search).has('still')) {
  const start = () => document.querySelectorAll('.viewport3d[data-layers]').forEach(h =>
    create(h, JSON.parse(h.dataset.layers), Number(h.dataset.radius), h.dataset.background));
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
}
