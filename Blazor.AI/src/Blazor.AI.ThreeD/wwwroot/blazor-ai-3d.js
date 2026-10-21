// Blazor.AI.ThreeD scene engine. C# sends a declarative list of objects; this module diffs it by id,
// renders with Three.js, and reports picks, model metadata and speech events back to .NET.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
import * as THREE from './lib/three/three.module.min.js';
import { OrbitControls } from './lib/three/addons/controls/OrbitControls.js';
import { GLTFLoader } from './lib/three/addons/loaders/GLTFLoader.js';
import { DRACOLoader } from './lib/three/addons/loaders/DRACOLoader.js';
import { CSS2DRenderer, CSS2DObject } from './lib/three/addons/renderers/CSS2DRenderer.js';
import { RoomEnvironment } from './lib/three/addons/environments/RoomEnvironment.js';
import { SimplifyModifier } from './lib/three/addons/modifiers/SimplifyModifier.js';
import * as SkeletonUtils from './lib/three/addons/utils/SkeletonUtils.js';
import { mergeVertices } from './lib/three/addons/utils/BufferGeometryUtils.js';

const scenes = new Map();
let nextId = 1;

const draco = new DRACOLoader();
draco.setDecoderPath(new URL('./lib/three/draco/', import.meta.url).href);
const gltfLoader = new GLTFLoader();
gltfLoader.setDRACOLoader(draco);
const modelCache = new Map();

function loadModel(url) {
    if (!modelCache.has(url)) {
        modelCache.set(url, new Promise((resolve, reject) => gltfLoader.load(url, resolve, undefined, reject)));
    }
    return modelCache.get(url);
}

const v3 = (a, fallback = [0, 0, 0]) => new THREE.Vector3(...(a ?? fallback));
const col = c => new THREE.Color(c ?? '#cfd6ff');
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

// ---------------------------------------------------------------- scene lifecycle

const parse = v => typeof v === 'string' ? JSON.parse(v) : v;

export function create(host, dotnet, options) {
    const id = nextId++;
    const o = parse(options) ?? {};
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: !o.background, preserveDrawingBuffer: !!o.preserveDrawingBuffer });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = o.exposure ?? 1.0;
    renderer.shadowMap.enabled = !!o.shadows;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    host.appendChild(renderer.domElement);
    renderer.domElement.setAttribute('role', 'img');
    renderer.domElement.setAttribute('aria-label', o.ariaLabel ?? '3D scene');

    const labels = new CSS2DRenderer();
    labels.domElement.className = 'bai3d-labels';
    host.appendChild(labels.domElement);

    const tip = document.createElement('div');
    tip.className = 'bai3d-tooltip';
    tip.hidden = true;
    host.appendChild(tip);

    const scene = new THREE.Scene();
    if (o.background) scene.background = col(o.background);
    if (o.fog) scene.fog = new THREE.Fog(col(o.fog.color ?? o.background ?? '#0b0f29'), o.fog.near ?? 10, o.fog.far ?? 60);
    if (o.environment !== false) {
        const pmrem = new THREE.PMREMGenerator(renderer);
        scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
        pmrem.dispose();
    }

    const camera = new THREE.PerspectiveCamera(o.camera?.fov ?? 45, 1, 0.05, 2000);
    camera.position.copy(v3(o.camera?.position, [6, 5, 8]));
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.copy(v3(o.camera?.target));
    controls.enableDamping = true;
    controls.enabled = o.controls?.enabled ?? true;
    controls.autoRotate = !!o.controls?.autoRotate && !reducedMotion();
    controls.autoRotateSpeed = o.controls?.autoRotateSpeed ?? 0.8;
    if (o.controls?.minDistance) controls.minDistance = o.controls.minDistance;
    if (o.controls?.maxDistance) controls.maxDistance = o.controls.maxDistance;
    controls.maxPolarAngle = o.controls?.maxPolarAngle ?? Math.PI * 0.495;
    controls.update();

    addLights(scene, o.lights ?? 'studio', !!o.shadows);
    if (o.grid) {
        const g = new THREE.GridHelper(o.grid.size ?? 20, o.grid.divisions ?? 20, col(o.grid.color ?? '#5b6bd6'), col(o.grid.color ?? '#5b6bd6'));
        g.material.transparent = true;
        g.material.opacity = o.grid.opacity ?? 0.25;
        scene.add(g);
    }
    if (o.ground) {
        const ground = new THREE.Mesh(new THREE.CircleGeometry(o.ground.size ?? 30, 64), new THREE.MeshStandardMaterial({ color: col(o.ground.color ?? '#10143a'), roughness: 1, metalness: 0, envMapIntensity: 0.2 }));
        ground.rotation.x = -Math.PI / 2;
        ground.receiveShadow = true;
        scene.add(ground);
    }

    const state = {
        id, host, dotnet, renderer, labels, tip, scene, camera, controls,
        objects: new Map(), clock: new THREE.Clock(), mixers: new Set(), tweens: [], flight: null,
        visible: true, disposed: false, speech: null, raf: 0,
    };

    const resize = () => {
        const w = Math.max(1, host.clientWidth), h = Math.max(1, host.clientHeight);
        renderer.setSize(w, h, false);
        renderer.domElement.style.width = '100%';
        renderer.domElement.style.height = '100%';
        labels.setSize(w, h);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    };
    state.resizeObserver = new ResizeObserver(resize);
    state.resizeObserver.observe(host);
    state.intersection = new IntersectionObserver(e => { state.visible = e[0]?.isIntersecting ?? true; });
    state.intersection.observe(host);
    resize();

    wirePointer(state);
    scenes.set(id, state);
    const loop = () => {
        if (state.disposed) return;
        state.raf = requestAnimationFrame(loop);
        if ((!state.visible || document.hidden) && !state.dirty) return;
        state.dirty = false;
        tick(state);
    };
    loop();
    return id;
}

