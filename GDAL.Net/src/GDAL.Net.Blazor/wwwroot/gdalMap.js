// GDAL.Net Blazor map interaction: wheel / drag / keyboard navigation applied as a CSS transform
// on the client, committed to .NET (which re-renders rasters + SVG) when the gesture settles.
const states = new WeakMap();

function apply(st) {
    st.stage.style.transform = `translate(${st.tx}px, ${st.ty}px) scale(${st.s})`;
}

function schedule(st, ms) {
    clearTimeout(st.timer);
    st.timer = setTimeout(() => commit(st), ms);
}

function commit(st) {
    if (st.inflight) { st.again = true; return; }
    if (Math.abs(st.s - 1) < 1e-9 && Math.abs(st.tx) < 0.5 && Math.abs(st.ty) < 0.5) return;
    st.inflight = { s: st.s, tx: st.tx, ty: st.ty };
    st.host.classList.add('is-loading');
    st.dotnet.invokeMethodAsync('Commit', st.s, st.tx, st.ty).catch(() => {
        st.inflight = null;
        st.host.classList.remove('is-loading');
    });
}

function zoomAt(st, ax, ay, f) {
    const next = st.s * f;
    if (next > 64 || next < 1 / 64) return;
    st.s = next;
    st.tx = f * (st.tx - ax) + ax;
    st.ty = f * (st.ty - ay) + ay;
    apply(st);
}

function localPoint(st, e) {
    const r = st.host.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
}

function toWorld(st, p) {
    if (!st.view) return null;
    const rx = (p.x - st.tx) / st.s;
    const ry = (p.y - st.ty) / st.s;
    return { x: st.view.originX + rx / st.view.scale, y: st.view.originY - ry / st.view.scale };
}

function updateReadout(st, p) {
    if (!st.readout) return;
    const w = toWorld(st, p);
    if (!w) return;
    const d = st.view.geographic ? 5 : 1;
    const fmt = v => v.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
    st.readout.textContent = st.view.geographic ? `${fmt(w.x)}°, ${fmt(w.y)}°` : `${fmt(w.x)}, ${fmt(w.y)}`;
}

export function init(host, stage, readout, dotnet) {
    const st = { host, stage, readout, dotnet, s: 1, tx: 0, ty: 0, inflight: null, again: false, timer: 0, view: null, down: null };
    states.set(host, st);
    stage.style.transformOrigin = '0 0';

    host.addEventListener('wheel', e => {
        e.preventDefault();
        const p = localPoint(st, e);
        zoomAt(st, p.x, p.y, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0016)));
        schedule(st, 220);
    }, { passive: false });

    host.addEventListener('pointerdown', e => {
        if (e.button !== 0 || e.target.closest('.gdal-map__ui')) return;
        host.setPointerCapture(e.pointerId);
        const p = localPoint(st, e);
        st.down = { x: p.x, y: p.y, tx: st.tx, ty: st.ty, moved: false };
        clearTimeout(st.timer);
    });

    host.addEventListener('pointermove', e => {
        const p = localPoint(st, e);
        updateReadout(st, p);
        if (!st.down) return;
        const dx = p.x - st.down.x, dy = p.y - st.down.y;
        if (!st.down.moved && Math.hypot(dx, dy) > 3) { st.down.moved = true; host.classList.add('is-dragging'); }
        if (st.down.moved) {
            st.tx = st.down.tx + dx;
            st.ty = st.down.ty + dy;
            apply(st);
        }
    });

    const end = e => {
        if (!st.down) return;
        const moved = st.down.moved;
        st.down = null;
        host.classList.remove('is-dragging');
        if (moved) { schedule(st, 0); return; }
        const p = localPoint(st, e);
        const rx = (p.x - st.tx) / st.s, ry = (p.y - st.ty) / st.s;
        st.dotnet.invokeMethodAsync('Click', rx, ry).catch(() => { });
    };
    host.addEventListener('pointerup', end);
    host.addEventListener('pointercancel', () => { st.down = null; host.classList.remove('is-dragging'); schedule(st, 0); });
    host.addEventListener('pointerleave', () => { if (st.readout && !st.down) st.readout.textContent = '—'; });

    host.addEventListener('keydown', e => {
        const r = host.getBoundingClientRect();
        const cx = r.width / 2, cy = r.height / 2, step = 80;
        let handled = true;
        switch (e.key) {
            case '+': case '=': zoomAt(st, cx, cy, 1.5); break;
            case '-': case '_': zoomAt(st, cx, cy, 1 / 1.5); break;
            case 'ArrowLeft': st.tx += step; apply(st); break;
            case 'ArrowRight': st.tx -= step; apply(st); break;
            case 'ArrowUp': st.ty += step; apply(st); break;
            case 'ArrowDown': st.ty -= step; apply(st); break;
            default: handled = false;
        }
        if (handled) { e.preventDefault(); schedule(st, 250); }
    });

    st.observer = new ResizeObserver(entries => {
        const r = entries[0].contentRect;
        dotnet.invokeMethodAsync('Resize', Math.round(r.width), Math.round(r.height)).catch(() => { });
    });
    st.observer.observe(host);

    const r = host.getBoundingClientRect();
    return { width: Math.round(r.width), height: Math.round(r.height) };
}

/// Called by .NET once the DOM shows a render that includes the committed transform.
export function rendered(host, cs, ctx, cty, view) {
    const st = states.get(host);
    if (!st) return;
    st.view = view;
    if (cs !== 1 || ctx !== 0 || cty !== 0) {
        const ns = st.s / cs;
        st.tx = st.tx - ns * ctx;
        st.ty = st.ty - ns * cty;
        st.s = ns;
        apply(st);
    }
    st.inflight = null;
    host.classList.remove('is-loading');
    if (st.again) { st.again = false; schedule(st, 0); }
}

/// Drops any client-side transform (after "fit" or a new extent).
export function reset(host, view) {
    const st = states.get(host);
    if (!st) return;
    clearTimeout(st.timer);
    st.s = 1; st.tx = 0; st.ty = 0; st.inflight = null; st.again = false; st.view = view;
    apply(st);
    host.classList.remove('is-loading');
}

export function zoomBy(host, f) {
    const st = states.get(host);
    if (!st) return;
    const r = host.getBoundingClientRect();
    zoomAt(st, r.width / 2, r.height / 2, f);
    schedule(st, 260);
}

export function dispose(host) {
    const st = states.get(host);
    if (!st) return;
    st.observer?.disconnect();
    clearTimeout(st.timer);
    states.delete(host);
}
