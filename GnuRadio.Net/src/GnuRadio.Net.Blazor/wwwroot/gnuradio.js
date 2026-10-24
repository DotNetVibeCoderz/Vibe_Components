// GnuRadio.Net.Blazor rendering module. Canvas 2D drawing for the instrument displays.
// Data arrives as raw little-endian float32 bytes (Blazor transfers byte[] as Uint8Array without JSON),
// and colours come from CSS custom properties so themes stay in CSS.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

const states = new WeakMap();

function state(canvas) {
  let s = states.get(canvas);
  if (!s) {
    s = { w: 1, h: 1, dpr: 1 };
    states.set(canvas, s);
    s.ro = new ResizeObserver(() => resize(canvas, s));
    s.ro.observe(canvas);
    resize(canvas, s);
  }
  return s;
}

function resize(canvas, s) {
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.round(canvas.clientWidth * dpr));
  const h = Math.max(1, Math.round(canvas.clientHeight * dpr));
  if (canvas.width !== w || canvas.height !== h) {
    canvas.width = w;
    canvas.height = h;
  }
  s.w = w;
  s.h = h;
  s.dpr = dpr;
  if (s.redraw) s.redraw();
}

function colors(canvas) {
  const cs = getComputedStyle(canvas);
  const v = (name, fallback) => (cs.getPropertyValue(name) || fallback).trim();
  return {
    screen: v('--gr-screen', '#0b1820'),
    grid: v('--gr-grid', 'rgba(120,170,190,0.14)'),
    gridStrong: v('--gr-grid-strong', 'rgba(120,170,190,0.32)'),
    text: v('--gr-screen-text', '#8fb3c2'),
    trace: v('--gr-trace', '#ffb23f'),
    trace2: v('--gr-trace-2', '#4fd1e8'),
    marker: v('--gr-marker', '#ff5c8a'),
    trace4: v('--gr-trace-4', '#9be564'),
    font: v('--gr-font-data', '"IBM Plex Mono", ui-monospace, monospace'),
  };
}

function floats(bytes) {
  // Copy so the view is 4-byte aligned regardless of the source offset.
  const copy = bytes instanceof Uint8Array ? bytes.slice() : new Uint8Array(bytes);
  return new Float32Array(copy.buffer, 0, copy.byteLength >> 2);
}

export function attach(canvas, dotnet) {
  const s = state(canvas);
  if (dotnet && !s.clickBound) {
    s.clickBound = true;
    canvas.addEventListener('click', (e) => {
      const r = canvas.getBoundingClientRect();
      const plot = s.plot;
      if (!plot) return;
      const x = ((e.clientX - r.left) * s.dpr - plot.x) / plot.w;
      const y = ((e.clientY - r.top) * s.dpr - plot.y) / plot.h;
      if (x >= 0 && x <= 1) dotnet.invokeMethodAsync('OnCanvasClick', x, y);
    });
    canvas.addEventListener('mousemove', (e) => {
      const r = canvas.getBoundingClientRect();
      const plot = s.plot;
      if (!plot) return;
      s.hoverX = ((e.clientX - r.left) * s.dpr - plot.x) / plot.w;
    });
    canvas.addEventListener('mouseleave', () => { s.hoverX = undefined; });
  }
}

export function detach(canvas) {
  const s = states.get(canvas);
  if (s && s.ro) s.ro.disconnect();
  states.delete(canvas);
}

function plotArea(s, opts) {
  const d = s.dpr;
  const left = (opts.left ?? 46) * d;
  const bottom = (opts.bottom ?? 22) * d;
  const top = (opts.top ?? 10) * d;
  const right = (opts.right ?? 10) * d;
  const plot = { x: left, y: top, w: Math.max(1, s.w - left - right), h: Math.max(1, s.h - top - bottom) };
  s.plot = plot;
  return plot;
}