function addLights(scene, preset, shadows) {
    const presets = {
        studio: { hemi: ['#dfe6ff', '#2a2f55', 0.9], sun: ['#ffffff', 2.2, [5, 9, 6]], rim: ['#d9964f', 0.8, [-6, 4, -5]] },
        day: { hemi: ['#cfe8ff', '#6b5a3a', 1.1], sun: ['#fff4dc', 3.0, [8, 12, 4]], rim: ['#9fd8ff', 0.4, [-6, 6, -6]] },
        night: { hemi: ['#3a4a9a', '#0b0f29', 0.6], sun: ['#9fb3ff', 0.9, [4, 10, 6]], rim: ['#3cc4b1', 1.2, [-6, 3, -4]] },
        soft: { hemi: ['#ffffff', '#c9cfe8', 1.4], sun: ['#ffffff', 1.2, [3, 8, 5]], rim: ['#ffd9a8', 0.4, [-5, 3, -5]] },
    };
    const p = presets[preset] ?? presets.studio;
    scene.add(new THREE.HemisphereLight(col(p.hemi[0]), col(p.hemi[1]), p.hemi[2]));
    const sun = new THREE.DirectionalLight(col(p.sun[0]), p.sun[1]);
    sun.position.copy(v3(p.sun[2]));
    if (shadows) {
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        Object.assign(sun.shadow.camera, { left: -15, right: 15, top: 15, bottom: -15, near: 0.5, far: 60 });
        sun.shadow.bias = -0.0005;
    }
    scene.add(sun);
    const rim = new THREE.DirectionalLight(col(p.rim[0]), p.rim[1]);
    rim.position.copy(v3(p.rim[2]));
    scene.add(rim);
}

export function dispose(id) {
    const s = scenes.get(id);
    if (!s) return;
    s.disposed = true;
    cancelAnimationFrame(s.raf);
    s.resizeObserver.disconnect();
    s.intersection.disconnect();
    if (s.speech) speechSynthesis?.cancel();
    for (const entry of s.objects.values()) disposeObject(entry.object);
    s.controls.dispose();
    s.renderer.dispose();
    s.host.replaceChildren();
    scenes.delete(id);
}

export function setOptions(id, o) {
    o = parse(o);
    const s = scenes.get(id);
    if (!s || !o) return;
    if (o.background !== undefined) s.scene.background = o.background ? col(o.background) : null;
    if (o.autoRotate !== undefined) s.controls.autoRotate = o.autoRotate && !reducedMotion();
    if (o.exposure !== undefined) s.renderer.toneMappingExposure = o.exposure;
}

// ---------------------------------------------------------------- objects

export function setObjects(id, specs) {
    const s = scenes.get(id);
    if (!s) return;
    specs = parse(specs);
    const seen = new Set();
    // Parents first so children can attach.
    const ordered = [...(specs ?? [])].sort((a, b) => (a.parent ? 1 : 0) - (b.parent ? 1 : 0));
    const pending = [];
    for (const spec of ordered) {
        seen.add(spec.id);
        const existing = s.objects.get(spec.id);
        const key = geometryKey(spec);
        if (existing && existing.key === key) {
            applyCommon(s, existing.object, spec, existing);
            existing.spec = spec;
            continue;
        }
        if (existing) removeEntry(s, spec.id);
        const entry = { spec, key, object: null, base: null };
        s.objects.set(spec.id, entry);
        pending.push(build(s, entry).catch(err => console.error('[bai3d] build failed', spec.id, err?.stack ?? err)));
    }
    for (const [oid] of [...s.objects]) {
        if (!seen.has(oid)) removeEntry(s, oid);
    }
    s.dirty = true;
    for (const p of pending) p.then(() => { s.dirty = true; });
    // Models stream in; primitives are already in the scene when this returns.
    return pending.length;
}

