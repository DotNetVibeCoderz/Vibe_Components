// OpenSceneGraph.Net.Blazor - WebGL2 renderer for OsgCanvas.
// Created by Gravicode Studios, led by Kang Fadhil.
//
// The .NET side (OsgCanvas + WebGlFrameEncoder) owns the scene graph and the viewer. Each animation frame this module
// calls RenderFrame(...) on the component, receives one binary packet (little-endian 32-bit words) and draws it with
// the same GLSL as the desktop GlRenderer (ShaderLibrary, sent from C# at start-up).
// Semantics mirror src/OpenSceneGraph.Net.Rendering.OpenGL/GlRenderer.cs.

const MAGIC = 0x3147534F;
const HEADER = 10, ENV = 48, LIGHT = 12, STATE = 20, PASS = 48, ITEM = 21;
const PASS_PRE = 0, PASS_MAIN = 1, PASS_POST = 2;
const PRIM_POINTS = 0, PRIM_LINES = 1, PRIM_LINESTRIP = 2, PRIM_TRIANGLES = 3, PRIM_TRISTRIP = 4;
const EV_PUSH = 0, EV_RELEASE = 1, EV_DRAG = 2, EV_MOVE = 3, EV_SCROLL = 4, EV_KEYDOWN = 5, EV_KEYUP = 6, EV_DOUBLECLICK = 9;
const SCROLL_KEYS = new Set([' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End']);

export function create(canvas, dotnet, shaders, options) {
    return new OsgCanvasRenderer(canvas, dotnet, shaders, options || {});
}

class GlProgram {
    constructor(gl, vs, fs, name) {
        this.gl = gl;
        this.name = name;
        const v = this.compile(gl.VERTEX_SHADER, vs);
        const f = this.compile(gl.FRAGMENT_SHADER, fs);
        const p = gl.createProgram();
        gl.attachShader(p, v);
        gl.attachShader(p, f);
        gl.linkProgram(p);
        gl.deleteShader(v);
        gl.deleteShader(f);
        if (!gl.getProgramParameter(p, gl.LINK_STATUS) && !gl.isContextLost())
            throw new Error(`Shader program '${name}' failed to link: ${gl.getProgramInfoLog(p)}`);
        this.handle = p;
        this.locs = new Map();
    }

    compile(type, src) {
        const gl = this.gl;
        const s = gl.createShader(type);
        gl.shaderSource(s, src);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS) && !gl.isContextLost())
            throw new Error(`${type === gl.VERTEX_SHADER ? 'Vertex' : 'Fragment'} shader '${this.name}' failed to compile: ${gl.getShaderInfoLog(s)}`);
        return s;
    }

    use() { this.gl.useProgram(this.handle); }

    loc(name) {
        let l = this.locs.get(name);
        if (l === undefined) { l = this.gl.getUniformLocation(this.handle, name); this.locs.set(name, l); }
        return l;
    }

    i(name, v) { const l = this.loc(name); if (l) this.gl.uniform1i(l, v); }
    f(name, v) { const l = this.loc(name); if (l) this.gl.uniform1f(l, v); }
    v2(name, x, y) { const l = this.loc(name); if (l) this.gl.uniform2f(l, x, y); }
    v3(name, a, o) { const l = this.loc(name); if (l) this.gl.uniform3f(l, a[o], a[o + 1], a[o + 2]); }
    v4(name, a, o) { const l = this.loc(name); if (l) this.gl.uniform4f(l, a[o], a[o + 1], a[o + 2], a[o + 3]); }
    v4a(name, a, count) { const l = this.loc(name); if (l && count > 0) this.gl.uniform4fv(l, a, 0, count * 4); }
    // System.Numerics matrices arrive raw (M11, M12, ...); uploading untransposed gives the column-vector GLSL equivalent.
    m4(name, a, o) { const l = this.loc(name); if (l) this.gl.uniformMatrix4fv(l, false, a, o, 16); }
    m3(name, a) { const l = this.loc(name); if (l) this.gl.uniformMatrix3fv(l, false, a); }
    dispose() { this.gl.deleteProgram(this.handle); }
}

class OsgCanvasRenderer {
    constructor(canvas, dotnet, shaders, options) {
        this.canvas = canvas;
        this.dotnet = dotnet;
        this.shaders = shaders;
        this.continuous = options.continuous !== false;
        this.maxPixelRatio = options.maxPixelRatio || 2;
        this.antialias = options.antialias !== false;
        this.disposed = false;
        this.running = false;
        this.busy = false;
        this.dirty = true;
        this.raf = 0;
        this.events = [];
        this.keys = [];
        this.keyIndex = new Map();
        this.heldKeys = new Set();
        this.buttons = 0;
        this.drawCalls = 0;
        this.renderMs = 0;
        this.lastDpr = window.devicePixelRatio || 1;
        this.frames = 0;
        this.errors = 0;
        this.resetPending = false;
        this.contextLost = false;
        this.targetW = Math.max(1, canvas.clientWidth | 0);
        this.targetH = Math.max(1, canvas.clientHeight | 0);
        this.normalMat = new Float32Array(9);
        this.mvp = new Float32Array(16);
        this.lightVec = { pos: new Float32Array(32), col: new Float32Array(32), dir: new Float32Array(32) };

        const gl = canvas.getContext('webgl2', {
            antialias: this.antialias, alpha: false, depth: true, stencil: false,
            premultipliedAlpha: false, preserveDrawingBuffer: false, powerPreference: 'high-performance',
        });
        if (!gl) throw new Error('WebGL2 is not available in this browser.');
        this.gl = gl;
        this.initResources();
        this.attachInput();
        this.measure();

        this.onLost = e => { e.preventDefault(); this.contextLost = true; };
        this.onRestored = () => {
            this.contextLost = false;
            this.initResources();
            this.resetPending = true; // .NET forgets its resource cache and re-sends everything
            this.dirty = true;
        };
        canvas.addEventListener('webglcontextlost', this.onLost, false);
        canvas.addEventListener('webglcontextrestored', this.onRestored, false);
    }

