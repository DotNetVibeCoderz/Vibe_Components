// Small helpers used by the GDAL.Net Gallery (theme persistence, clipboard).
window.gdalGallery = {
    setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem('gdalnet-theme', theme); } catch (e) { }
    },
    getTheme() {
        const explicit = document.documentElement.getAttribute('data-theme');
        if (explicit) return explicit;
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    },
    setLang(lang) {
        document.documentElement.setAttribute('lang', lang);
        try { localStorage.setItem('gdalnet-lang', lang); } catch (e) { }
    },
    getLang() {
        try { return localStorage.getItem('gdalnet-lang') || 'en'; } catch (e) { return 'en'; }
    },
    async copy(text) {
        try { await navigator.clipboard.writeText(text); return true; } catch (e) { return false; }
    }
};
