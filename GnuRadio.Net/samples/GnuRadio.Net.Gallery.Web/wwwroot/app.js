// Small helpers for the gallery shell.
window.grGallery = {
  copy: async (text) => {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
  },
};
