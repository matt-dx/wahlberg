window.appInterop = {
    _scrollHandler: null,
    _dotNetRef: null,
    _dropDotNetRef: null,
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

        // Visual feedback only — MAUI's DropGestureRecognizer handles actual file opening
        document.addEventListener('dragover', function (e) {
            e.preventDefault();
            document.body.classList.add('drag-over');
        });

        document.addEventListener('dragleave', function (e) {
            if (e.relatedTarget === null) {
                document.body.classList.remove('drag-over');
            }
        });

        document.addEventListener('drop', function (e) {
            e.preventDefault();
            document.body.classList.remove('drag-over');
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

        this._setupScrollTracking();
        await this._renderMermaid();
    },

    scrollToHeading: function (id) {
        const el = document.getElementById(id);
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
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

    _renderMermaid: async function () {
        if (typeof mermaid === 'undefined') return;

        // Markdig with UseAdvancedExtensions renders mermaid blocks as <pre class="mermaid">
        const blocks = document.querySelectorAll('pre.mermaid:not([data-mermaid-processed])');
        if (blocks.length === 0) return;

        const divsToRender = [];
        blocks.forEach(function (pre, index) {
            pre.setAttribute('data-mermaid-processed', 'true');

            const div = document.createElement('div');
            div.className = 'mermaid-rendered';
            div.id = 'mermaid-' + Date.now() + '-' + index;
            div.textContent = pre.textContent;

            // Hide the <pre> but keep it in DOM so Blazor can still manage it
            pre.style.display = 'none';
            pre.insertAdjacentElement('afterend', div);
            divsToRender.push(div);
        });

        if (divsToRender.length > 0) {
            try {
                await mermaid.run({ nodes: divsToRender });
            } catch (e) {
                console.error('Mermaid rendering error:', e);
            }
        }
    },

    dispose: function () {
        const container = document.querySelector('.document-content');
        if (container && this._scrollHandler) {
            container.removeEventListener('scroll', this._scrollHandler);
        }
        this._scrollHandler = null;
        this._dotNetRef = null;
        this._dropDotNetRef = null;
        this._lastActiveId = '';
    }
};