export function updateObjects(id, patches, duration) {
    const s = scenes.get(id);
    if (!s) return;
    patches = parse(patches);
    for (const p of patches ?? []) {
        const e = s.objects.get(p.id);
        if (!e?.object) continue;
        const ms = reducedMotion() ? 0 : (duration ?? 0);
        if (p.position) tween(s, e.object.position, v3(p.position), ms);
        if (p.scale !== undefined && p.scale !== null) tween(s, e.object.scale, Array.isArray(p.scale) ? v3(p.scale) : new THREE.Vector3(p.scale, p.scale, p.scale), ms);
        if (p.rotation) e.object.rotation.set(...p.rotation);
        if (p.color || p.emissive || p.opacity !== undefined) {
            e.object.traverse(m => {
                if (!m.material || m.userData.keepMaterial) return;
                if (p.color && m.material.color) m.material.color.set(p.color);
                if (p.emissive && m.material.emissive) m.material.emissive.set(p.emissive);
                if (p.emissiveIntensity !== undefined && m.material.emissive) m.material.emissiveIntensity = p.emissiveIntensity;
                if (p.opacity !== undefined) { m.material.transparent = p.opacity < 1; m.material.opacity = p.opacity; }
            });
        }
        if (p.visible !== undefined) e.object.visible = p.visible;
        if (p.label !== undefined) setLabel(e, p.label, e.spec.labelClass);
        if (p.clip) playClipOn(e, p.clip, p.clipLoop ?? true, p.clipSpeed);
        else if (p.clipSpeed !== undefined && p.clipSpeed !== null && e.action) e.action.timeScale = p.clipSpeed;
        Object.assign(e.spec, p);
    }
    s.dirty = true;
}

function geometryKey(spec) {
    const { position, rotation, scale, color, emissive, emissiveIntensity, opacity, visible, label, tooltip, animate, clip, clipLoop, clipSpeed, ...rest } = spec;
    return JSON.stringify(rest);
}

async function build(s, entry) {
    const spec = entry.spec;
    let obj;
    if (spec.kind === 'model') {
        const group = new THREE.Group();
        entry.object = group;
        attach(s, entry, group);
        applyCommon(s, group, spec, entry);
        try {
            const gltf = await loadModel(spec.url);
            if (!s.objects.has(spec.id) || s.objects.get(spec.id) !== entry) return;
            const model = SkeletonUtils.clone(gltf.scene);
            model.traverse(m => {
                if (m.isMesh) {
                    m.castShadow = spec.castShadow ?? true;
                    m.receiveShadow = true;
                    if (spec.color && m.material && !spec.keepModelColors) {
                        m.material = m.material.clone();
                        m.material.color?.set(spec.color);
                    }
                }
            });
            if (spec.fitHeight || spec.fitSize) {
                const box = new THREE.Box3().setFromObject(model);
                const size = box.getSize(new THREE.Vector3());
                const f = spec.fitHeight ? spec.fitHeight / (size.y || 1) : spec.fitSize / Math.max(size.x, size.y, size.z, 1e-6);
                model.scale.multiplyScalar(f);
                const box2 = new THREE.Box3().setFromObject(model);
                const c = box2.getCenter(new THREE.Vector3());
                model.position.x -= c.x; model.position.z -= c.z; model.position.y -= box2.min.y;
            }
            group.add(model);
            entry.model = model;
            if (spec.color && !spec.keepModelColors) entry.tint = spec.color;
            entry.morphs = [];
            model.traverse(m => { if (m.morphTargetDictionary) entry.morphs.push(m); });
            if (gltf.animations?.length) {
                entry.mixer = new THREE.AnimationMixer(model);
                entry.clips = gltf.animations;
                s.mixers.add(entry.mixer);
                const first = spec.clip ?? (gltf.animations.find(a => /idle/i.test(a.name))?.name ?? gltf.animations[0].name);
                playClipOn(entry, first, spec.clipLoop ?? true, spec.clipSpeed);
            }
            applyCommon(s, group, spec, entry);
            s.dirty = true;
            const morphNames = [...new Set(entry.morphs.flatMap(m => Object.keys(m.morphTargetDictionary)))];
            let triangles = 0;
            model.traverse(m => { if (m.isMesh) triangles += (m.geometry.index ? m.geometry.index.count : m.geometry.attributes.position.count) / 3; });
            s.dotnet?.invokeMethodAsync('OnModelLoaded', spec.id, (gltf.animations ?? []).map(a => a.name), morphNames, Math.round(triangles)).catch(() => { });
        } catch (err) {
            console.warn('Blazor.AI.ThreeD: could not load model', spec.url, err);
            const fallback = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.5, 0.5), new THREE.MeshStandardMaterial({ color: '#c2414b', wireframe: true }));
            group.add(fallback);
        }
        return;
    }

    obj = createPrimitive(spec);
    entry.object = obj;
    attach(s, entry, obj);
    applyCommon(s, obj, spec, entry);
}

