// Blazor.AI.MediaPipe camera + overlay module. Captures frames from a camera, video, image or stream, sends them to
// .NET one at a time (backpressure: the next frame is captured only after the previous result arrives), and draws the
// returned overlay. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

const viewers = new Map();
let nextId = 1;

const lower = v => String(v ?? '').toLowerCase();
const parse = v => typeof v === 'string' ? JSON.parse(v) : v;

export function create(host, dotnet) {
    const id = nextId++;
    host.classList.add('baiv-stage');
    const video = document.createElement('video');
    video.playsInline = true;
    video.muted = true;
    video.className = 'baiv-media';
    const img = document.createElement('img');
    img.className = 'baiv-media';
    img.crossOrigin = 'anonymous';
    img.hidden = true;
    const overlay = document.createElement('canvas');
    overlay.className = 'baiv-overlay';
    host.append(video, img, overlay);
    const capture = document.createElement('canvas');
    const v = { id, host, dotnet, video, img, overlay, capture, running: false, opts: {}, frameIndex: 0, lastOverlay: null, lastBitmap: null, stream: null, loopTimer: 0, recorder: null, chunks: [] };
    v.resize = new ResizeObserver(() => draw(v));
    v.resize.observe(host);
    viewers.set(id, v);
    return id;
}

export async function listCameras() {
    try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        return devices.filter(d => d.kind === 'videoinput').map((d, i) => ({ id: d.deviceId, label: d.label || `Camera ${i + 1}` }));
    } catch { return []; }
}