function grid(ctx, s, plot, c, opts) {
  const d = s.dpr;
  ctx.font = `${10.5 * d}px ${c.font}`;
  ctx.lineWidth = 1;
  ctx.textBaseline = 'middle';
  for (const t of opts.yTicks || []) {
    const y = Math.round(plot.y + (1 - t.p) * plot.h) + 0.5;
    ctx.strokeStyle = t.strong ? c.gridStrong : c.grid;
    ctx.beginPath();
    ctx.moveTo(plot.x, y);
    ctx.lineTo(plot.x + plot.w, y);
    ctx.stroke();
    ctx.fillStyle = c.text;
    ctx.textAlign = 'right';
    ctx.fillText(t.l, plot.x - 6 * d, y);
  }
  ctx.textBaseline = 'top';
  for (const t of opts.xTicks || []) {
    const x = Math.round(plot.x + t.p * plot.w) + 0.5;
    ctx.strokeStyle = t.strong ? c.gridStrong : c.grid;
    ctx.beginPath();
    ctx.moveTo(x, plot.y);
    ctx.lineTo(x, plot.y + plot.h);
    ctx.stroke();
    ctx.fillStyle = c.text;
    ctx.textAlign = 'center';
    ctx.fillText(t.l, x, plot.y + plot.h + 6 * d);
  }
  ctx.strokeStyle = c.gridStrong;
  ctx.strokeRect(plot.x + 0.5, plot.y + 0.5, plot.w - 1, plot.h - 1);
}

function clear(ctx, s, c) {
  ctx.fillStyle = c.screen;
  ctx.fillRect(0, 0, s.w, s.h);
}

// Peak-preserving decimation: one min/max pair per pixel column.
function columns(data, plotW) {
  const n = data.length;
  const cols = Math.max(1, Math.floor(plotW));
  const lo = new Float32Array(cols);
  const hi = new Float32Array(cols);
  for (let x = 0; x < cols; x++) {
    const a = Math.floor((x * n) / cols);
    const b = Math.max(a + 1, Math.floor(((x + 1) * n) / cols));
    let mn = Infinity, mx = -Infinity;
    for (let i = a; i < b && i < n; i++) {
      const v = data[i];
      if (v < mn) mn = v;
      if (v > mx) mx = v;
    }
    lo[x] = mn;
    hi[x] = mx;
  }
  return { lo, hi, cols };
}

function markers(ctx, s, plot, c, list) {
  const d = s.dpr;
  ctx.font = `${10.5 * d}px ${c.font}`;
  for (const m of list || []) {
    if (m.p < 0 || m.p > 1) continue;
    const x = Math.round(plot.x + m.p * plot.w) + 0.5;
    ctx.strokeStyle = m.color || c.marker;
    ctx.setLineDash(m.dashed ? [4 * d, 4 * d] : []);
    ctx.beginPath();
    ctx.moveTo(x, plot.y);
    ctx.lineTo(x, plot.y + plot.h);
    ctx.stroke();
    ctx.setLineDash([]);
    if (m.l) {
      ctx.fillStyle = m.color || c.marker;
      ctx.textAlign = x > plot.x + plot.w - 80 * d ? 'right' : 'left';
      ctx.textBaseline = 'top';
      ctx.fillText(m.l, x + (ctx.textAlign === 'left' ? 4 : -4) * d, plot.y + 4 * d + (m.row || 0) * 13 * d);
    }
  }
}

