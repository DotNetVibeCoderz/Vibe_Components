// Applies the saved (or ?theme=) colour theme before first paint and exposes a tiny API for the Blazor toggle.
(function () {
  try {
    var q = new URLSearchParams(location.search).get('theme');
    var t = q || localStorage.getItem('eln-theme');
    if (t === 'dark' || t === 'light') document.documentElement.setAttribute('data-theme', t);
  } catch (e) { }
})();
window.elnTheme = {
  get: function () {
    var a = document.documentElement.getAttribute('data-theme');
    if (a) return a;
    return window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  },
  set: function (t) {
    document.documentElement.setAttribute('data-theme', t);
    try { localStorage.setItem('eln-theme', t); } catch (e) { }
  },
  setLang: function (l) {
    document.documentElement.setAttribute('lang', l);
    try { localStorage.setItem('eln-lang', l); } catch (e) { }
  },
  getLang: function () { try { return localStorage.getItem('eln-lang'); } catch (e) { return null; } }
};