export async function start(id, options) {
    const v = viewers.get(id);
    if (!v) return 'gone';
    stop(id);
    const o = parse(options) ?? {};
    v.opts = o;
    v.frameIndex = 0;
    v.lastOverlay = null;
    const kind = lower(o.source);
    v.host.classList.toggle('is-mirrored', !!o.mirror);
    try {
        if (kind === 'camera') {
            if (!navigator.mediaDevices?.getUserMedia) return 'Camera access needs HTTPS (or localhost) and a browser with getUserMedia.';
            const constraints = { video: o.deviceId ? { deviceId: { exact: o.deviceId } } : { facingMode: o.facingMode || 'user', width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false };
            v.stream = await navigator.mediaDevices.getUserMedia(constraints);
            showVideo(v);
            v.video.srcObject = v.stream;
            await v.video.play();
        } else if (kind === 'video' || (kind === 'stream' && /\.m3u8|\.mp4|\.webm/i.test(o.url ?? ''))) {
            showVideo(v);
            v.video.srcObject = null;
            v.video.crossOrigin = 'anonymous';
            v.video.src = o.url;
            v.video.loop = o.loop !== false;
            await v.video.play();
        } else if (kind === 'stream') {
            // MJPEG over HTTP renders in an <img>; frames are read from it (needs CORS for capture).
            showImage(v);
            v.img.src = o.url;
            await waitImage(v.img);
        } else {
            showImage(v);
            v.img.src = o.url;
            await waitImage(v.img);
        }
    } catch (e) {
        return (e && e.name === 'NotAllowedError') ? 'Camera permission was denied. Allow it in the browser and try again.' : (e?.message ?? String(e));
    }
    v.running = true;
    v.isStill = kind === 'image';
    v.t0 = performance.now();
    loop(v);
    return 'ok';
}

function showVideo(v) { v.video.hidden = false; v.img.hidden = true; }
function showImage(v) { v.video.hidden = true; v.img.hidden = false; v.video.pause?.(); }
function waitImage(img) {
    return img.complete && img.naturalWidth ? Promise.resolve() : new Promise((res, rej) => { img.onload = () => res(); img.onerror = () => rej(new Error('Could not load the image.')); });
}

export function stop(id) {
    const v = viewers.get(id);
    if (!v) return;
    v.running = false;
    clearTimeout(v.loopTimer);
    v.stream?.getTracks().forEach(t => t.stop());
    v.stream = null;
    if (!v.video.hidden) { v.video.pause(); }
}

/** Re-processes the current still image (e.g. after a setting changed). */
export function reprocess(id) {
    const v = viewers.get(id);
    if (v && v.isStill) { v.running = true; loop(v); }
}

function source(v) {
    if (!v.video.hidden && v.video.readyState >= 2) return [v.video, v.video.videoWidth, v.video.videoHeight];
    if (!v.img.hidden && v.img.complete && v.img.naturalWidth) return [v.img, v.img.naturalWidth, v.img.naturalHeight];
    return [null, 0, 0];
}

async function loop(v) {
    if (!v.running) return;
    const started = performance.now();
    const [src, sw, sh] = source(v);
    if (!src) { v.loopTimer = setTimeout(() => loop(v), 100); return; }
    const scale = Math.min(1, (v.opts.maxWidth || 640) / sw);
    const w = Math.round(sw * scale), h = Math.round(sh * scale);
    v.capture.width = w; v.capture.height = h;
    const ctx = v.capture.getContext('2d', { willReadFrequently: false });
    try {
        ctx.drawImage(src, 0, 0, w, h);
        const blob = await new Promise(res => v.capture.toBlob(res, 'image/jpeg', v.opts.quality || 0.72));
        const bytes = new Uint8Array(await blob.arrayBuffer());
        const bitmap = await createImageBitmap(v.capture);
        const ts = Math.round(v.isStill ? v.frameIndex : (!v.video.hidden ? v.video.currentTime * 1000 + (v.loopCount ?? 0) : performance.now() - v.t0));
        const json = await v.dotnet.invokeMethodAsync('OnFrame', bytes, w, h, ts, !v.isStill, v.frameIndex++);
        v.lastBitmap?.close?.();
        v.lastBitmap = bitmap;
        v.lastOverlay = json ? JSON.parse(json) : null;
        draw(v);
    } catch (e) {
        if (String(e).includes('tainted') || String(e).includes('insecure')) {
            v.running = false;
            v.dotnet.invokeMethodAsync('OnSourceError', 'This stream does not allow frame capture (missing CORS headers). Use a proxy that adds Access-Control-Allow-Origin.').catch(() => { });
            return;
        }
        if (!viewers.has(v.id)) return;
        console.warn('Blazor.AI.MediaPipe frame', e);
    }
    if (v.isStill) { v.running = false; return; }
    if (!v.video.hidden && v.video.ended && !v.opts.loop) { v.running = false; v.dotnet.invokeMethodAsync('OnSourceEnded').catch(() => { }); return; }
    const interval = 1000 / Math.max(1, v.opts.fps || 12);
    const wait = Math.max(0, interval - (performance.now() - started));
    v.loopTimer = setTimeout(() => loop(v), wait);
}

// Draws the last overlay to fit the stage (object-fit: contain), mirrored when the stage is.
function draw(v) {
    const c = v.overlay, host = v.host;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const cw = host.clientWidth, ch = host.clientHeight;
    if (!cw || !ch) return;
    c.width = Math.round(cw * dpr); c.height = Math.round(ch * dpr);
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, cw, ch);
    const [, sw, sh] = source(v);
    if (!sw) return;
    const s = Math.min(cw / sw, ch / sh);
    const dw = sw * s, dh = sh * s, ox = (cw - dw) / 2, oy = (ch - dh) / 2;
    const o = v.lastOverlay;
    if (!o) return;
    const mirror = host.classList.contains('is-mirrored');
    const X = x => mirror ? ox + (1 - x) * dw : ox + x * dw;
    const Y = y => oy + y * dh;

    if (o.mask) drawMask(v, g, o.mask, ox, oy, dw, dh, mirror);
    if (o.heat) drawHeat(g, o.heat, ox, oy, dw, dh, mirror);

    for (const p of o.paths ?? []) {
        const pts = p.points; if (!pts || pts.length < 4) continue;
        g.beginPath();
        g.moveTo(X(pts[0]), Y(pts[1]));
        for (let i = 2; i < pts.length; i += 2) g.lineTo(X(pts[i]), Y(pts[i + 1]));
        if (p.closed) g.closePath();
        if (p.fill) { g.fillStyle = p.fill; g.fill(); }
        g.strokeStyle = p.color; g.lineWidth = p.width ?? 3; g.lineJoin = 'round'; g.lineCap = 'round'; g.stroke();
    }

    for (const lm of o.landmarks ?? []) {
        const pts = lm.points; const n = pts.length / 2;
        if (lm.connections && lm.lineWidth > 0) {
            g.strokeStyle = lm.color; g.lineWidth = lm.lineWidth; g.lineCap = 'round';
            g.beginPath();
            for (let i = 0; i < lm.connections.length; i += 2) {
                const a = lm.connections[i], b = lm.connections[i + 1];
                if (a >= n || b >= n) continue;
                g.moveTo(X(pts[2 * a]), Y(pts[2 * a + 1]));
                g.lineTo(X(pts[2 * b]), Y(pts[2 * b + 1]));
            }
            g.stroke();
        }
        if (lm.radius > 0) {
            g.fillStyle = lm.pointColor ?? lm.color;
            for (let i = 0; i < n; i++) {
                g.beginPath(); g.arc(X(pts[2 * i]), Y(pts[2 * i + 1]), lm.radius, 0, Math.PI * 2); g.fill();
            }
        }
        for (const i of lm.highlight ?? []) {
            if (i >= n) continue;
            g.fillStyle = lm.highlightColor ?? '#d9964f';
            g.beginPath(); g.arc(X(pts[2 * i]), Y(pts[2 * i + 1]), Math.max(5, lm.radius * 2.2), 0, Math.PI * 2); g.fill();
        }
    }

    for (const b of o.boxes ?? []) {
        const x1 = X(mirror ? b.x + b.w : b.x), y1 = Y(b.y), w = b.w * dw, h = b.h * dh;
        g.setLineDash(b.dashed ? [6, 4] : []);
        if (b.fill) { g.fillStyle = hexA(b.color, 0.18); g.fillRect(x1, y1, w, h); }
        g.strokeStyle = b.color; g.lineWidth = 2.5; g.strokeRect(x1, y1, w, h);
        g.setLineDash([]);
        if (b.label) tag(g, b.label, x1, Math.max(0, y1 - 22), b.color);
    }

    for (const t of o.texts ?? []) {
        g.font = `600 ${t.size ?? 14}px var(--bai-font-body, system-ui), system-ui, sans-serif`;
        const x = X(t.x), y = Y(t.y);
        if (t.background !== false) { tag(g, t.text, x, y, 'rgba(11,15,41,0.78)', t.color, t.size); }
        else { g.fillStyle = t.color; g.fillText(t.text, x, y); }
    }

    if (o.status) {
        g.font = '600 13px system-ui, sans-serif';
        tag(g, o.status, 10, 10, 'rgba(11,15,41,0.78)', '#ffffff');
    }
}

