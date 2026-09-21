window.appInterop = {
    _scrollHandler: null,
    _linkClickHandler: null,
    _dotNetRef: null,
    _dropDotNetRef: null,
    _tabDropDotNetRef: null,
    _draggedTabId: null,
    _lastActiveId: '',

    applyTheme: function (theme) {
        const r = document.documentElement.style;
        r.setProperty('--theme-bg', theme.backgroundColor);
        r.setProperty('--theme-surface', theme.surfaceColor);
        r.setProperty('--theme-text', theme.textColor);
        r.setProperty('--theme-heading', theme.headingColor);
        r.setProperty('--theme-accent', theme.accentColor);
        r.setProperty('--theme-border', theme.borderColor);
        r.setProperty('--theme-link', theme.linkColor);
        r.setProperty('--theme-code-color', theme.codeColor);
        r.setProperty('--theme-code-bg', theme.codeBackground);
        r.setProperty('--theme-pre-bg', theme.preBackground);
        r.setProperty('--theme-table-header-bg', theme.tableHeaderBackground);
        r.setProperty('--theme-table-row-alt', theme.tableRowAltBackground);
        r.setProperty('--theme-table-border', theme.tableBorderColor);
        r.setProperty('--theme-font', theme.fontFamily);
        r.setProperty('--theme-code-font', theme.codeFontFamily);
        r.setProperty('--theme-font-size', theme.fontSizePx + 'px');
    },

    initDropZone: function (dotNetRef) {
        this._dropDotNetRef = dotNetRef;
        const self = this;

        // Visual feedback only — MAUI's DropGestureRecognizer handles actual file opening.
        // A tab drag must not light up the "you're dropping a file" overlay: the
        // webview2-dnd-polyfill (see enableWebView2DragPolyfillIfNeeded) dispatches plain
        // Event objects that bubble to document, which instanceof DragEvent excludes — but a
        // genuine native tab drag (real browsers, this app's own --serve mode) dispatches real
        // DragEvents too, so that check alone doesn't exclude those. Checking
        // self._draggedTabId (set by initTabDragDrop for the duration of any tab drag,
        // native or polyfilled) covers both.
        document.addEventListener('dragover', function (e) {
            if (!(e instanceof DragEvent) || self._draggedTabId) return;
            e.preventDefault();
            document.body.classList.add('drag-over');
        });

        document.addEventListener('dragleave', function (e) {
            if (!(e instanceof DragEvent) || self._draggedTabId) return;
            if (e.relatedTarget === null) {
                document.body.classList.remove('drag-over');
            }
        });

        document.addEventListener('drop', function (e) {
            if (!(e instanceof DragEvent) || self._draggedTabId) return;
            e.preventDefault();
            document.body.classList.remove('drag-over');
        });
    },

    // Native HTML5 drag-and-drop is broken inside a WinUI3-hosted WebView2: dragstart fires
    // but dragover/drop never do, so the OS-level drag session dies before it can go anywhere
    // (confirmed via WebView2 DevTools; tracked upstream as dotnet/maui#2205 and
    // microsoft-ui-xaml#10576, both still open/blocked on WebView2 itself). The vendored
    // mouse-event polyfill in webview2-dnd-polyfill.js works around it by simulating the whole
    // drag from mousedown/mousemove/mouseup instead of relying on the native drag session —
    // load it only for that shell; real browsers (including this app's own --serve mode) and
    // other platforms' WebViews don't have the bug and should keep using native drag.
    enableWebView2DragPolyfillIfNeeded: function (needed) {
        if (!needed) return Promise.resolve();

        // Blazor's JS interop awaits a returned promise before the caller (OnAfterRenderAsync)
        // moves on to initTabDragDrop — without that, a drag started before this script tag
        // finishes loading would still hit the broken native path and silently fail.
        const existing = document.querySelector('script[data-webview2-dnd-polyfill]');
        if (existing) {
            return existing.dataset.loaded === 'true'
                ? Promise.resolve()
                : new Promise(function (resolve) { existing.addEventListener('load', resolve, { once: true }); });
        }

        return new Promise(function (resolve) {
            const script = document.createElement('script');
            script.src = 'js/webview2-dnd-polyfill.js';
            script.dataset.webview2DndPolyfill = 'true';
            script.onload = function () { script.dataset.loaded = 'true'; resolve(); };
            // Best-effort — if the script 404s or errors, don't block tab dragging forever;
            // native drag stays broken on this shell, but at least nothing hangs.
            script.onerror = resolve;
            document.head.appendChild(script);
        });
    },

    // Tab reordering runs entirely client-side (drag visuals + hit-testing) and only calls
    // back into .NET once, at drop — binding every dragover to a Blazor Server round trip
    // fires on every pixel of mouse movement and the resulting re-renders were enough to make
    // the browser lose track of the actual drop target mid-drag. Delegated on `document` (like
    // initDropZone) rather than the tab strip itself, since that element doesn't exist until
    // at least one tab is open and gets recreated whenever the last tab closes and reopens.
    initTabDragDrop: function (dotNetRef) {
        if (this._tabDragDropInitialized) {
            this._tabDropDotNetRef = dotNetRef;
            return;
        }
        this._tabDragDropInitialized = true;
        this._tabDropDotNetRef = dotNetRef;
        const self = this;

        const clearDragOver = function () {
            document.querySelectorAll('.tab.drag-over').forEach(function (el) { el.classList.remove('drag-over'); });
        };

        // Tracks whether the gesture that's about to start a drag began on the close button.
        // e.target for a native 'dragstart' is always the draggable ancestor (.tab) regardless
        // of which descendant the pointer actually went down on, so checking e.target against
        // .tab-close inside the dragstart handler can never match — mousedown is the only
        // reliable point to observe where the gesture actually began.
        let dragStartedOnCloseButton = false;

        // The webview2-dnd-polyfill starts a (simulated) drag from any mousedown on a
        // draggable ancestor, which includes the close button nested inside .tab — without
        // this, clicking Close would also be read as a self-drop-to-end reorder right before
        // the tab closes. A capture-phase listener runs before the polyfill's own (bubble-phase)
        // mousedown handler on the same document target, so stopping it here keeps the
        // polyfill from ever treating that click as a drag start.
        document.addEventListener('mousedown', function (e) {
            dragStartedOnCloseButton = !!e.target.closest('.tab-close');
            if (dragStartedOnCloseButton) e.stopImmediatePropagation();
        }, true);

        // Blazor's @onkeydown:preventDefault directive is decided once per render, not per
        // keystroke, so it can't conditionally suppress only Space while leaving Tab and other
        // keys alone — Space is the browser's default page-scroll key on any focusable
        // non-form element (like the tabindex="0" .tab-title span), so without this, using it
        // to activate a tab also scrolls the page.
        document.addEventListener('keydown', function (e) {
            if (e.key === ' ' && e.target.closest('.tab-title')) e.preventDefault();
        });

        document.addEventListener('dragstart', function (e) {
            // draggable="true" on .tab makes its whole subtree a drag source — including the
            // nested close button — so a native (non-polyfilled) drag can still start there.
            // preventDefault cancels that native drag entirely, which is what lets the
            // browser's normal click (and Blazor's CloseTab) fire instead; the mousedown guard
            // above only covers the polyfill's own simulated path, not real native DnD.
            if (dragStartedOnCloseButton) {
                e.preventDefault();
                return;
            }
            const tab = e.target.closest('.tab');
            if (!tab) return;
            self._draggedTabId = tab.dataset.docId;
            tab.classList.add('dragging');
            e.dataTransfer.effectAllowed = 'move';
            // Chromium doesn't strictly require data for a same-page drag, but some browsers
            // refuse to start the drag at all without it.
            try { e.dataTransfer.setData('text/plain', tab.dataset.docId); } catch { /* best-effort */ }
        });

        // e.target on a dragover/drop event can lag behind the pointer's actual position
        // (observed during a fast/long drag) — elementFromPoint at the event's own
        // coordinates is the authoritative source for what's really under the cursor. But the
        // webview2-dnd-polyfill's synthetic 'drop' event (unlike its 'dragover') never sets
        // clientX/clientY, so elementFromPoint(0, 0) would resolve to whatever's in the
        // top-left corner instead — fall back to e.target in that case, which the polyfill
        // already dispatches on the correct element.
        const elementAt = function (e) {
            if (e.clientX || e.clientY) {
                const el = document.elementFromPoint(e.clientX, e.clientY);
                if (el) return el;
            }
            return e.target;
        };

        document.addEventListener('dragover', function (e) {
            if (!self._draggedTabId) return;
            const el = elementAt(e);
            const strip = el && el.closest('.tab-strip');
            // Once the pointer leaves the strip, no tab is a valid drop target anymore — clear
            // any leftover highlight instead of leaving the last-hovered tab looking like the
            // drop target for the rest of the drag.
            if (!strip) { clearDragOver(); return; }
            e.preventDefault();

            clearDragOver();
            const tab = el.closest('.tab');
            if (tab && tab.dataset.docId !== self._draggedTabId) {
                tab.classList.add('drag-over');
            }
        });

        document.addEventListener('drop', function (e) {
            if (!self._draggedTabId) return;
            // preventDefault unconditionally as soon as we know this is our own drag, even if
            // it lands outside the tab strip — otherwise the browser's own default drop
            // handling can run over ordinary page content (e.g. consuming the dragged
            // document's id, set as text/plain data, in whatever way it treats dropped text).
            // Whether to actually reorder is a separate decision, made below via the strip check.
            e.preventDefault();

            const el = elementAt(e);
            const strip = el && el.closest('.tab-strip');
            if (!strip) {
                clearDragOver();
                document.querySelectorAll('.tab.dragging').forEach(function (el) { el.classList.remove('dragging'); });
                self._draggedTabId = null;
                return;
            }

            // A null targetId means "no specific tab" to OnTabDropped/ReorderDocument, which
            // moves the dragged tab to the end — so a self-drop must resolve to its own id
            // (a harmless no-op via ReorderDocument's doc == target check), not null, or
            // picking a tab up and putting it back down would send it to the end instead.
            const tab = el.closest('.tab');
            const targetId = tab ? tab.dataset.docId : null;
            const draggedId = self._draggedTabId;

            clearDragOver();
            document.querySelectorAll('.tab.dragging').forEach(function (el) { el.classList.remove('dragging'); });
            self._draggedTabId = null;

            if (self._tabDropDotNetRef) {
                self._tabDropDotNetRef.invokeMethodAsync('OnTabDropped', draggedId, targetId);
            }
        });

        document.addEventListener('dragend', function () {
            self._draggedTabId = null;
            clearDragOver();
            document.querySelectorAll('.tab.dragging').forEach(function (el) { el.classList.remove('dragging'); });
        });
    },

    processContent: async function (dotNetRef) {
        this._dotNetRef = dotNetRef;
        this._lastActiveId = '';

        // Clean up any mermaid divs injected outside Blazor's DOM control
        document.querySelectorAll('.mermaid-rendered').forEach(function (el) { el.remove(); });
        document.querySelectorAll('pre.mermaid[data-mermaid-processed]').forEach(function (el) {
            el.removeAttribute('data-mermaid-processed');
            el.style.display = '';
        });

        // Same story for the CSV export toolbars injected next to tables — Blazor's
        // diffing doesn't know about them, so they'd otherwise survive a content refresh.
        document.querySelectorAll('.table-export-toolbar').forEach(function (el) { el.remove(); });
        document.querySelectorAll('table[data-csv-export-processed]').forEach(function (el) {
            el.removeAttribute('data-csv-export-processed');
        });

        this._setupScrollTracking();
        this._setupLinkHandling();
        this._renderDiff();
        this._injectTableExportButtons();
        await this._renderMermaid();
    },

    // Takes a .NET DotNetStreamReference instead of an inline string, so large diffs don't
    // ride over a single Blazor Server SignalR message (which has a default size limit and
    // can disconnect the circuit) — the .NET-to-JS transfer itself is chunked, even though
    // arrayBuffer() below buffers the full result into memory on the JS side afterward.
    downloadFileFromStream: async function (fileName, contentStreamReference) {
        const arrayBuffer = await contentStreamReference.arrayBuffer();
        const blob = new Blob([arrayBuffer], { type: 'text/plain' });
        this._triggerDownload(fileName, blob);
    },

    _triggerDownload: function (fileName, blob) {
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName;
        anchor.style.display = 'none';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        // Some browsers cancel the download if the blob URL is revoked immediately.
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    },

    scrollToHeading: function (id) {
        const el = document.getElementById(id);
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    },

    _setupLinkHandling: function () {
        const container = document.querySelector('.document-content');
        if (!container) return;

        if (this._linkClickHandler) {
            container.removeEventListener('click', this._linkClickHandler);
        }

        const self = this;
        this._linkClickHandler = function (e) {
            const anchor = e.target.closest('a[href]');
            if (!anchor) return;

            const href = anchor.getAttribute('href');
            if (!href) return;

            const normalized = href.trim().toLowerCase();
            if (normalized.startsWith('#')) return;

            // Block dangerous schemes before any navigation
            if (normalized.startsWith('javascript:') || normalized.startsWith('data:')) {
                e.preventDefault();
                return;
            }

            e.preventDefault();
            if (!self._dotNetRef) return;

            // Treat anything with a scheme (e.g. http, https, mailto, file, tel) or
            // protocol-relative URLs as external; bare/relative paths go to OpenRelativeLink
            const hasScheme = /^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//');
            if (hasScheme) {
                self._dotNetRef.invokeMethodAsync('OpenExternalUrl', href);
            } else {
                self._dotNetRef.invokeMethodAsync('OpenRelativeLink', href);
            }
        };

        container.addEventListener('click', this._linkClickHandler);
    },

    _setupScrollTracking: function () {
        const container = document.querySelector('.document-content');
        if (!container) return;

        if (this._scrollHandler) {
            container.removeEventListener('scroll', this._scrollHandler);
        }

        const self = this;
        this._scrollHandler = function () {
            const headings = container.querySelectorAll('h1[id], h2[id], h3[id], h4[id], h5[id], h6[id]');
            let activeId = '';
            const containerRect = container.getBoundingClientRect();

            for (const heading of headings) {
                const rect = heading.getBoundingClientRect();
                if (rect.top - containerRect.top <= 80) {
                    activeId = heading.id;
                }
            }

            if (activeId && activeId !== self._lastActiveId) {
                self._lastActiveId = activeId;
                if (self._dotNetRef) {
                    self._dotNetRef.invokeMethodAsync('SetActiveHeading', activeId);
                }
            }
        };

        container.addEventListener('scroll', this._scrollHandler, { passive: true });
        setTimeout(this._scrollHandler, 100);
    },

    _renderDiff: function () {
        document.querySelectorAll('code.language-diff').forEach(function (code) {
            if (code.dataset.diffProcessed) return;
            code.dataset.diffProcessed = 'true';

            const escaped = code.textContent
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;');

            const lines = escaped.split('\n');
            if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();

            let inHunk = false;
            code.innerHTML = lines.map(function (line) {
                let cls = 'diff-context';
                if (line.startsWith('@@')) {
                    cls = 'diff-hunk';
                    inHunk = true;
                } else if (!inHunk && (line.startsWith('+++') || line.startsWith('---'))) {
                    cls = 'diff-meta';
                } else if (line.startsWith('+')) cls = 'diff-added';
                else if (line.startsWith('-')) cls = 'diff-removed';
                return '<span class="' + cls + '">' + line + '</span>';
            }).join('');
        });
    },

    // Scoped to the plain (non-diff) content container — diff-view tables are out of scope.
    _injectTableExportButtons: function () {
        const container = document.querySelector('.document-content:not(.diff-content)');
        if (!container) return;

        const self = this;
        container.querySelectorAll('table:not([data-csv-export-processed])').forEach(function (table) {
            table.setAttribute('data-csv-export-processed', 'true');

            const toolbar = document.createElement('div');
            toolbar.className = 'table-export-toolbar';

            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'table-export-btn';
            btn.title = 'Export as CSV';
            btn.setAttribute('aria-label', 'Export as CSV');
            btn.innerHTML = '<i class="bi bi-filetype-csv"></i>';
            btn.addEventListener('click', async function () {
                if (!self._dotNetRef) return;
                try {
                    const csv = self._tableToCsv(table);
                    const tableIndex = Array.from(container.querySelectorAll('table')).indexOf(table);

                    // Only a filename crosses the wire here — in service mode the CSV itself
                    // never leaves the browser (avoids the large-SignalR-message risk SaveDiff
                    // avoids in the opposite direction); the native path saves via .NET's
                    // FileSaver instead.
                    const info = await self._dotNetRef.invokeMethodAsync('GetTableCsvExportInfo', tableIndex);
                    if (info.isServiceMode) {
                        self._triggerDownload(info.fileName, new Blob([csv], { type: 'text/csv' }));
                    } else {
                        await self._dotNetRef.invokeMethodAsync('SaveTableCsv', csv, info.fileName);
                    }
                } catch (e) {
                    console.error('CSV export error:', e);
                }
            });

            toolbar.appendChild(btn);
            table.parentNode.insertBefore(toolbar, table);
        });
    },

    _tableToCsv: function (table) {
        const lines = [];
        table.querySelectorAll('tr').forEach(function (row) {
            const fields = Array.from(row.querySelectorAll('th, td')).map(function (cell) {
                // textContent alone drops <br> entirely (no "\n", no space) — replace it with
                // a literal newline on a clone first so line breaks survive into the CSV field.
                const clone = cell.cloneNode(true);
                clone.querySelectorAll('br').forEach(function (br) { br.replaceWith('\n'); });

                // Collapse runs of spaces/tabs, but keep the newlines from <br> above intact
                // so the quoting below actually has a "\n" to quote.
                const text = clone.textContent.replace(/[ \t]+/g, ' ').trim();
                return /[",\r\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
            });
            lines.push(fields.join(','));
        });
        return lines.join('\r\n');
    },

    _renderMermaid: async function () {
        if (typeof mermaid === 'undefined') return;

        // Markdig with UseAdvancedExtensions renders mermaid blocks as <pre class="mermaid">
        const blocks = document.querySelectorAll('pre.mermaid:not([data-mermaid-processed])');
        if (blocks.length === 0) return;

        for (let index = 0; index < blocks.length; index++) {
            const pre = blocks[index];
            pre.setAttribute('data-mermaid-processed', 'true');

            const div = document.createElement('div');
            div.className = 'mermaid-rendered';
            div.id = 'mermaid-' + Date.now() + '-' + index;

            // Hide the <pre> but keep it in DOM so Blazor can still manage it
            pre.style.display = 'none';
            pre.insertAdjacentElement('afterend', div);

            await this._renderMermaidInto(div, pre.textContent);
        }
    },

    // Renders one diagram, then checks for overlapping x-axis category labels and
    // re-renders once with them rotated if needed. xyChart-beta (mermaid's bar/line chart)
    // lays out labels with no collision detection of its own, so long or numerous category
    // labels can overlap the bar they sit under (mermaid-js/mermaid#5926); every other
    // diagram type is unaffected since _xAxisLabelsOverlap only matches xyChart's own
    // bottom-axis label group, so this is a no-op for them.
    _renderMermaidInto: async function (div, source) {
        try {
            const rendered = await mermaid.render(div.id + '-svg', source);
            div.innerHTML = rendered.svg;
            if (rendered.bindFunctions) rendered.bindFunctions(div);

            if (this._xAxisLabelsOverlap(div)) {
                // %%{init: ...}%% overrides config for just this render call, so it can't
                // leak into diagrams rendered before or after it (unlike calling
                // mermaid.initialize() again, which would change the global config).
                const rotated = '%%{init: {"xyChart": {"xAxis": {"labelRotation": 90}}}}%%\n' + source;
                const rotatedRendered = await mermaid.render(div.id + '-svg-rotated', rotated);
                div.innerHTML = rotatedRendered.svg;
                if (rotatedRendered.bindFunctions) rotatedRendered.bindFunctions(div);
            }
        } catch (e) {
            console.error('Mermaid rendering error:', e);
        }
    },

    // True when consecutive x-axis category labels' bounding boxes overlap horizontally.
    // Only xyChart-beta emits a 'bottom-axis' axis group with a nested 'label' group, so
    // this is always false for other diagram types (flowcharts, sequence diagrams, etc.).
    // Deliberately uses getBoundingClientRect() (post-transform, viewport space) rather than
    // getBBox() (pre-transform, local space) — each label's actual x/y come from a
    // `transform="translate(...)"` on the <text> itself with x="0" y="0" attributes, so
    // getBBox() would return nearly the same small box centered on the local origin for
    // every label regardless of where it actually renders, making every multi-label chart
    // look "overlapping".
    _xAxisLabelsOverlap: function (container) {
        const labels = container.querySelectorAll('g.bottom-axis g.label text');
        if (labels.length < 2) return false;

        const boxes = Array.from(labels)
            .map(function (el) { return el.getBoundingClientRect(); })
            .sort(function (a, b) { return a.left - b.left; });

        for (let i = 1; i < boxes.length; i++) {
            if (boxes[i].left < boxes[i - 1].right) return true;
        }
        return false;
    },

    dispose: function () {
        const container = document.querySelector('.document-content');
        if (container) {
            if (this._scrollHandler) container.removeEventListener('scroll', this._scrollHandler);
            if (this._linkClickHandler) container.removeEventListener('click', this._linkClickHandler);
        }
        this._scrollHandler = null;
        this._linkClickHandler = null;
        this._dotNetRef = null;
        this._dropDotNetRef = null;
        this._tabDropDotNetRef = null;
        this._lastActiveId = '';
    }
};