function attach(s, entry, obj) {
    obj.userData.baiId = entry.spec.id;
    const parent = entry.spec.parent ? s.objects.get(entry.spec.parent)?.object : null;
    (parent ?? s.scene).add(obj);
}

function material(spec) {
    const common = {
        color: col(spec.color),
        transparent: (spec.opacity ?? 1) < 1,
        opacity: spec.opacity ?? 1,
        wireframe: !!spec.wireframe,
        side: spec.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
        vertexColors: !!spec.colors,
        flatShading: !!spec.flatShading,
    };
    if (spec.unlit) return new THREE.MeshBasicMaterial(common);
    return new THREE.MeshStandardMaterial({
        ...common,
        metalness: spec.metalness ?? 0.1,
        roughness: spec.roughness ?? 0.55,
        emissive: col(spec.emissive ?? '#000000'),
        emissiveIntensity: spec.emissiveIntensity ?? 1,
    });
}

function createPrimitive(spec) {
    const seg = spec.segments ?? 32;
    const size = spec.size ?? [1, 1, 1];
    let geometry;
    switch (spec.kind) {
        case 'group': return new THREE.Group();
        case 'box': geometry = new THREE.BoxGeometry(size[0], size[1], size[2]); break;
        case 'sphere': geometry = new THREE.SphereGeometry(spec.radius ?? 0.5, seg, Math.max(8, seg / 2)); break;
        case 'cylinder': geometry = new THREE.CylinderGeometry(spec.radiusTop ?? spec.radius ?? 0.5, spec.radiusBottom ?? spec.radius ?? 0.5, spec.height ?? 1, seg); break;
        case 'cone': geometry = new THREE.ConeGeometry(spec.radius ?? 0.5, spec.height ?? 1, seg); break;
        case 'torus': geometry = new THREE.TorusGeometry(spec.radius ?? 1, spec.tube ?? 0.05, 16, seg * 2); break;
        case 'icosahedron': geometry = new THREE.IcosahedronGeometry(spec.radius ?? 0.5, spec.detail ?? 0); break;
        case 'capsule': geometry = new THREE.CapsuleGeometry(spec.radius ?? 0.3, spec.height ?? 1, 8, seg); break;
        case 'plane': geometry = new THREE.PlaneGeometry(size[0], size[1]); break;
        case 'ring': geometry = new THREE.RingGeometry(spec.innerRadius ?? 0.8, spec.radius ?? 1, seg * 2); break;
        case 'text': {
            const g = new THREE.Group();
            return g; // label only
        }
        case 'line': {
            const pts = (spec.points ?? []).map(p => v3(p));
            const g = new THREE.BufferGeometry().setFromPoints(pts);
            const m = spec.dashed
                ? new THREE.LineDashedMaterial({ color: col(spec.color), dashSize: spec.dashSize ?? 0.2, gapSize: spec.gapSize ?? 0.12, transparent: true, opacity: spec.opacity ?? 0.9 })
                : new THREE.LineBasicMaterial({ color: col(spec.color), transparent: true, opacity: spec.opacity ?? 0.9, vertexColors: !!spec.colors });
            if (spec.colors) g.setAttribute('color', new THREE.Float32BufferAttribute(spec.colors.flatMap(c => col(c).toArray()), 3));
            const line = new THREE.Line(g, m);
            if (spec.dashed) line.computeLineDistances();
            line.userData.keepMaterial = !!spec.colors;
            return line;
        }
        case 'tube': {
            const curve = new THREE.CatmullRomCurve3((spec.points ?? [[0, 0, 0], [1, 1, 1]]).map(p => v3(p)), !!spec.closed);
            geometry = new THREE.TubeGeometry(curve, spec.tubularSegments ?? 48, spec.radius ?? 0.03, 8, !!spec.closed);
            break;
        }
        case 'points': {
            const g = new THREE.BufferGeometry();
            g.setAttribute('position', new THREE.Float32BufferAttribute((spec.points ?? []).flat(), 3));
            if (spec.colors) g.setAttribute('color', new THREE.Float32BufferAttribute(spec.colors.flatMap(c => col(c).toArray()), 3));
            const m = new THREE.PointsMaterial({ size: spec.pointSize ?? 0.08, sizeAttenuation: true, color: spec.colors ? 0xffffff : col(spec.color), vertexColors: !!spec.colors, transparent: true, opacity: spec.opacity ?? 0.95 });
            return new THREE.Points(g, m);
        }
        case 'arrow': {
            const from = v3(spec.from), to = v3(spec.to);
            const dir = to.clone().sub(from);
            const len = dir.length();
            const arrow = new THREE.ArrowHelper(dir.normalize(), from, len, col(spec.color), Math.min(0.35, len * 0.25), Math.min(0.18, len * 0.12));
            return arrow;
        }
        case 'surface': {
            const rows = spec.rows, cols = spec.cols;
            const w = size[0], d = size[2] ?? size[1];
            geometry = new THREE.PlaneGeometry(w, d, cols - 1, rows - 1);
            geometry.rotateX(-Math.PI / 2);
            const pos = geometry.attributes.position;
            for (let i = 0; i < pos.count; i++) pos.setY(i, (spec.values?.[i] ?? 0));
            geometry.computeVertexNormals();
            break;
        }
        default: geometry = new THREE.BoxGeometry(1, 1, 1);
    }
    if (spec.colors && geometry) geometry.setAttribute('color', new THREE.Float32BufferAttribute(spec.colors.flatMap(c => col(c).toArray()), 3));
    const mesh = new THREE.Mesh(geometry, material(spec));
    mesh.castShadow = spec.castShadow ?? true;
    mesh.receiveShadow = spec.receiveShadow ?? true;
    if (spec.edges) {
        const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 30), new THREE.LineBasicMaterial({ color: col(spec.edges), transparent: true, opacity: 0.6 }));
        edges.userData.keepMaterial = true;
        mesh.add(edges);
    }
    return mesh;
}