    // ------------------------------------------------------------------ setup
    initResources() {
        const gl = this.gl, s = this.shaders;
        this.floatColor = !!gl.getExtension('EXT_color_buffer_float');
        this.main = new GlProgram(gl, s.mainVs, s.mainFs, 'main');
        this.depth = new GlProgram(gl, s.depthVs, s.depthFs, 'depth');
        this.background = new GlProgram(gl, s.fullscreenVs, s.backgroundFs, 'background');
        this.bright = new GlProgram(gl, s.fullscreenVs, s.brightFs, 'bright');
        this.blur = new GlProgram(gl, s.fullscreenVs, s.blurFs, 'blur');
        this.composite = new GlProgram(gl, s.fullscreenVs, s.compositeFs, 'composite');
        this.emptyVao = gl.createVertexArray();
        this.meshes = new Map();
        this.textures = new Map();
        this.shadow = null;
        this.post = null;
        this.white = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this.white);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([255, 255, 255, 255]));
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        // default generic attribute values for missing arrays (context state, identical for every mesh)
        gl.vertexAttrib3f(1, 0, 0, 1);
        gl.vertexAttrib4f(2, 1, 1, 1, 1);
        gl.vertexAttrib2f(3, 0, 0);
    }

    getInfo() {
        const gl = this.gl;
        const dbg = gl.getExtension('WEBGL_debug_renderer_info');
        const attrs = gl.getContextAttributes() || {};
        return {
            renderer: String(dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)),
            vendor: String(dbg ? gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) : gl.getParameter(gl.VENDOR)),
            version: String(gl.getParameter(gl.VERSION)),
            shadingLanguage: String(gl.getParameter(gl.SHADING_LANGUAGE_VERSION)),
            maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE) | 0,
            floatColorBuffers: this.floatColor,
            antialias: !!attrs.antialias,
            pixelRatio: Math.min(window.devicePixelRatio || 1, this.maxPixelRatio),
        };
    }

    // ------------------------------------------------------------------ frame loop
    start() {
        if (this.running || this.disposed) return;
        this.running = true;
        this.raf = requestAnimationFrame(this.tick);
    }

    stop() {
        this.running = false;
        if (this.raf) cancelAnimationFrame(this.raf);
        this.raf = 0;
    }

    setContinuous(v) { this.continuous = !!v; this.dirty = true; }
    invalidate() { this.dirty = true; }

    tick = () => {
        if (!this.running || this.disposed) return;
        this.raf = requestAnimationFrame(this.tick);
        // zoom or moving to another monitor changes the ratio without necessarily changing the CSS size
        const dpr = window.devicePixelRatio || 1;
        if (dpr !== this.lastDpr) { this.lastDpr = dpr; this.measure(); }
        if (this.busy || this.contextLost) return;            // never overlap frames
        if (!this.continuous && !this.dirty && this.events.length === 0) return;
        this.busy = true;
        this.dirty = false;
        const events = this.events, keys = this.keys;
        this.events = []; this.keys = []; this.keyIndex.clear();
        const flags = this.resetPending ? 1 : 0;
        this.resetPending = false;
        const w = this.targetW, h = this.targetH;
        this.dotnet.invokeMethodAsync('RenderFrame', w, h, flags, this.drawCalls, this.renderMs, events, keys)
            .then(packet => {
                if (packet && !this.disposed && !this.contextLost) this.render(packet, w, h);
            })
            .catch(err => {
                this.errors++;
                if (this.errors <= 3 || this.errors % 300 === 0) console.error('[OsgCanvas] frame failed:', err);
            })
            .finally(() => { this.busy = false; });
    };

    // ------------------------------------------------------------------ input
    measure() {
        const r = this.canvas.getBoundingClientRect();
        const ratio = Math.min(window.devicePixelRatio || 1, this.maxPixelRatio);
        this.setTargetSize(r.width * ratio, r.height * ratio);
    }

    setTargetSize(w, h) {
        w = Math.max(1, Math.round(w));
        h = Math.max(1, Math.round(h));
        if (w !== this.targetW || h !== this.targetH) { this.targetW = w; this.targetH = h; this.dirty = true; }
    }

    attachInput() {
        const c = this.canvas;
        this.resizeObserver = new ResizeObserver(entries => {
            const e = entries[entries.length - 1];
            const ratio = Math.min(window.devicePixelRatio || 1, this.maxPixelRatio);
            const w = e.contentRect.width * ratio, h = e.contentRect.height * ratio;
            // prefer the exact device-pixel box, but only when it agrees with the CSS size * ratio
            const dp = e.devicePixelContentBoxSize && e.devicePixelContentBoxSize[0];
            if (dp && Math.abs(dp.inlineSize - w) <= 2 && Math.abs(dp.blockSize - h) <= 2) this.setTargetSize(dp.inlineSize, dp.blockSize);
            else this.setTargetSize(w, h);
        });
        try { this.resizeObserver.observe(c, { box: 'device-pixel-content-box' }); }
        catch { this.resizeObserver.observe(c); }

        const mods = e => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0);
        // DOM buttons: 1 left, 2 right, 4 middle -> OSG: 1 left, 2 middle, 4 right
        const buttons = b => ((b & 1) ? 1 : 0) | ((b & 4) ? 2 : 0) | ((b & 2) ? 4 : 0);
        const pos = e => {
            const r = c.getBoundingClientRect();
            const sx = r.width > 0 ? this.targetW / r.width : 1, sy = r.height > 0 ? this.targetH / r.height : 1;
            return [(e.clientX - r.left) * sx, (e.clientY - r.top) * sy];
        };

        this.handlers = {
            pointerdown: e => {
                c.focus({ preventScroll: true });
                try { c.setPointerCapture(e.pointerId); } catch { /* ignore */ }
                this.buttons = e.pointerType === 'mouse' ? buttons(e.buttons) : 1;
                const [x, y] = pos(e);
                this.push(EV_PUSH, x, y, this.buttons, 0, mods(e), null);
                e.preventDefault();
            },
            pointermove: e => {
                const [x, y] = pos(e);
                const b = e.pointerType === 'mouse' ? buttons(e.buttons) : (this.buttons ? 1 : 0);
                this.push(b ? EV_DRAG : EV_MOVE, x, y, b, 0, mods(e), null, true);
            },
            pointerup: e => {
                const [x, y] = pos(e);
                this.push(EV_RELEASE, x, y, this.buttons, 0, mods(e), null);
                this.buttons = e.pointerType === 'mouse' ? buttons(e.buttons) : 0;
                try { c.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
            },
            pointercancel: e => {
                const [x, y] = pos(e);
                this.push(EV_RELEASE, x, y, this.buttons, 0, mods(e), null);
                this.buttons = 0;
            },
            dblclick: e => {
                const [x, y] = pos(e);
                this.push(EV_DOUBLECLICK, x, y, 1, 0, mods(e), null);
            },
            wheel: e => {
                const [x, y] = pos(e);
                const unit = e.deltaMode === 1 ? 3 : e.deltaMode === 2 ? 0.1 : 100;
                const d = Math.max(-3, Math.min(3, -e.deltaY / unit));
                this.push(EV_SCROLL, x, y, 0, d, mods(e), null);
                e.preventDefault();
            },
            contextmenu: e => e.preventDefault(),
            keydown: e => {
                const k = mapKey(e.key);
                if (k === null) return;
                if (SCROLL_KEYS.has(e.key)) e.preventDefault();
                this.heldKeys.add(k);
                this.push(EV_KEYDOWN, 0, 0, 0, 0, mods(e), k);
            },
            keyup: e => {
                const k = mapKey(e.key);
                if (k === null) return;
                // shift+letter may arrive as an upper-case key on release: release both spellings
                this.heldKeys.delete(k);
                this.push(EV_KEYUP, 0, 0, 0, 0, mods(e), k);
            },
            blur: () => {
                // avoid stuck vehicle controls when focus leaves the canvas
                for (const k of this.heldKeys) this.push(EV_KEYUP, 0, 0, 0, 0, 0, k);
                this.heldKeys.clear();
            },
        };
        for (const [name, fn] of Object.entries(this.handlers))
            c.addEventListener(name, fn, name === 'wheel' || name === 'pointerdown' ? { passive: false } : false);
    }

    push(type, x, y, buttons, scroll, mods, key, coalesce) {
        let k = -1;
        if (key !== null) {
            k = this.keyIndex.get(key);
            if (k === undefined) { k = this.keys.length; this.keys.push(key); this.keyIndex.set(key, k); }
        }
        const ev = this.events, n = ev.length;
        if (coalesce && n >= 7 && ev[n - 7] === type) {
            ev[n - 6] = x; ev[n - 5] = y; ev[n - 4] = buttons; ev[n - 2] = mods;
        } else {
            ev.push(type, x, y, buttons, scroll, mods, k);
        }
        this.dirty = true;
    }

    // ------------------------------------------------------------------ packet decoding
    render(packet, w, h) {
        let buf = packet.buffer, off = packet.byteOffset;
        if (off % 4 !== 0) { const copy = new Uint8Array(packet.byteLength); copy.set(packet); buf = copy.buffer; off = 0; }
        const words = packet.byteLength >> 2;
        const i32 = new Int32Array(buf, off, words), f32 = new Float32Array(buf, off, words);
        if (words < HEADER || i32[0] !== MAGIC) return;
        const nGeo = i32[2], nTex = i32[3], nGeoEv = i32[4], nTexEv = i32[5], nStates = i32[6], nPasses = i32[7], nLights = i32[8];
        let p = HEADER;
        for (let i = 0; i < nGeo; i++) p = this.uploadMesh(i32, f32, buf, off, p);
        for (let i = 0; i < nTex; i++) p = this.uploadTexture(i32, buf, off, p);
        for (let i = 0; i < nGeoEv; i++) this.deleteMesh(i32[p++]);
        for (let i = 0; i < nTexEv; i++) this.deleteTexture(i32[p++]);
        const env = p; p += ENV;
        const lights = p; p += nLights * LIGHT;
        const states = p; p += nStates * STATE;
        const passes = [];
        for (let i = 0; i < nPasses; i++) {
            const n = i32[p + 43] + i32[p + 44];
            passes.push(p);
            p += PASS + n * ITEM;
        }
        this.frame = { i32, f32, env, lights, nLights, states, passes };

        if (this.canvas.width !== w || this.canvas.height !== h) { this.canvas.width = w; this.canvas.height = h; }
        this.drawCalls = 0;
        const t0 = performance.now();
        this.draw(w, h);
        this.renderMs = performance.now() - t0;
        this.frames++;
        this.canvas.dataset.frames = String(this.frames);
        this.frame = null;
    }

    uploadMesh(i32, f32, buf, off, p) {
        const gl = this.gl;
        const handle = i32[p], flags = i32[p + 1], n = i32[p + 2], ni = i32[p + 3];
        p += 4;
        let m = this.meshes.get(handle);
        if (!m) {
            m = { vao: gl.createVertexArray(), bufs: [null, null, null, null], ebo: null, wire: null, indices: null };
            this.meshes.set(handle, m);
        }
        const usage = (flags & 16) ? gl.DYNAMIC_DRAW : gl.STATIC_DRAW;
        gl.bindVertexArray(m.vao);
        const attr = (loc, size, present) => {
            if (!present) {
                gl.disableVertexAttribArray(loc);
                if (m.bufs[loc]) { gl.deleteBuffer(m.bufs[loc]); m.bufs[loc] = null; }
                return;
            }
            if (!m.bufs[loc]) m.bufs[loc] = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, m.bufs[loc]);
            gl.bufferData(gl.ARRAY_BUFFER, f32, usage, p, n * size);
            gl.enableVertexAttribArray(loc);
            gl.vertexAttribPointer(loc, size, gl.FLOAT, false, size * 4, 0);
            p += n * size;
        };
        attr(0, 3, true);
        attr(1, 3, (flags & 1) !== 0);
        attr(2, 4, (flags & 2) !== 0);
        attr(3, 2, (flags & 4) !== 0);
        m.hasColors = (flags & 2) !== 0;
        m.hasUv = (flags & 4) !== 0;
        m.vertexCount = n;
        if (m.wire) { gl.deleteBuffer(m.wire); m.wire = null; }
        if (ni > 0) {
            if (!m.ebo) m.ebo = gl.createBuffer();
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, m.ebo);
            const idx = new Uint32Array(buf, off + p * 4, ni);
            gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, usage);
            m.indices = idx.slice(); // kept for on-demand wireframe edges
            m.indexed = true;
            m.count = ni;
            p += ni;
        } else {
            if (m.ebo) { gl.deleteBuffer(m.ebo); m.ebo = null; }
            m.indices = null;
            m.indexed = false;
            m.count = n;
        }
        gl.bindVertexArray(null);
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
        return p;
    }

    deleteMesh(handle) {
        const m = this.meshes.get(handle);
        if (!m) return;
        const gl = this.gl;
        gl.deleteVertexArray(m.vao);
        for (const b of m.bufs) if (b) gl.deleteBuffer(b);
        if (m.ebo) gl.deleteBuffer(m.ebo);
        if (m.wire) gl.deleteBuffer(m.wire);
        this.meshes.delete(handle);
    }

    uploadTexture(i32, buf, off, p) {
        const gl = this.gl;
        const handle = i32[p], kind = i32[p + 1], w = i32[p + 2], h = i32[p + 3], wrap = i32[p + 4], filter = i32[p + 5];
        p += 6;
        this.deleteTexture(handle);
        const glWrap = wrap === 1 ? gl.CLAMP_TO_EDGE : wrap === 2 ? gl.MIRRORED_REPEAT : gl.REPEAT;
        const t = { tex: gl.createTexture(), w, h, fbo: null, depth: null };
        gl.bindTexture(gl.TEXTURE_2D, t.tex);
        if (kind === 1) {
            // render target (CameraNode.RenderTarget)
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            t.depth = gl.createRenderbuffer();
            gl.bindRenderbuffer(gl.RENDERBUFFER, t.depth);
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, h);
            t.fbo = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, t.fbo);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t.tex, 0);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, t.depth);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        } else {
            const pixels = new Uint8Array(buf, off + p * 4, w * h * 4);
            gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, glWrap);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, glWrap);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter === 0 ? gl.NEAREST : gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter === 0 ? gl.NEAREST : filter === 1 ? gl.LINEAR : gl.LINEAR_MIPMAP_LINEAR);
            if (filter === 2) gl.generateMipmap(gl.TEXTURE_2D);
            p += w * h;
        }
        this.textures.set(handle, t);
        return p;
    }

    deleteTexture(handle) {
        const t = this.textures.get(handle);
        if (!t) return;
        const gl = this.gl;
        gl.deleteTexture(t.tex);
        if (t.fbo) gl.deleteFramebuffer(t.fbo);
        if (t.depth) gl.deleteRenderbuffer(t.depth);
        this.textures.delete(handle);
    }

    // ------------------------------------------------------------------ drawing
    draw(width, height) {
        const gl = this.gl, F = this.frame, i32 = F.i32, f32 = F.f32, e = F.env;
        const envFlags = i32[e];
        const sky = (envFlags & 1) !== 0, shadows = (envFlags & 4) !== 0;
        const hdr = (envFlags & 8) !== 0, bloom = (envFlags & 16) !== 0, vignette = (envFlags & 32) !== 0;

        // 1. pre-render passes (render to texture)
        for (const p of F.passes) {
            if (i32[p] !== PASS_PRE) continue;
            const rt = this.textures.get(i32[p + 1]);
            if (!rt || !rt.fbo) continue;
            gl.bindFramebuffer(gl.FRAMEBUFFER, rt.fbo);
            gl.viewport(0, 0, rt.w, rt.h);
            if (i32[p + 2]) gl.clearColor(f32[p + 3], f32[p + 4], f32[p + 5], f32[p + 6]);
            else gl.clearColor(0, 0, 0, 1);
            gl.depthMask(true);
            gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
            this.drawPass(p, false, rt.h);
        }

        // 2. shadow map
        const main = F.passes.find(p => i32[p] === PASS_MAIN);
        const useShadow = shadows && main !== undefined;
        if (useShadow) this.renderShadowMap(main, i32[e + 1]);

        // 3. main pass (optionally into the post-processing buffer)
        const post = hdr || bloom || vignette;
        let sceneFbo = null;
        if (post) {
            if (!this.post) this.post = new PostChain(gl, this.floatColor);
            this.post.ensure(width, height);
            sceneFbo = this.post.sceneFbo;
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFbo);
        gl.viewport(0, 0, width, height);
        gl.clearColor(f32[e + 2], f32[e + 3], f32[e + 4], f32[e + 5]);
        gl.depthMask(true);
        gl.colorMask(true, true, true, true);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        if (sky) this.drawBackground(e + 6, e + 2);
        if (main !== undefined) this.drawPass(main, useShadow, height);

        // 4. post-render (HUD) passes
        for (const p of F.passes) {
            if (i32[p] !== PASS_POST || i32[p + 1] !== -1) continue;
            if (i32[p + 7]) { gl.depthMask(true); gl.clear(gl.DEPTH_BUFFER_BIT); }
            if (i32[p + 2]) { gl.clearColor(f32[p + 3], f32[p + 4], f32[p + 5], f32[p + 6]); gl.clear(gl.COLOR_BUFFER_BIT); }
            this.drawPass(p, false, height);
        }

        // 5. post-processing to the canvas
        if (post) this.post.run(this, f32, e, width, height, bloom, hdr, vignette);
    }

    drawBackground(top, bottom) {
        const gl = this.gl, f32 = this.frame.f32;
        gl.disable(gl.DEPTH_TEST);
        gl.depthMask(false);
        gl.disable(gl.BLEND);
        gl.disable(gl.CULL_FACE);
        const b = this.background;
        b.use();
        b.v4('uTop', f32, top);
        b.v4('uBottom', f32, bottom);
        gl.bindVertexArray(this.emptyVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        this.drawCalls++;
        gl.depthMask(true);
    }

    drawPass(p, useShadow, viewportH) {
        const gl = this.gl, F = this.frame, i32 = F.i32, f32 = F.f32, e = F.env;
        const hud = i32[p] === PASS_POST; // RTT passes are lit like the main pass
        gl.enable(gl.DEPTH_TEST);
        const m = this.main;
        m.use();
        m.m4('uView', f32, p + 8);
        m.m4('uProj', f32, p + 24);
        m.v3('uEye', f32, p + 40);
        m.f('uViewportH', viewportH);
        m.v3('uAmbient', f32, e + 10);
        m.v3('uEnvColor', f32, e + 13);
        m.v3('uFogColor', f32, e + 16);
        m.v2('uFogRange', f32[e + 19], f32[e + 20]);
        m.f('uShadowBias', f32[e + 21]);
        m.i('uTex', 0);
        m.i('uShadowMap', 1);
        this.uploadLights(hud ? 0 : F.nLights);
        gl.activeTexture(gl.TEXTURE1);
        if (useShadow && this.shadow) {
            m.m4('uLightSpace', f32, e + 27);
            m.f('uShadowTexel', 1 / this.shadow.size * f32[e + 22]);
            gl.bindTexture(gl.TEXTURE_2D, this.shadow.depth);
        } else {
            gl.bindTexture(gl.TEXTURE_2D, this.white);
        }
        gl.activeTexture(gl.TEXTURE0);

        const opaque = i32[p + 43], transparent = i32[p + 44];
        this.drawItems(p + PASS, opaque, useShadow, hud);
        this.drawItems(p + PASS + opaque * ITEM, transparent, useShadow, hud);
        gl.depthMask(true);
        gl.disable(gl.BLEND);
    }

    uploadLights(count) {
        const F = this.frame, f32 = F.f32, m = this.main, L = this.lightVec;
        m.i('uLightCount', count);
        if (count === 0) return;
        for (let i = 0; i < count; i++) {
            const o = F.lights + i * LIGHT;
            for (let k = 0; k < 4; k++) {
                L.pos[i * 4 + k] = f32[o + k];
                L.col[i * 4 + k] = f32[o + 4 + k];
                L.dir[i * 4 + k] = f32[o + 8 + k];
            }
        }
        m.v4a('uLightPos', L.pos, count);
        m.v4a('uLightColor', L.col, count);
        m.v4a('uLightDir', L.dir, count);
    }

    drawItems(start, count, useShadow, hud) {
        const gl = this.gl, F = this.frame, i32 = F.i32, f32 = F.f32;
        const m = this.main, nm = this.normalMat;
        let lastState = -1, polygonMode = 0;
        for (let k = 0; k < count; k++) {
            const o = start + k * ITEM;
            const mesh = this.meshes.get(i32[o]);
            if (!mesh || mesh.count === 0) continue;
            const si = i32[o + 1];
            if (si !== lastState) {
                polygonMode = this.applyState(si, useShadow, hud);
                lastState = si;
            }
            const prim = i32[o + 2];
            m.m4('uModel', f32, o + 5);
            normalMatrix(f32, o + 5, nm);
            m.m3('uNormalMat', nm);
            const points = prim === PRIM_POINTS || polygonMode === 2;
            m.i('uHasVertexColor', mesh.hasColors ? 1 : 0);
            m.i('uIsPoints', points ? 1 : 0);
            m.i('uPointMode', points && mesh.hasUv ? 1 : 0);

            gl.bindVertexArray(mesh.vao);
            if (polygonMode === 1 && (prim === PRIM_TRIANGLES || prim === PRIM_TRISTRIP)) {
                // PolygonMode.Line: WebGL has no glPolygonMode, so draw the triangle edges as lines
                const wire = this.wireBuffer(mesh, prim);
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, wire.buffer);
                gl.drawElements(gl.LINES, wire.count, gl.UNSIGNED_INT, 0);
                if (mesh.ebo) gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, mesh.ebo);
                this.drawCalls++;
                continue;
            }
            const first = Math.max(0, i32[o + 3]);
            const dc = i32[o + 4];
            const n = dc >= 0 ? Math.min(dc, mesh.count - first) : mesh.count - first;
            if (n <= 0) continue;
            const mode = polygonMode === 2 ? gl.POINTS : glMode(gl, prim);
            if (mesh.indexed) gl.drawElements(mode, n, gl.UNSIGNED_INT, first * 4);
            else gl.drawArrays(mode, first, n);
            this.drawCalls++;
        }
        gl.bindVertexArray(null);
    }

    wireBuffer(mesh, prim) {
        if (mesh.wire) return { buffer: mesh.wire, count: mesh.wireCount };
        const gl = this.gl;
        const idx = mesh.indices, n = idx ? idx.length : mesh.vertexCount;
        const at = i => idx ? idx[i] : i;
        const tris = prim === PRIM_TRIANGLES ? Math.floor(n / 3) : Math.max(0, n - 2);
        const lines = new Uint32Array(tris * 6);
        let w = 0;
        for (let t = 0; t < tris; t++) {
            const a = prim === PRIM_TRIANGLES ? at(t * 3) : at(t);
            const b = prim === PRIM_TRIANGLES ? at(t * 3 + 1) : at(t + 1);
            const c = prim === PRIM_TRIANGLES ? at(t * 3 + 2) : at(t + 2);
            lines[w++] = a; lines[w++] = b; lines[w++] = b; lines[w++] = c; lines[w++] = c; lines[w++] = a;
        }
        gl.bindVertexArray(mesh.vao);
        mesh.wire = gl.createBuffer();
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, mesh.wire);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, lines, gl.STATIC_DRAW);
        mesh.wireCount = w;
        return { buffer: mesh.wire, count: w };
    }

    /** Applies a state-table entry; returns its polygon mode (0 fill, 1 line, 2 point). */
    applyState(si, useShadow, hud) {
        const gl = this.gl, F = this.frame, i32 = F.i32, f32 = F.f32, e = F.env;
        const s = F.states + si * STATE;
        const flags = i32[s + 16];
        if (flags & (1 << 5)) gl.enable(gl.DEPTH_TEST); else gl.disable(gl.DEPTH_TEST);
        gl.depthMask((flags & (1 << 6)) !== 0);
        switch ((flags >> 1) & 3) {
            case 0: gl.disable(gl.CULL_FACE); break;
            case 1: gl.enable(gl.CULL_FACE); gl.cullFace(gl.BACK); break;
            case 2: gl.enable(gl.CULL_FACE); gl.cullFace(gl.FRONT); break;
        }
        switch ((flags >> 3) & 3) {
            case 0: gl.disable(gl.BLEND); break;
            case 1: gl.enable(gl.BLEND); gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA); break;
            case 2: gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE); break;
            case 3: gl.enable(gl.BLEND); gl.blendFunc(gl.DST_COLOR, gl.ZERO); break;
        }
        const texHandle = i32[s + 17];
        const tex = texHandle >= 0 ? this.textures.get(texHandle) : undefined;
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, tex ? tex.tex : this.white);

        const m = this.main;
        m.v4('uDiffuse', f32, s);
        m.v3('uMatAmbient', f32, s + 4);
        m.v3('uSpecular', f32, s + 7);
        m.v3('uEmission', f32, s + 10);
        m.f('uShininess', f32[s + 13]);
        m.f('uReflectivity', f32[s + 14]);
        m.f('uPointSize', f32[s + 15]);
        m.i('uLighting', flags & 1);
        m.i('uHasTexture', tex ? 1 : 0);
        m.i('uFog', (i32[e] & 2) && (flags & (1 << 7)) && !hud ? 1 : 0);
        m.i('uShadow', useShadow && (flags & (1 << 8)) ? 1 : 0);
        return (flags >> 10) & 3;
    }

    renderShadowMap(main, size) {
        const gl = this.gl, F = this.frame, i32 = F.i32, f32 = F.f32, e = F.env;
        if (!this.shadow || this.shadow.size !== size) {
            if (this.shadow) this.shadow.dispose();
            this.shadow = new ShadowMap(gl, size);
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.shadow.fbo);
        gl.viewport(0, 0, size, size);
        gl.depthMask(true);
        gl.clear(gl.DEPTH_BUFFER_BIT);
        gl.enable(gl.DEPTH_TEST);
        gl.disable(gl.BLEND);
        gl.enable(gl.CULL_FACE);
        gl.cullFace(gl.FRONT); // reduces acne on lit faces
        const d = this.depth, mvp = this.mvp;
        d.use();
        const opaque = i32[main + 43];
        for (let k = 0; k < opaque; k++) {
            const o = main + PASS + k * ITEM;
            const prim = i32[o + 2];
            if (prim === PRIM_POINTS || prim === PRIM_LINES || prim === PRIM_LINESTRIP) continue;
            const s = F.states + i32[o + 1] * STATE;
            if ((i32[s + 16] & (1 << 9)) === 0) continue;
            const mesh = this.meshes.get(i32[o]);
            if (!mesh || mesh.count === 0) continue;
            mul4(f32, o + 5, f32, e + 27, mvp); // World * LightSpace (row-vector convention)
            d.m4('uLightMvp', mvp, 0);
            gl.bindVertexArray(mesh.vao);
            const mode = glMode(gl, prim);
            if (mesh.indexed) gl.drawElements(mode, mesh.count, gl.UNSIGNED_INT, 0);
            else gl.drawArrays(mode, 0, mesh.count);
            this.drawCalls++;
        }
        gl.cullFace(gl.BACK);
        gl.bindVertexArray(null);
    }

    drawFullscreen() {
        const gl = this.gl;
        gl.bindVertexArray(this.emptyVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        this.drawCalls++;
    }

    // ------------------------------------------------------------------ teardown
    dispose() {
        if (this.disposed) return;
        this.stop();
        this.disposed = true;
        const c = this.canvas;
        if (this.resizeObserver) this.resizeObserver.disconnect();
        if (this.handlers) for (const [name, fn] of Object.entries(this.handlers)) c.removeEventListener(name, fn);
        c.removeEventListener('webglcontextlost', this.onLost);
        c.removeEventListener('webglcontextrestored', this.onRestored);
        const gl = this.gl;
        if (!gl.isContextLost()) {
            for (const h of [...this.meshes.keys()]) this.deleteMesh(h);
            for (const h of [...this.textures.keys()]) this.deleteTexture(h);
            if (this.shadow) this.shadow.dispose();
            if (this.post) this.post.dispose();
            for (const p of [this.main, this.depth, this.background, this.bright, this.blur, this.composite]) p.dispose();
            gl.deleteVertexArray(this.emptyVao);
            gl.deleteTexture(this.white);
            // free the context right away instead of waiting for GC (browsers cap live WebGL contexts)
            const lose = gl.getExtension('WEBGL_lose_context');
            if (lose) lose.loseContext();
        }
        this.dotnet = null;
    }
}