// Spectrum analyser / generic trace. opts: { min, max, xTicks, yTicks, markers, fill, hold }
export function drawTrace(canvas, bytes, opts, holdBytes) {
  const s = state(canvas);
  const ctx = canvas.getContext('2d');
  const c = colors(canvas);
  const data = floats(bytes);
  const hold = holdBytes ? floats(holdBytes) : null;
  s.redraw = () => drawTrace(canvas, bytes, opts, holdBytes);
  clear(ctx, s, c);
  const plot = plotArea(s, opts);
  grid(ctx, s, plot, c, opts);
  if (data.length === 0) return;
  const range = opts.max - opts.min || 1;
  const yOf = (v) => plot.y + (1 - (Math.min(Math.max(v, opts.min), opts.max) - opts.min) / range) * plot.h;

  ctx.save();
  ctx.beginPath();
  ctx.rect(plot.x, plot.y, plot.w, plot.h);
  ctx.clip();

  if (hold) {
    const h = columns(hold, plot.w);
    ctx.strokeStyle = c.trace2;
    ctx.globalAlpha = 0.7;
    ctx.beginPath();
    for (let x = 0; x < h.cols; x++) {
      const px = plot.x + (x * plot.w) / h.cols;
      const y = yOf(h.hi[x]);
      if (x === 0) ctx.moveTo(px, y); else ctx.lineTo(px, y);
    }
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  const col = columns(data, plot.w);
  if (opts.fill !== false) {
    const g = ctx.createLinearGradient(0, plot.y, 0, plot.y + plot.h);
    g.addColorStop(0, hexA(c.trace, 0.35));
    g.addColorStop(1, hexA(c.trace, 0.02));
    ctx.fillStyle = g;
    ctx.beginPath();
    ctx.moveTo(plot.x, plot.y + plot.h);
    for (let x = 0; x < col.cols; x++) ctx.lineTo(plot.x + (x * plot.w) / col.cols, yOf(col.hi[x]));
    ctx.lineTo(plot.x + plot.w, plot.y + plot.h);
    ctx.closePath();
    ctx.fill();
  }
  ctx.strokeStyle = c.trace;
  ctx.lineWidth = 1.25 * s.dpr;
  ctx.beginPath();
  for (let x = 0; x < col.cols; x++) {
    const px = plot.x + (x * plot.w) / col.cols;
    ctx.moveTo(px, yOf(col.lo[x]));
    ctx.lineTo(px, yOf(col.hi[x]) - 0.5);
  }
  ctx.stroke();
  ctx.beginPath();
  for (let x = 0; x < col.cols; x++) {
    const px = plot.x + (x * plot.w) / col.cols;
    const y = yOf(col.hi[x]);
    if (x === 0) ctx.moveTo(px, y); else ctx.lineTo(px, y);
  }
  ctx.stroke();
  ctx.restore();
  markers(ctx, s, plot, c, opts.markers);

  if (s.hoverX !== undefined && opts.hoverLabels) {
    const idx = Math.min(data.length - 1, Math.max(0, Math.floor(s.hoverX * data.length)));
    const x = plot.x + s.hoverX * plot.w;
    ctx.strokeStyle = c.gridStrong;
    ctx.beginPath();
    ctx.moveTo(x, plot.y);
    ctx.lineTo(x, plot.y + plot.h);
    ctx.stroke();
    ctx.fillStyle = c.text;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'bottom';
    ctx.fillText(`${formatHover(opts.hoverLabels, idx)}  ${data[idx].toFixed(1)} ${opts.hoverLabels.unit || ''}`, plot.x + 6 * s.dpr, plot.y + plot.h - 4 * s.dpr);
  }
}

function formatHover(h, idx) {
  const v = h.x0 + idx * h.dx;
  const a = Math.abs(v);
  if (h.kind === 'hz') {
    if (a >= 1e9) return (v / 1e9).toFixed(6) + ' GHz';
    if (a >= 1e6) return (v / 1e6).toFixed(4) + ' MHz';
    if (a >= 1e3) return (v / 1e3).toFixed(2) + ' kHz';
    return v.toFixed(0) + ' Hz';
  }
  return v.toFixed(2) + (h.xUnit ? ' ' + h.xUnit : '');
}

function hexA(color, alpha) {
  if (color.startsWith('#') && (color.length === 7)) {
    const r = parseInt(color.slice(1, 3), 16), g = parseInt(color.slice(3, 5), 16), b = parseInt(color.slice(5, 7), 16);
    return `rgba(${r},${g},${b},${alpha})`;
  }
  return color;
}

// Waterfall: rgba holds `rows` rows of `bins` pixels, newest first. History scrolls down.
export function pushWaterfall(canvas, rgba, bins, rows, history, opts) {
  const s = state(canvas);
  if (!s.off || s.off.width !== bins || s.off.height !== history) {
    s.off = document.createElement('canvas');
    s.off.width = bins;
    s.off.height = history;
    const octx = s.off.getContext('2d');
    octx.fillStyle = colors(canvas).screen;
    octx.fillRect(0, 0, bins, history);
  }
  const octx = s.off.getContext('2d');
  if (rows > 0) {
    const r = Math.min(rows, history);
    octx.drawImage(s.off, 0, 0, bins, history - r, 0, r, bins, history - r);
    const pixels = rgba instanceof Uint8Array ? new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, bins * r * 4) : new Uint8ClampedArray(rgba);
    octx.putImageData(new ImageData(pixels.slice(), bins, r), 0, 0);
  }
  s.wfOpts = opts;
  s.redraw = () => renderWaterfall(canvas, s);
  renderWaterfall(canvas, s);
}