function applyCommon(s, obj, spec, entry) {
    if (spec.position) obj.position.copy(v3(spec.position));
    if (spec.rotation) obj.rotation.set(...spec.rotation);
    if (spec.scale !== undefined && spec.scale !== null) {
        if (Array.isArray(spec.scale)) obj.scale.copy(v3(spec.scale)); else obj.scale.setScalar(spec.scale);
    }
    obj.visible = spec.visible ?? true;
    if (spec.kind !== 'model' && obj.material && !obj.userData.keepMaterial) {
        if (spec.color && obj.material.color && !spec.colors) obj.material.color.set(spec.color);
        if (obj.material.emissive) {
            obj.material.emissive.set(spec.emissive ?? '#000000');
            obj.material.emissiveIntensity = spec.emissiveIntensity ?? 1;
        }
        const op = spec.opacity ?? 1;
        obj.material.transparent = op < 1;
        obj.material.opacity = op;
    }
    // Animation base comes from the spec (or the previous base), never from a mid-animation pose.
    const prev = entry.base;
    entry.base = {
        position: spec.position ? v3(spec.position) : (prev?.position ?? obj.position.clone()),
        rotation: spec.rotation ? new THREE.Euler(...spec.rotation) : (prev?.rotation ?? obj.rotation.clone()),
        scale: spec.scale !== undefined && spec.scale !== null
            ? (Array.isArray(spec.scale) ? v3(spec.scale) : new THREE.Vector3(spec.scale, spec.scale, spec.scale))
            : (prev?.scale ?? new THREE.Vector3(1, 1, 1)),
    };
    if (!spec.animate) {
        // Animation removed: settle back to the base pose.
        obj.position.copy(entry.base.position);
        obj.scale.copy(entry.base.scale);
    }
    const animKey = JSON.stringify(spec.animate ?? null);
    if (animKey !== entry.animKey) {
        entry.curve = null; // path points or motion type changed
        entry.animKey = animKey;
        entry.t0 = performance.now() / 1000 - (spec.animate?.phase ?? 0);
    }
    entry.animate = reducedMotion() ? null : spec.animate ?? null;
    setLabel(entry, spec.label, spec.labelClass);
    obj.userData.tooltip = spec.tooltip ?? null;
    obj.userData.pickable = spec.pickable ?? true;
    if (spec.kind === 'model' && entry.model && (spec.opacity ?? 1) !== (entry.modelOpacity ?? 1)) {
        entry.modelOpacity = spec.opacity ?? 1;
        entry.model.traverse(m => {
            if (!m.isMesh || !m.material) return;
            if (!m.userData.ownMaterial) { m.material = m.material.clone(); m.userData.ownMaterial = true; }
            m.material.transparent = entry.modelOpacity < 1;
            m.material.opacity = entry.modelOpacity;
            m.material.depthWrite = entry.modelOpacity >= 1;
        });
    }
    if (spec.kind === 'model' && entry.model && spec.color && spec.color !== entry.tint && !spec.keepModelColors) {
        entry.tint = spec.color;
        entry.model.traverse(m => {
            if (!m.isMesh || !m.material?.color) return;
            if (!m.userData.ownMaterial) { m.material = m.material.clone(); m.userData.ownMaterial = true; }
            m.material.color.set(spec.color);
        });
    }
    if (entry.mixer && spec.clip && spec.clip !== entry.currentClip) playClipOn(entry, spec.clip, spec.clipLoop ?? true, spec.clipSpeed);
}