function tag(g, text, x, y, bg, fg = '#0b0f29', size = 13) {
    g.font = `600 ${size}px system-ui, sans-serif`;
    const w = g.measureText(text).width + 12, h = size + 9;
    g.fillStyle = bg;
    roundRect(g, x, y, w, h, 6); g.fill();
    g.fillStyle = fg === '#0b0f29' && /^#/.test(bg) ? '#0b0f29' : fg;
    g.textBaseline = 'middle';
    g.fillText(text, x + 6, y + h / 2 + 0.5);
    g.textBaseline = 'alphabetic';
}

function roundRect(g, x, y, w, h, r) {
    g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r); g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath();
}

function hexA(hex, a) {
    const m = /^#?([0-9a-f]{6})$/i.exec(hex ?? ''); if (!m) return hex;
    const n = parseInt(m[1], 16); return `rgba(${n >> 16},${(n >> 8) & 255},${n & 255},${a})`;
}

const bgCache = new Map();
function background(url) {
    if (!url) return null;
    if (!bgCache.has(url)) { const im = new Image(); im.crossOrigin = 'anonymous'; im.src = url; bgCache.set(url, im); }
    const im = bgCache.get(url); return im.complete && im.naturalWidth ? im : null;
}

function maskCanvas(m) {
    const bin = atob(m.data);
    const c = document.createElement('canvas'); c.width = m.width; c.height = m.height;
    const g = c.getContext('2d'); const id = g.createImageData(m.width, m.height);
    for (let i = 0; i < bin.length; i++) { const a = bin.charCodeAt(i); id.data[4 * i] = 255; id.data[4 * i + 1] = 255; id.data[4 * i + 2] = 255; id.data[4 * i + 3] = a; }
    g.putImageData(id, 0, 0); return c;
}