/** Depth-only framebuffer for directional shadow mapping (DEPTH_COMPONENT24 texture). */
class ShadowMap {
    constructor(gl, size) {
        this.gl = gl;
        this.size = size;
        this.depth = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this.depth);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, size, size, 0, gl.DEPTH_COMPONENT, gl.UNSIGNED_INT, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        this.fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, this.depth, 0);
        gl.drawBuffers([gl.NONE]);
        gl.readBuffer(gl.NONE);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    dispose() {
        this.gl.deleteFramebuffer(this.fbo);
        this.gl.deleteTexture(this.depth);
    }
}

/** HDR scene buffer + half-resolution bloom ping-pong buffers (RGBA16F with EXT_color_buffer_float, else RGBA8). */
class PostChain {
    constructor(gl, floatColor) {
        this.gl = gl;
        this.float = floatColor;
        this.w = 0; this.h = 0;
        this.sceneFbo = null; this.sceneTex = null; this.sceneDepth = null;
        this.bloomFbo = [null, null]; this.bloomTex = [null, null];
    }

    ensure(w, h) {
        if (w === this.w && h === this.h && this.sceneFbo) return;
        this.release();
        const gl = this.gl;
        this.w = w; this.h = h;
        this.sceneFbo = gl.createFramebuffer();
        this.sceneTex = this.colorTexture(w, h);
        this.sceneDepth = gl.createRenderbuffer();
        gl.bindRenderbuffer(gl.RENDERBUFFER, this.sceneDepth);
        gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, h);
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.sceneFbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.sceneTex, 0);
        gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, this.sceneDepth);
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE && this.float) {
            this.float = false; // float colour buffers unsupported: retry with RGBA8
            this.release();
            this.ensure(w, h);
            return;
        }
        const bw = Math.max(1, w >> 1), bh = Math.max(1, h >> 1);
        for (let i = 0; i < 2; i++) {
            this.bloomFbo[i] = gl.createFramebuffer();
            this.bloomTex[i] = this.colorTexture(bw, bh);
            gl.bindFramebuffer(gl.FRAMEBUFFER, this.bloomFbo[i]);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.bloomTex[i], 0);
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    colorTexture(w, h) {
        const gl = this.gl;
        const t = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, t);
        if (this.float) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, w, h, 0, gl.RGBA, gl.HALF_FLOAT, null);
        else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        return t;
    }

    run(r, f32, e, w, h, bloom, hdr, vignette) {
        const gl = this.gl;
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.BLEND);
        gl.disable(gl.CULL_FACE);
        gl.depthMask(false);
        const bw = Math.max(1, w >> 1), bh = Math.max(1, h >> 1);
        if (bloom) {
            gl.viewport(0, 0, bw, bh);
            gl.bindFramebuffer(gl.FRAMEBUFFER, this.bloomFbo[0]);
            const bright = r.bright;
            bright.use();
            bright.i('uScene', 0);
            bright.f('uThreshold', f32[e + 24]);
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, this.sceneTex);
            r.drawFullscreen();
            const blur = r.blur;
            blur.use();
            blur.i('uSrc', 0);
            for (let i = 0; i < 4; i++) {
                const src = i % 2, dst = 1 - src;
                gl.bindFramebuffer(gl.FRAMEBUFFER, this.bloomFbo[dst]);
                gl.bindTexture(gl.TEXTURE_2D, this.bloomTex[src]);
                if (i % 2 === 0) blur.v2('uDir', 1 / bw, 0); else blur.v2('uDir', 0, 1 / bh);
                r.drawFullscreen();
            }
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, w, h);
        const c = r.composite;
        c.use();
        c.i('uScene', 0);
        c.i('uBloom', 1);
        c.i('uUseBloom', bloom ? 1 : 0);
        c.f('uBloomIntensity', f32[e + 25]);
        c.i('uHdr', hdr ? 1 : 0);
        c.f('uExposure', f32[e + 23]);
        c.i('uVignette', vignette ? 1 : 0);
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, this.bloomTex[0]);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, this.sceneTex);
        r.drawFullscreen();
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, null);
        gl.activeTexture(gl.TEXTURE0);
        gl.depthMask(true);
    }

    release() {
        const gl = this.gl;
        if (this.sceneFbo) gl.deleteFramebuffer(this.sceneFbo);
        if (this.sceneTex) gl.deleteTexture(this.sceneTex);
        if (this.sceneDepth) gl.deleteRenderbuffer(this.sceneDepth);
        for (let i = 0; i < 2; i++) {
            if (this.bloomFbo[i]) gl.deleteFramebuffer(this.bloomFbo[i]);
            if (this.bloomTex[i]) gl.deleteTexture(this.bloomTex[i]);
            this.bloomFbo[i] = this.bloomTex[i] = null;
        }
        this.sceneFbo = this.sceneTex = this.sceneDepth = null;
    }

    dispose() { this.release(); }
}