function renderWaterfall(canvas, s) {
  const ctx = canvas.getContext('2d');
  const c = colors(canvas);
  const opts = s.wfOpts || {};
  clear(ctx, s, c);
  const plot = plotArea(s, opts);
  if (s.off) {
    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(s.off, plot.x, plot.y, plot.w, plot.h);
  }
  grid(ctx, s, plot, c, { xTicks: opts.xTicks, yTicks: [] });
  markers(ctx, s, plot, c, opts.markers);
  if (opts.yLabel) {
    ctx.save();
    ctx.fillStyle = c.text;
    ctx.font = `${10.5 * s.dpr}px ${c.font}`;
    ctx.translate(14 * s.dpr, plot.y + plot.h / 2);
    ctx.rotate(-Math.PI / 2);
    ctx.textAlign = 'center';
    ctx.fillText(opts.yLabel, 0, 0);
    ctx.restore();
  }
}

// Constellation with phosphor persistence. points: interleaved re/im float32.
export function drawConstellation(canvas, bytes, opts) {
  const s = state(canvas);
  const ctx = canvas.getContext('2d');
  const c = colors(canvas);
  const pts = floats(bytes);
  const d = s.dpr;
  const size = Math.min(s.w, s.h) - 24 * d;
  const plot = { x: (s.w - size) / 2, y: (s.h - size) / 2, w: size, h: size };
  s.plot = plot;
  s.redraw = () => drawConstellation(canvas, bytes, opts);
  // Fade the previous frame instead of clearing: a phosphor afterglow.
  ctx.fillStyle = hexA(c.screen, opts.persistence ?? 0.45);
  if (!s.primed || opts.persistence === 1) {
    ctx.fillStyle = c.screen;
    s.primed = true;
  }
  ctx.fillRect(0, 0, s.w, s.h);
  const scale = opts.scale || 1.5;
  const toX = (v) => plot.x + ((v / scale) * 0.5 + 0.5) * plot.w;
  const toY = (v) => plot.y + (0.5 - (v / scale) * 0.5) * plot.h;
  ctx.strokeStyle = c.grid;
  ctx.lineWidth = 1;
  for (let k = -2; k <= 2; k++) {
    const t = (k / 2) * scale;
    ctx.beginPath(); ctx.moveTo(toX(t), plot.y); ctx.lineTo(toX(t), plot.y + plot.h); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(plot.x, toY(t)); ctx.lineTo(plot.x + plot.w, toY(t)); ctx.stroke();
  }
  ctx.strokeStyle = c.gridStrong;
  ctx.strokeRect(plot.x + 0.5, plot.y + 0.5, plot.w - 1, plot.h - 1);
  if (opts.reference) {
    ctx.strokeStyle = hexA(c.trace2, 0.8);
    for (let i = 0; i + 1 < opts.reference.length; i += 2) {
      ctx.beginPath();
      ctx.arc(toX(opts.reference[i]), toY(opts.reference[i + 1]), 5 * d, 0, Math.PI * 2);
      ctx.stroke();
    }
  }
  ctx.fillStyle = c.trace;
  const r = Math.max(1, 1.6 * d);
  for (let i = 0; i + 1 < pts.length; i += 2) {
    const x = toX(pts[i]), y = toY(pts[i + 1]);
    if (x < plot.x || x > plot.x + plot.w || y < plot.y || y > plot.y + plot.h) continue;
    ctx.fillRect(x - r / 2, y - r / 2, r, r);
  }
  ctx.fillStyle = c.text;
  ctx.font = `${10.5 * d}px ${c.font}`;
  ctx.textAlign = 'left';
  ctx.textBaseline = 'top';
  ctx.fillText('I', plot.x + plot.w - 10 * d, toY(0) + 3 * d);
  ctx.fillText('Q', toX(0) + 4 * d, plot.y + 3 * d);
}