function drawMask(v, g, m, ox, oy, dw, dh, mirror) {
    const mc = maskCanvas(m);
    const layer = document.createElement('canvas'); layer.width = Math.round(dw); layer.height = Math.round(dh);
    const lg = layer.getContext('2d');
    lg.imageSmoothingEnabled = true; lg.imageSmoothingQuality = 'high';
    const mode = lower(m.mode);
    const frame = v.lastBitmap;
    if (mode === 'tint') {
        lg.drawImage(mc, 0, 0, layer.width, layer.height);
        lg.globalCompositeOperation = 'source-in';
        lg.fillStyle = m.color; lg.globalAlpha = m.opacity ?? 0.55; lg.fillRect(0, 0, layer.width, layer.height);
    } else {
        // Subject from the processed frame, kept by the mask.
        const subject = document.createElement('canvas'); subject.width = layer.width; subject.height = layer.height;
        const sg = subject.getContext('2d');
        if (frame) sg.drawImage(frame, 0, 0, subject.width, subject.height);
        sg.globalCompositeOperation = 'destination-in';
        sg.drawImage(mc, 0, 0, subject.width, subject.height);
        if (mode === 'replace') {
            const bg = background(m.backgroundUrl);
            if (bg) drawCover(lg, bg, layer.width, layer.height); else { lg.fillStyle = m.color; lg.fillRect(0, 0, layer.width, layer.height); }
        } else if (mode === 'blur' && frame) {
            lg.filter = `blur(${m.blurPx ?? 12}px)`; lg.drawImage(frame, 0, 0, layer.width, layer.height); lg.filter = 'none';
        } else if (mode === 'cutout') {
            checker(lg, layer.width, layer.height);
        }
        lg.drawImage(subject, 0, 0);
    }
    g.save();
    if (mirror) { g.translate(ox * 2 + dw, 0); g.scale(-1, 1); }
    g.drawImage(layer, ox, oy, dw, dh);
    g.restore();
}

function drawCover(g, im, w, h) {
    const s = Math.max(w / im.naturalWidth, h / im.naturalHeight);
    const iw = im.naturalWidth * s, ih = im.naturalHeight * s;
    g.drawImage(im, (w - iw) / 2, (h - ih) / 2, iw, ih);
}

function checker(g, w, h) {
    const s = 14;
    for (let y = 0; y < h; y += s) for (let x = 0; x < w; x += s) { g.fillStyle = ((x / s + y / s) % 2) ? '#2a2f55' : '#1b2045'; g.fillRect(x, y, s, s); }
}

function drawHeat(g, hm, ox, oy, dw, dh, mirror) {
    const cw = dw / hm.cols, ch = dh / hm.rows;
    for (let r = 0; r < hm.rows; r++) for (let c = 0; c < hm.cols; c++) {
        const val = hm.values[r * hm.cols + c]; if (!(val > 0.02)) continue;
        const col = mirror ? hm.cols - 1 - c : c;
        g.fillStyle = heatColor(val, hm.opacity ?? 0.5);
        g.fillRect(ox + col * cw, oy + r * ch, cw + 0.5, ch + 0.5);
    }
}

function heatColor(t, a) {
    const stops = [[60, 196, 177], [232, 184, 74], [229, 83, 75]];
    const x = Math.min(0.999, Math.max(0, t)) * (stops.length - 1), i = Math.floor(x), k = x - i;
    const [r0, g0, b0] = stops[i], [r1, g1, b1] = stops[Math.min(i + 1, stops.length - 1)];
    return `rgba(${Math.round(r0 + (r1 - r0) * k)},${Math.round(g0 + (g1 - g0) * k)},${Math.round(b0 + (b1 - b0) * k)},${a * Math.min(1, 0.35 + t)})`;
}