function setLabel(entry, text, cls) {
    if (entry.labelObject) {
        entry.labelObject.removeFromParent();
        entry.labelObject = null;
    }
    if (!text || !entry.object) return;
    const div = document.createElement('div');
    div.className = 'bai3d-label' + (cls ? ' ' + cls : '');
    div.textContent = text;
    const label = new CSS2DObject(div);
    const box = new THREE.Box3().setFromObject(entry.object);
    const h = entry.spec.labelOffset ?? (isFinite(box.max.y) ? Math.max(0.2, (box.max.y - entry.object.position.y) / Math.max(entry.object.scale.y, 1e-6) + 0.15) : 0.4);
    label.position.set(0, h, 0);
    entry.object.add(label);
    entry.labelObject = label;
}

function removeEntry(s, oid) {
    const e = s.objects.get(oid);
    if (!e) return;
    if (e.mixer) s.mixers.delete(e.mixer);
    if (e.object) {
        e.object.removeFromParent();
        disposeObject(e.object);
    }
    s.objects.delete(oid);
}

function disposeObject(obj) {
    obj.traverse(m => {
        if (m.isCSS2DObject) m.element?.remove();
        m.geometry?.dispose();
        if (m.material) (Array.isArray(m.material) ? m.material : [m.material]).forEach(x => x.dispose?.());
    });
}

// ---------------------------------------------------------------- animation

