// Small helpers for the OpenSceneGraph.Net Gallery (no external dependencies).
window.osgGallery = {
    getLang() {
        try { return localStorage.getItem('osg.lang'); } catch { return null; }
    },
    setLang(lang) {
        try { localStorage.setItem('osg.lang', lang); } catch { /* private mode */ }
        document.documentElement.lang = lang;
    },
    async copy(text) {
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            ta.remove();
        }
    },
};
(function () {
    const l = window.osgGallery.getLang();
    if (l) document.documentElement.lang = l;
})();