// ---------------------------------------------------------------------- helpers
function glMode(gl, prim) {
    switch (prim) {
        case PRIM_POINTS: return gl.POINTS;
        case PRIM_LINES: return gl.LINES;
        case PRIM_LINESTRIP: return gl.LINE_STRIP;
        case PRIM_TRISTRIP: return gl.TRIANGLE_STRIP;
        default: return gl.TRIANGLES;
    }
}

/**
 * Normal matrix for a System.Numerics world matrix stored at a[o..o+15]: rows of transpose(inverse(upper 3x3)),
 * which equal the cofactor rows (b x c, c x a, a x b) / det - the same values GlRenderer uploads.
 */
function normalMatrix(a, o, out) {
    const a0 = a[o], a1 = a[o + 1], a2 = a[o + 2];
    const b0 = a[o + 4], b1 = a[o + 5], b2 = a[o + 6];
    const c0 = a[o + 8], c1 = a[o + 9], c2 = a[o + 10];
    const r00 = b1 * c2 - b2 * c1, r01 = b2 * c0 - b0 * c2, r02 = b0 * c1 - b1 * c0;
    const r10 = c1 * a2 - c2 * a1, r11 = c2 * a0 - c0 * a2, r12 = c0 * a1 - c1 * a0;
    const r20 = a1 * b2 - a2 * b1, r21 = a2 * b0 - a0 * b2, r22 = a0 * b1 - a1 * b0;
    let det = a0 * r00 + a1 * r01 + a2 * r02;
    det = Math.abs(det) < 1e-30 ? 1 : 1 / det;
    out[0] = r00 * det; out[1] = r01 * det; out[2] = r02 * det;
    out[3] = r10 * det; out[4] = r11 * det; out[5] = r12 * det;
    out[6] = r20 * det; out[7] = r21 * det; out[8] = r22 * det;
}

/** out = A * B for row-major System.Numerics matrices. */
function mul4(a, ao, b, bo, out) {
    for (let r = 0; r < 4; r++) {
        const x = a[ao + r * 4], y = a[ao + r * 4 + 1], z = a[ao + r * 4 + 2], w = a[ao + r * 4 + 3];
        for (let c = 0; c < 4; c++)
            out[r * 4 + c] = x * b[bo + c] + y * b[bo + 4 + c] + z * b[bo + 8 + c] + w * b[bo + 12 + c];
    }
}

/** DOM key names as used by the manipulators: lower-case characters, " ", "ArrowUp", "Shift", ... */
function mapKey(key) {
    if (!key || key === 'Unidentified' || key === 'Dead' || key === 'Tab') return null;
    if (key.length === 1) return key.toLowerCase();
    return key;
}