function playClipOn(entry, name, loop = true, speed) {
    if (!entry.mixer || !entry.clips) return;
    const clip = entry.clips.find(c => c.name === name) ?? entry.clips.find(c => c.name.toLowerCase() === String(name).toLowerCase());
    if (!clip) return;
    const action = entry.mixer.clipAction(clip);
    action.reset();
    action.setLoop(loop ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
    action.clampWhenFinished = !loop;
    action.timeScale = speed ?? 1;
    if (entry.action && entry.action !== action) action.crossFadeFrom(entry.action, 0.35, false);
    action.play();
    entry.action = action;
    entry.currentClip = clip.name;
    if (!loop) {
        const back = entry.spec.clip ?? entry.clips.find(c => /idle/i.test(c.name))?.name;
        const onDone = ev => {
            if (ev.action !== action) return;
            entry.mixer.removeEventListener('finished', onDone);
            if (back && back !== clip.name) playClipOn(entry, back, true);
        };
        entry.mixer.addEventListener('finished', onDone);
    }
}

export function playClip(id, objectId, clip, loop) {
    const e = scenes.get(id)?.objects.get(objectId);
    if (e) playClipOn(e, clip, loop ?? true);
}

export function setMorph(id, objectId, name, value) {
    const e = scenes.get(id)?.objects.get(objectId);
    if (!e?.morphs) return;
    e.morphTargets = e.morphTargets ?? {};
    e.morphTargets[name] = value;
}

function tween(s, target, to, ms) {
    if (!ms) { target.copy(to); return; }
    s.tweens = s.tweens.filter(t => t.target !== target);
    s.tweens.push({ target, from: target.clone(), to, start: performance.now(), ms });
}

const ease = t => t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;

function tick(s) {
    const dt = Math.min(0.1, s.clock.getDelta());
    const now = performance.now();
    const t = now / 1000;
    for (const m of s.mixers) m.update(dt);
    s.tweens = s.tweens.filter(tw => {
        const k = Math.min(1, (now - tw.start) / tw.ms);
        tw.target.copy(tw.from).lerp(tw.to, ease(k));
        return k < 1;
    });
    if (s.flight) {
        const f = s.flight, k = Math.min(1, (now - f.start) / f.ms), e = ease(k);
        s.camera.position.copy(f.fromPos).lerp(f.toPos, e);
        s.controls.target.copy(f.fromTarget).lerp(f.toTarget, e);
        if (k >= 1) s.flight = null;
    }
    for (const e of s.objects.values()) {
        animateEntry(e, t, dt);
        if (e.morphs?.length) {
            const speaking = s.speech?.objectId === e.spec.id;
            const talkTarget = speaking ? s.speech.level : 0;
            for (const m of e.morphs) {
                const dict = m.morphTargetDictionary;
                for (const [name, idx] of Object.entries(dict)) {
                    let target = e.morphTargets?.[name] ?? 0;
                    if (/talk|open|jaw/i.test(name) && speaking) target = Math.max(target, talkTarget);
                    m.morphTargetInfluences[idx] += (target - m.morphTargetInfluences[idx]) * Math.min(1, dt * 18);
                }
            }
        }
    }
    if (s.speech) s.speech.level *= Math.pow(0.02, dt);
    s.controls.update();
    s.renderer.render(s.scene, s.camera);
    s.labels.render(s.scene, s.camera);
}

function animateEntry(e, t, dt) {
    const a = e.animate;
    if (!a || !e.object || !e.base) return;
    const o = e.object, b = e.base, k = t - e.t0, sp = a.speed ?? 1;
    switch (a.type) {
        case 'spin': {
            const axis = a.axis ?? 'y';
            o.rotation[axis] = b.rotation[axis] + k * sp;
            break;
        }
        case 'bob': o.position.y = b.position.y + Math.sin(k * sp * 2) * (a.amplitude ?? 0.1); break;
        case 'pulse': o.scale.copy(b.scale).multiplyScalar(1 + Math.sin(k * sp * 3) * (a.amplitude ?? 0.08)); break;
        case 'orbit': {
            const r = a.radius ?? 2, c = v3(a.center), ang = k * sp + (a.phase ?? 0);
            o.position.set(c.x + Math.cos(ang) * r, c.y + Math.sin(ang) * r * Math.sin(a.tilt ?? 0), c.z + Math.sin(ang) * r * Math.cos(a.tilt ?? 0));
            if (a.selfSpin) o.rotation.y = k * a.selfSpin;
            break;
        }
        case 'path': {
            if (!a.points?.length) break;
            if (!e.curve) e.curve = new THREE.CatmullRomCurve3(a.points.map(p => v3(p)), a.loop ?? true);
            const len = e.curve.getLength() || 1;
            let u = (k * sp) / len;
            u = (a.loop ?? true) ? u % 1 : Math.min(1, u);
            const p = e.curve.getPointAt(u);
            o.position.copy(p);
            if (a.face ?? true) {
                const ahead = e.curve.getPointAt((a.loop ?? true) ? (u + 0.01) % 1 : Math.min(1, u + 0.01));
                if (ahead.distanceToSquared(p) > 1e-8) {
                    const yaw = Math.atan2(ahead.x - p.x, ahead.z - p.z) + (a.yawOffset ?? 0);
                    o.rotation.y = yaw;
                }
            }
            break;
        }
        case 'wander': {
            const r = a.radius ?? 2, c = b.position;
            const x = Math.sin(k * sp * 0.7 + (a.phase ?? 0)) * r, z = Math.sin(k * sp * 0.43 + 1.3 + (a.phase ?? 0)) * r * 0.8;
            const nx = c.x + x, nz = c.z + z;
            const yaw = Math.atan2(nx - o.position.x, nz - o.position.z) + (a.yawOffset ?? 0);
            if (isFinite(yaw) && (Math.abs(nx - o.position.x) + Math.abs(nz - o.position.z)) > 1e-5) o.rotation.y = yaw;
            o.position.x = nx; o.position.z = nz;
            o.position.y = c.y + Math.sin(k * sp * 1.7) * (a.amplitude ?? 0);
            break;
        }
    }
}

// ---------------------------------------------------------------- camera, picking, utilities

export function flyTo(id, position, target, ms) {
    const s = scenes.get(id);
    if (!s) return;
    const duration = reducedMotion() ? 1 : (ms ?? 1200);
    s.flight = { fromPos: s.camera.position.clone(), toPos: v3(position), fromTarget: s.controls.target.clone(), toTarget: v3(target), start: performance.now(), ms: duration };
}

export function focusObject(id, objectId, distance) {
    const s = scenes.get(id);
    const e = s?.objects.get(objectId);
    if (!e?.object) return;
    const box = new THREE.Box3().setFromObject(e.object);
    const c = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3()).length() || 1;
    const dir = s.camera.position.clone().sub(s.controls.target).normalize();
    flyTo(id, c.clone().add(dir.multiplyScalar(distance ?? size * 1.6)).toArray(), c.toArray());
}

export function screenshot(id) {
    const s = scenes.get(id);
    if (!s) return null;
    s.renderer.render(s.scene, s.camera);
    return s.renderer.domElement.toDataURL('image/png');
}

export function stats(id) {
    const s = scenes.get(id);
    if (!s) return null;
    const i = s.renderer.info;
    return { triangles: i.render.triangles, calls: i.render.calls, geometries: i.memory.geometries, textures: i.memory.textures, objects: s.objects.size };
}

