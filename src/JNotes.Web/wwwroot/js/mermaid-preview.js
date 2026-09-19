// Renders ```mermaid blocks in MarkdownPreview.razor. Markdig emits <pre class="mermaid">SOURCE</pre>
// (escaped); this module swaps the source for an SVG.
//
// Vendored mermaid 11.17.2 (MIT) lives at wwwroot/lib/mermaid/dist/mermaid.min.js — a self-contained
// UMD bundle that defines window.mermaid. It's injected on first use rather than from App.razor, so
// notes without diagrams never download 3.4 MB.
//
// The awkward part is Blazor: a MarkupString is opaque to its diff, so ANY change to the rendered
// HTML (every keystroke in the editor) tears down the whole preview subtree and rebuilds it,
// destroying rendered diagrams. Hence three mechanisms below: an element stamp (skip work when the
// same element already shows this source+theme), an SVG cache (repaint a rebuilt element
// synchronously, so there's no flash), and a debounce (only genuinely new source reaches mermaid).

const DEBOUNCE_MS = 250;
const CACHE_LIMIT = 50;

const svgCache = new Map();          // key -> svg string
const pendingTimers = new WeakMap(); // container element -> timeout id
let mermaidPromise = null;
let configuredTheme = null;
let themeObserved = false;
let idCounter = 0;

function currentTheme() {
    return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'default';
}

function keyFor(theme, source) {
    return theme + '\u0000' + source;
}

function sourceOf(element) {
    // Cached on the element because rendering replaces the text content with the SVG, and a theme
    // change has to be able to re-render from the original source.
    if (element.dataset.mermaidSource === undefined) {
        element.dataset.mermaidSource = element.textContent ?? '';
    }
    return element.dataset.mermaidSource;
}

function cacheSvg(key, svg) {
    if (svgCache.size >= CACHE_LIMIT) {
        svgCache.delete(svgCache.keys().next().value);
    }
    svgCache.set(key, svg);
}

function applySvg(element, key, svg) {
    if (!element.isConnected) {
        return;
    }
    element.innerHTML = svg;
    element.classList.remove('mermaid-error');
    element.removeAttribute('title');
    element.dataset.mermaidKey = key;
}

function applyError(element, key, message) {
    if (!element.isConnected) {
        return;
    }
    element.textContent = sourceOf(element);
    element.classList.add('mermaid-error');
    element.title = message || 'The diagram could not be rendered.';
    // Stamped anyway: a broken diagram should be retried when its source (or the theme) changes,
    // not on every single re-render.
    element.dataset.mermaidKey = key;
}

function loadMermaid() {
    mermaidPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = new URL('lib/mermaid/dist/mermaid.min.js', document.baseURI).href;
        script.onload = () => resolve(window.mermaid);
        script.onerror = () => {
            mermaidPromise = null; // let a later attempt retry
            reject(new Error('Failed to load mermaid.'));
        };
        document.head.appendChild(script);
    });
    return mermaidPromise;
}

function configure(mermaid, theme) {
    if (configuredTheme === theme) {
        return;
    }
    // securityLevel 'strict' is mermaid's default and is kept explicitly: it sanitizes the SVG it
    // produces and disables click/script directives inside a diagram.
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme });
    configuredTheme = theme;
}

function observeTheme() {
    if (themeObserved) {
        return;
    }
    themeObserved = true;
    // Settings -> Dark mode flips data-bs-theme live with no reload, and Blazor keeps the same
    // <html> element across in-app navigation, so the DOM is the only reliable source of truth.
    new MutationObserver(() => {
        if (currentTheme() === configuredTheme) {
            return;
        }
        for (const element of document.querySelectorAll('pre.mermaid[data-mermaid-key]')) {
            delete element.dataset.mermaidKey;
        }
        for (const container of document.querySelectorAll('.markdown-preview')) {
            renderDiagrams(container);
        }
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });
}

async function renderPending(container) {
    const theme = currentTheme();
    let mermaid;
    try {
        mermaid = await loadMermaid();
        configure(mermaid, theme);
    } catch (error) {
        for (const element of container.querySelectorAll('pre.mermaid')) {
            applyError(element, keyFor(theme, sourceOf(element)), error.message);
        }
        return;
    }

    for (const element of container.querySelectorAll('pre.mermaid')) {
        const source = sourceOf(element);
        const key = keyFor(theme, source);
        if (element.dataset.mermaidKey === key || source.trim() === '') {
            element.dataset.mermaidKey = key; // an empty fence is "done", not an error
            continue;
        }

        const cached = svgCache.get(key);
        if (cached !== undefined) {
            applySvg(element, key, cached);
            continue;
        }

        const id = 'mermaid-' + (idCounter++);
        try {
            // suppressErrors: parse returns falsy instead of throwing mermaid's "error bomb" SVG.
            const valid = await mermaid.parse(source, { suppressErrors: true });
            if (!valid) {
                applyError(element, key, 'Invalid mermaid syntax.');
                continue;
            }
            const { svg } = await mermaid.render(id, source);
            cacheSvg(key, svg);
            applySvg(element, key, svg);
        } catch (error) {
            applyError(element, key, error?.message);
        } finally {
            // mermaid appends a temporary measuring element to <body> and can leave it behind.
            document.getElementById('d' + id)?.remove();
        }
    }
}

function schedule(container) {
    clearTimeout(pendingTimers.get(container));
    pendingTimers.set(container, setTimeout(() => {
        pendingTimers.delete(container);
        renderPending(container);
    }, DEBOUNCE_MS));
}

export function renderDiagrams(container) {
    if (!container) {
        return;
    }
    observeTheme();

    const theme = currentTheme();
    let needsWork = false;

    // Synchronous first pass: anything already rendered is left alone, and anything whose SVG is
    // cached is repainted right now — that's what keeps a keystroke from flashing the raw source of
    // every other diagram in the note.
    for (const element of container.querySelectorAll('pre.mermaid')) {
        const source = sourceOf(element);
        const key = keyFor(theme, source);
        if (element.dataset.mermaidKey === key) {
            continue;
        }
        const cached = svgCache.get(key);
        if (cached !== undefined) {
            applySvg(element, key, cached);
        } else if (source.trim() === '') {
            element.dataset.mermaidKey = key;
        } else {
            needsWork = true;
        }
    }

    if (needsWork) {
        schedule(container);
    }
}
