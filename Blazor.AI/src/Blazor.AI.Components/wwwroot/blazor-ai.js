// Blazor.AI browser helpers (ES module). Loaded lazily by BlazorAIJs.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

const THEME_KEY = "blazorai-theme";

export function getTheme() {
    try {
        return localStorage.getItem(THEME_KEY) || document.documentElement.getAttribute("data-bs-theme") || "light";
    } catch {
        return document.documentElement.getAttribute("data-bs-theme") || "light";
    }
}

export function setTheme(theme) {
    document.documentElement.setAttribute("data-bs-theme", theme);
    try { localStorage.setItem(THEME_KEY, theme); } catch { /* storage may be blocked */ }
}

export async function readClipboard() {
    try {
        return await navigator.clipboard.readText();
    } catch {
        return null;
    }
}

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

export function downloadText(fileName, content, mime) {
    const blob = new Blob([content], { type: mime || "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

export function focus(element) {
    element?.focus();
}

export function autosize(element, maxPx) {
    if (!element) return;
    element.style.height = "auto";
    element.style.height = Math.min(element.scrollHeight, maxPx || 160) + "px";
}

// Enter sends, Shift+Enter inserts a newline; the textarea grows with its content.
const composers = new WeakMap();

export function attachComposer(element, dotnet) {
    if (!element || composers.has(element)) return;
    const onKey = event => {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
            dotnet.invokeMethodAsync("SubmitFromComposer", element.value);
        }
    };
    const onInput = () => autosize(element, 160);
    element.addEventListener("keydown", onKey);
    element.addEventListener("input", onInput);
    composers.set(element, { onKey, onInput });
}

export function detachComposer(element) {
    const handlers = element && composers.get(element);
    if (handlers) {
        element.removeEventListener("keydown", handlers.onKey);
        element.removeEventListener("input", handlers.onInput);
        composers.delete(element);
    }
}

export function getValue(element) {
    return element ? element.value : "";
}

export function clearComposer(element) {
    if (element) {
        element.value = "";
        autosize(element, 160);
    }
}

// ---------------- SmartPaste ----------------

function scopeOf(element) {
    return element?.closest("[data-smartpaste-scope]") || element?.closest("form") || document.body;
}

function labelFor(field, scope) {
    if (field.dataset.smartpasteDescription) return field.dataset.smartpasteDescription;
    if (field.id) {
        const label = scope.querySelector(`label[for="${CSS.escape(field.id)}"]`);
        if (label) return label.textContent.trim();
    }
    const wrapping = field.closest("label");
    if (wrapping) return wrapping.textContent.trim();
    return field.getAttribute("aria-label") || field.placeholder || field.title || "";
}

export function collectFormFields(element) {
    const scope = scopeOf(element);
    const fields = [];
    const seenRadios = new Set();
    for (const field of scope.querySelectorAll("input, select, textarea")) {
        if (field.dataset.smartpaste === "false" || field.disabled || field.readOnly) continue;
        const type = (field.type || "").toLowerCase();
        if (["hidden", "submit", "button", "reset", "file", "password", "image"].includes(type)) continue;
        const key = field.name || field.id;
        if (!key) continue;

        if (type === "radio") {
            if (seenRadios.has(key)) continue;
            seenRadios.add(key);
            const options = [...scope.querySelectorAll(`input[type=radio][name="${CSS.escape(key)}"]`)].map(r => labelFor(r, scope) || r.value);
            fields.push({ name: key, description: key, type: "string", allowedValues: options });
            continue;
        }

        let kind = "string";
        if (type === "number" || type === "range") kind = "number";
        else if (type === "checkbox") kind = "boolean";
        else if (type === "date" || type === "datetime-local") kind = "date";
        else if (type === "email") kind = "email";
        else if (type === "tel") kind = "phone";

        const allowed = field.tagName === "SELECT" ? [...field.options].filter(o => o.value !== "").map(o => o.text.trim()) : null;
        fields.push({ name: key, description: labelFor(field, scope), type: kind, allowedValues: allowed });
    }
    return fields;
}

function fire(field) {
    field.dispatchEvent(new Event("input", { bubbles: true }));
    field.dispatchEvent(new Event("change", { bubbles: true }));
}

export function applyFormValues(element, values) {
    const scope = scopeOf(element);
    let applied = 0;
    for (const [key, raw] of Object.entries(values)) {
        const value = raw == null ? "" : String(raw);
        const byName = scope.querySelectorAll(`[name="${CSS.escape(key)}"]`);
        const nodes = byName.length ? [...byName] : [scope.querySelector(`#${CSS.escape(key)}`)].filter(Boolean);
        for (const field of nodes) {
            const type = (field.type || "").toLowerCase();
            if (type === "radio") {
                const label = labelFor(field, scope);
                if (field.value.toLowerCase() === value.toLowerCase() || label.toLowerCase() === value.toLowerCase()) {
                    field.checked = true;
                    fire(field);
                    applied++;
                }
            } else if (type === "checkbox") {
                field.checked = /^(true|yes|ya|1|on)$/i.test(value);
                fire(field);
                applied++;
            } else if (field.tagName === "SELECT") {
                const option = [...field.options].find(o => o.text.trim().toLowerCase() === value.toLowerCase() || o.value.toLowerCase() === value.toLowerCase());
                if (option) {
                    field.value = option.value;
                    fire(field);
                    applied++;
                }
            } else {
                field.value = value;
                field.classList.add("bai-pasted");
                setTimeout(() => field.classList.remove("bai-pasted"), 1600);
                fire(field);
                applied++;
            }
        }
    }
    return applied;
}

// ---------------- SmartTextArea ----------------

const attached = new WeakMap();

export function attachSmartTextArea(element, dotnet) {
    if (!element || attached.has(element)) return;
    const onKey = event => {
        if (element.dataset.suggestion !== "1") return;
        if (event.key === "Tab" && !event.shiftKey) {
            event.preventDefault();
            dotnet.invokeMethodAsync("AcceptSuggestion");
        } else if (event.key === "Escape") {
            dotnet.invokeMethodAsync("DismissSuggestion");
        }
    };
    element.addEventListener("keydown", onKey);
    attached.set(element, onKey);
}

export function detachSmartTextArea(element) {
    const handler = element && attached.get(element);
    if (handler) {
        element.removeEventListener("keydown", handler);
        attached.delete(element);
    }
}

export function getCaret(element) {
    if (!element) return { start: 0, end: 0, length: 0 };
    return { start: element.selectionStart ?? 0, end: element.selectionEnd ?? 0, length: element.value.length };
}

export function setValueAndCaret(element, value, caret) {
    if (!element) return;
    element.value = value;
    element.setSelectionRange(caret, caret);
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
}

export function syncScroll(source, target) {
    if (source && target) target.scrollTop = source.scrollTop;
}

// ---------------- Speech (Web Speech API) ----------------

let recognition = null;

export function speechSupported() {
    return {
        recognition: !!(window.SpeechRecognition || window.webkitSpeechRecognition),
        synthesis: "speechSynthesis" in window,
    };
}

export function startRecognition(dotnet, lang) {
    const Ctor = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Ctor) return false;
    stopRecognition();
    recognition = new Ctor();
    recognition.lang = lang || "en-US";
    recognition.interimResults = true;
    recognition.continuous = true;
    recognition.onresult = e => {
        let interim = "", final = "";
        for (let i = e.resultIndex; i < e.results.length; i++) {
            const r = e.results[i];
            if (r.isFinal) final += r[0].transcript; else interim += r[0].transcript;
        }
        dotnet.invokeMethodAsync("OnSpeechResult", final, interim);
    };
    recognition.onerror = e => dotnet.invokeMethodAsync("OnSpeechError", e.error || "error");
    recognition.onend = () => dotnet.invokeMethodAsync("OnSpeechEnd");
    recognition.start();
    return true;
}

export function stopRecognition() {
    if (recognition) {
        try { recognition.stop(); } catch { /* already stopped */ }
        recognition = null;
    }
}

export function listVoices() {
    if (!("speechSynthesis" in window)) return [];
    return speechSynthesis.getVoices().map(v => ({ name: v.name, lang: v.lang, isDefault: v.default }));
}

export function speak(text, voiceName, rate, pitch) {
    if (!("speechSynthesis" in window)) return false;
    speechSynthesis.cancel();
    const u = new SpeechSynthesisUtterance(text);
    const voice = speechSynthesis.getVoices().find(v => v.name === voiceName);
    if (voice) u.voice = voice;
    u.rate = rate || 1;
    u.pitch = pitch || 1;
    speechSynthesis.speak(u);
    return true;
}

export function stopSpeaking() {
    if ("speechSynthesis" in window) speechSynthesis.cancel();
}

export function playAudio(base64, mime) {
    const audio = new Audio(`data:${mime || "audio/flac"};base64,${base64}`);
    audio.play();
}

// ---------------- Geometry (designers, dock panels) ----------------

export function boundingRect(element) {
    if (!element) return { x: 0, y: 0, width: 0, height: 0 };
    const r = element.getBoundingClientRect();
    return { x: r.left, y: r.top, width: r.width, height: r.height };
}