// Oscilloscope: `count` traces of `length` samples each, concatenated.
export function drawScope(canvas, bytes, count, length, opts) {
  const s = state(canvas);
  const ctx = canvas.getContext('2d');
  const c = colors(canvas);
  const data = floats(bytes);
  s.redraw = () => drawScope(canvas, bytes, count, length, opts);
  clear(ctx, s, c);
  const plot = plotArea(s, opts);
  grid(ctx, s, plot, c, opts);
  const palette = [c.trace, c.trace2, c.marker, c.trace4];
  const range = opts.max - opts.min || 1;
  ctx.save();
  ctx.beginPath();
  ctx.rect(plot.x, plot.y, plot.w, plot.h);
  ctx.clip();
  ctx.lineWidth = 1.25 * s.dpr;
  for (let t = 0; t < count; t++) {
    ctx.strokeStyle = palette[t % palette.length];
    ctx.beginPath();
    for (let i = 0; i < length; i++) {
      const v = data[t * length + i];
      const x = plot.x + (i / Math.max(1, length - 1)) * plot.w;
      const y = plot.y + (1 - (v - opts.min) / range) * plot.h;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
  }
  ctx.restore();
  if (opts.names && opts.names.length > 1) {
    ctx.font = `${10.5 * s.dpr}px ${c.font}`;
    ctx.textAlign = 'right';
    ctx.textBaseline = 'top';
    opts.names.forEach((n, i) => {
      ctx.fillStyle = palette[i % palette.length];
      ctx.fillText(n, plot.x + plot.w - 6 * s.dpr, plot.y + 4 * s.dpr + i * 13 * s.dpr);
    });
  }
}

// XY line chart with optional log-y, for BER curves and filter responses.
// opts: { series: [{ name, x: [], y: [], dashed, color }], xMin, xMax, yMin, yMax, logY, xTicks, yTicks }
export function drawLines(canvas, opts) {
  const s = state(canvas);
  const ctx = canvas.getContext('2d');
  const c = colors(canvas);
  s.redraw = () => drawLines(canvas, opts);
  clear(ctx, s, c);
  const plot = plotArea(s, { ...opts, left: opts.left ?? 56 });
  grid(ctx, s, plot, c, opts);
  const palette = [c.trace, c.trace2, c.marker, c.trace4, '#c792ea', '#f78c6c'];
  const ty = (v) => (opts.logY ? Math.log10(Math.max(v, 1e-30)) : v);
  const y0 = ty(opts.yMin), y1 = ty(opts.yMax);
  const toX = (v) => plot.x + ((v - opts.xMin) / (opts.xMax - opts.xMin || 1)) * plot.w;
  const toY = (v) => plot.y + (1 - (ty(v) - y0) / (y1 - y0 || 1)) * plot.h;
  ctx.save();
  ctx.beginPath();
  ctx.rect(plot.x, plot.y, plot.w, plot.h);
  ctx.clip();
  (opts.series || []).forEach((ser, i) => {
    const color = ser.color || palette[i % palette.length];
    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.lineWidth = 1.6 * s.dpr;
    ctx.setLineDash(ser.dashed ? [5 * s.dpr, 4 * s.dpr] : []);
    ctx.beginPath();
    let started = false;
    for (let k = 0; k < ser.x.length; k++) {
      const y = ser.y[k];
      if (!(y > (opts.logY ? 0 : -Infinity))) { started = false; continue; }
      const px = toX(ser.x[k]), py = toY(y);
      if (!started) { ctx.moveTo(px, py); started = true; } else ctx.lineTo(px, py);
    }
    ctx.stroke();
    ctx.setLineDash([]);
    if (ser.points) {
      for (let k = 0; k < ser.x.length; k++) {
        const y = ser.y[k];
        if (!(y > (opts.logY ? 0 : -Infinity))) continue;
        ctx.beginPath();
        ctx.arc(toX(ser.x[k]), toY(y), 2.6 * s.dpr, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  });
  ctx.restore();
  ctx.font = `${11 * s.dpr}px ${c.font}`;
  ctx.textAlign = 'right';
  ctx.textBaseline = 'top';
  (opts.series || []).forEach((ser, i) => {
    ctx.fillStyle = ser.color || palette[i % palette.length];
    ctx.fillText(ser.name, plot.x + plot.w - 8 * s.dpr, plot.y + 6 * s.dpr + i * 14 * s.dpr);
  });
}
