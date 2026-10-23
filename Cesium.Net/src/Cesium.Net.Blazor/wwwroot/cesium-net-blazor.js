// Cesium.Net Blazor interop module. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
let bridgeLoading = null;
const observers = new Map();

function ensureBridge() {
  if (window.CesiumNet) return Promise.resolve(window.CesiumNet);
  if (bridgeLoading) return bridgeLoading;
  bridgeLoading = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = new URL('./cesium-net.js', import.meta.url).href;
    s.onload = () => resolve(window.CesiumNet);
    s.onerror = () => { bridgeLoading = null; reject(new Error('Failed to load cesium-net.js')); };
    document.head.appendChild(s);
  });
  return bridgeLoading;
}

export async function create(element, optionsJson, dotnetRef) {
  const bridge = await ensureBridge();
  const id = await bridge.create(element, optionsJson, (name, json) => {
    dotnetRef.invokeMethodAsync('OnCesiumEvent', name, json).catch(() => { /* circuit gone */ });
  });
  if ('ResizeObserver' in window) {
    const ro = new ResizeObserver(() => bridge.resize(id));
    ro.observe(element);
    observers.set(id, ro);
  }
  return id;
}

// Results larger than this are not returned inline: Blazor Server rejects JS-to-.NET messages
// above its SignalR limit (32 KB by default) and drops the circuit. They are parked here and
// streamed to .NET through an IJSStreamReference instead (e.g. screenshots).
const INLINE_LIMIT = 16 * 1024;
const LARGE_PREFIX = '\u0001cesium-net-large:';
const largeResults = new Map();
let nextLargeId = 1;

export async function dispatch(id, method, argsJson) {
  const result = await window.CesiumNet.dispatch(id, method, argsJson);
  if (typeof result === 'string' && result.length > INLINE_LIMIT) {
    const key = String(nextLargeId++);
    largeResults.set(key, result);
    return LARGE_PREFIX + key;
  }
  return result;
}

export function takeLargeResult(key) {
  const value = largeResults.get(key);
  largeResults.delete(key);
  return new TextEncoder().encode(value ?? 'null');
}

export function destroy(id) {
  const ro = observers.get(id);
  if (ro) { ro.disconnect(); observers.delete(id); }
  if (window.CesiumNet) window.CesiumNet.destroy(id);
}
