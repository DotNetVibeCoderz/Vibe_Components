// Gallery helpers: preferences in localStorage (per-viewer convenience) and clipboard.
window.galleryPrefs = {
  get(key) {
    try { return localStorage.getItem('cesiumnet.gallery.' + key); } catch { return null; }
  },
  set(key, value) {
    try {
      if (value) localStorage.setItem('cesiumnet.gallery.' + key, value);
      else localStorage.removeItem('cesiumnet.gallery.' + key);
    } catch { /* storage blocked */ }
  },
  async copy(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
  },
  scrollLogToEnd(el) {
    if (el) el.scrollTop = el.scrollHeight;
  }
};