function wirePointer(s) {
    const ray = new THREE.Raycaster();
    const ndc = new THREE.Vector2();
    let down = null;
    const hit = ev => {
        const r = s.renderer.domElement.getBoundingClientRect();
        ndc.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
        ray.setFromCamera(ndc, s.camera);
        ray.params.Points.threshold = 0.08;
        for (const h of ray.intersectObjects(s.scene.children, true)) {
            let o = h.object;
            while (o && !o.userData.baiId) o = o.parent;
            if (o && o.userData.pickable !== false && o.visible) return { o, point: h.point };
        }
        return null;
    };
    const el = s.renderer.domElement;
    el.addEventListener('pointerdown', ev => { down = [ev.clientX, ev.clientY]; });
    el.addEventListener('pointerup', ev => {
        if (!down || Math.hypot(ev.clientX - down[0], ev.clientY - down[1]) > 5) return;
        const h = hit(ev);
        if (h) s.dotnet?.invokeMethodAsync('OnPick', h.o.userData.baiId, h.point.toArray()).catch(() => { });
    });
    el.addEventListener('pointermove', ev => {
        if (ev.buttons) { s.tip.hidden = true; return; }
        const h = hit(ev);
        const text = h?.o.userData.tooltip;
        el.style.cursor = h ? 'pointer' : '';
        if (!text) { s.tip.hidden = true; return; }
        const r = s.host.getBoundingClientRect();
        s.tip.textContent = text;
        s.tip.style.left = `${ev.clientX - r.left + 14}px`;
        s.tip.style.top = `${ev.clientY - r.top + 10}px`;
        s.tip.hidden = false;
    });
    el.addEventListener('pointerleave', () => { s.tip.hidden = true; });
}

// ---------------------------------------------------------------- speech with lip sync

export function voices() {
    return (window.speechSynthesis?.getVoices() ?? []).map(v => ({ name: v.name, lang: v.lang }));
}

export function speak(id, objectId, text, lang, rate, voiceName) {
    const s = scenes.get(id);
    if (!s) return;
    stopSpeaking(id);
    const state = { objectId, level: 0, timer: 0 };
    s.speech = state;
    const done = () => {
        if (s.speech !== state) return;
        clearInterval(state.timer);
        s.speech = null;
        s.dotnet?.invokeMethodAsync('OnSpeechEnded', objectId).catch(() => { });
    };
    // Syllable-rate pulses keep the mouth moving even when the browser reports no word boundaries.
    state.timer = setInterval(() => { state.level = 0.55 + Math.random() * 0.45; }, 110);
    const synth = window.speechSynthesis;
    if (!synth || !text) {
        setTimeout(done, Math.min(15000, 55 * (text?.length ?? 0) + 400));
        return;
    }
    const u = new SpeechSynthesisUtterance(text);
    if (lang) u.lang = lang;
    u.rate = rate ?? 1;
    const voice = voiceName ? synth.getVoices().find(v => v.name === voiceName) : synth.getVoices().find(v => lang && v.lang?.toLowerCase().startsWith(lang.toLowerCase().slice(0, 2)));
    if (voice) u.voice = voice;
    u.onboundary = () => { state.level = 1; };
    u.onend = done;
    u.onerror = done;
    synth.speak(u);
    // Some engines (headless browsers) never fire onend; finish by an estimate.
    setTimeout(done, Math.min(60000, 90 * text.length + 3000));
}

export function stopSpeaking(id) {
    const s = scenes.get(id);
    if (!s?.speech) return;
    clearInterval(s.speech.timer);
    s.speech = null;
    window.speechSynthesis?.cancel();
}

// ---------------------------------------------------------------- mesh optimisation

export function simplify(id, objectId, ratio) {
    const e = scenes.get(id)?.objects.get(objectId);
    if (!e?.object) return null;
    const modifier = new SimplifyModifier();
    let before = 0, after = 0;
    e.object.traverse(m => {
        if (!m.isMesh || m.isSkinnedMesh) return;
        if (!m.userData.originalGeometry) m.userData.originalGeometry = m.geometry;
        const original = m.userData.originalGeometry;
        const tris = (original.index ? original.index.count : original.attributes.position.count) / 3;
        before += tris;
        if (ratio >= 0.999) {
            if (m.geometry !== original) m.geometry.dispose();
            m.geometry = original;
            after += tris;
            return;
        }
        // Normals are recomputed afterwards; UVs are kept so textured models stay textured.
        const merged = mergeVertices(original.clone().deleteAttribute('normal'));
        const vertices = merged.attributes.position.count;
        const simplified = modifier.modify(merged, Math.floor(vertices * (1 - Math.max(0.02, ratio))));
        simplified.computeVertexNormals();
        if (m.geometry !== original) m.geometry.dispose();
        m.geometry = simplified;
        after += (simplified.index ? simplified.index.count : simplified.attributes.position.count) / 3;
    });
    return { before: Math.round(before), after: Math.round(after) };
}

export function setWireframe(id, objectId, on) {
    const e = scenes.get(id)?.objects.get(objectId);
    e?.object?.traverse(m => { if (m.material) (Array.isArray(m.material) ? m.material : [m.material]).forEach(x => { x.wireframe = on; }); });
}