/** Draws an overlay supplied directly (e.g. for replays) instead of one returned by OnFrame. */
export function setOverlay(id, json) {
    const v = viewers.get(id); if (!v) return;
    v.lastOverlay = json ? parse(json) : null; draw(v);
}

/** PNG data URL of the picture with the overlay. */
export function snapshot(id, withOverlay = true) {
    const v = viewers.get(id); if (!v) return null;
    const [src, sw, sh] = source(v); if (!src) return null;
    const c = document.createElement('canvas'); c.width = sw; c.height = sh;
    const g = c.getContext('2d');
    const mirror = v.host.classList.contains('is-mirrored');
    if (mirror) { g.translate(sw, 0); g.scale(-1, 1); }
    try { g.drawImage(src, 0, 0, sw, sh); } catch { return null; }
    g.setTransform(1, 0, 0, 1, 0, 0);
    if (withOverlay) {
        // Crop the picture area (object-fit: contain) out of the overlay canvas and scale it to the source size.
        const cw = v.host.clientWidth, ch = v.host.clientHeight, dpr = v.overlay.width / Math.max(1, cw);
        const s = Math.min(cw / sw, ch / sh), dw = sw * s, dh = sh * s, ox = (cw - dw) / 2, oy = (ch - dh) / 2;
        g.drawImage(v.overlay, ox * dpr, oy * dpr, dw * dpr, dh * dpr, 0, 0, sw, sh);
    }
    try { return c.toDataURL('image/jpeg', 0.9); } catch { return null; }
}

/** Starts recording the stage (picture + overlay) with MediaRecorder. */
export function startRecording(id, fps = 24) {
    const v = viewers.get(id); if (!v || v.recorder) return false;
    const comp = document.createElement('canvas');
    const [, sw, sh] = source(v); comp.width = sw || 640; comp.height = sh || 480;
    const g = comp.getContext('2d');
    v.recCanvas = comp;
    v.recTimer = setInterval(() => {
        const [src] = source(v); if (!src) return;
        const mirror = v.host.classList.contains('is-mirrored');
        g.save(); if (mirror) { g.translate(comp.width, 0); g.scale(-1, 1); }
        try { g.drawImage(src, 0, 0, comp.width, comp.height); } catch { }
        g.restore();
        g.drawImage(v.overlay, 0, 0, comp.width, comp.height);
    }, 1000 / fps);
    const mime = ['video/webm;codecs=vp9', 'video/webm;codecs=vp8', 'video/webm'].find(t => window.MediaRecorder?.isTypeSupported?.(t));
    if (!mime) { clearInterval(v.recTimer); return false; }
    v.chunks = [];
    v.recorder = new MediaRecorder(comp.captureStream(fps), { mimeType: mime, videoBitsPerSecond: 2_500_000 });
    v.recorder.ondataavailable = e => { if (e.data.size) v.chunks.push(e.data); };
    v.recorder.start(500);
    return true;
}

/** Stops recording and returns an object URL of the WebM clip (download it or show it in a <video>). */
export function stopRecording(id) {
    const v = viewers.get(id); if (!v?.recorder) return Promise.resolve(null);
    return new Promise(res => {
        v.recorder.onstop = () => {
            clearInterval(v.recTimer);
            const url = URL.createObjectURL(new Blob(v.chunks, { type: 'video/webm' }));
            v.recorder = null; v.chunks = []; res(url);
        };
        v.recorder.stop();
    });
}

export function download(url, fileName) {
    const a = document.createElement('a'); a.href = url; a.download = fileName; document.body.appendChild(a); a.click(); a.remove();
}

export function objectUrl(bytes, mime) {
    return URL.createObjectURL(new Blob([bytes], { type: mime || 'application/octet-stream' }));
}

export function dispose(id) {
    const v = viewers.get(id); if (!v) return;
    stop(id);
    clearInterval(v.recTimer);
    v.resize.disconnect();
    v.lastBitmap?.close?.();
    viewers.delete(id);
}
