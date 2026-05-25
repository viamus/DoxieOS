// DoxieOS — xterm.js + SignalR bridge for the /consoles page.
// One attach() call per terminal mount. Sessions persist server-side,
// so navigating away and back re-attaches with replayed scrollback.
window.doxieOs = window.doxieOs || {};
window.doxieOs.terminals = window.doxieOs.terminals || {};

(function () {
    const handles = new Map(); // mountId -> { term, fit, conn, sessionId }

    async function attach(mountId, sessionId) {
        if (!mountId || !sessionId) return;
        const mountElement = document.getElementById(mountId);
        if (!mountElement) {
            // Element not mounted yet — Blazor's @key rotation is async.
            // Caller should retry on next render tick.
            return;
        }
        // If a stale handle still exists for this mountId (hot-reload,
        // navigation churn), tear it down before binding the new one.
        if (handles.has(mountId)) {
            await detach(mountId);
        }

        const term = new window.Terminal({
            convertEol: false,
            // Cursor blink off — Claude's Ink-based TUI repaints the
            // screen aggressively, and a blinking xterm cursor on top
            // creates visible flicker overlapping Claude's own cursor.
            cursorBlink: false,
            // Consolas first because it's always pre-loaded on Windows;
            // Cascadia Code is nicer but loads async on some installs,
            // and xterm measures char width on first render — if the
            // font swaps after measuring, cols/rows get off by a few
            // and Claude's box-drawing UI wraps at the wrong column.
            fontFamily: "Consolas, 'Cascadia Code', 'Courier New', monospace",
            fontSize: 13,
            theme: {
                background: '#1e1e1e',
                foreground: '#e6e6e6',
                cursor: '#d97757', // Tigerlily — brand accent
                selectionBackground: 'rgba(217, 119, 87, 0.35)',
            },
            scrollback: 5000,
            allowProposedApi: true,
            // Tell xterm we're connected to a winpty backend (Pty.Net
            // 0.1.16-pre uses winpty, not ConPTY). This switches
            // xterm's input/wrap heuristics to match what winpty emits
            // — without it Claude's full-screen redraws (Ink TUI) leave
            // visual artifacts and stale cells.
            windowsPty: { backend: 'winpty' },
        });
        const fit = new window.FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(mountElement);

        // WebGL renderer — much faster than the default canvas renderer
        // and the standard fix for xterm artifacts under heavy TUI
        // redraws. Claude's Ink-based UI repaints the entire visible
        // region on every render tick; canvas can't keep up cleanly,
        // WebGL can. Falls back gracefully if the GPU context is lost
        // (rare; usually a tab-switch or driver hiccup).
        try {
            const webgl = new window.WebglAddon.WebglAddon();
            webgl.onContextLoss(() => { try { webgl.dispose(); } catch (_) { } });
            term.loadAddon(webgl);
        } catch (_) {
            // WebGL not available (very old GPU or hardware-accel disabled
            // in the browser) — xterm falls back to canvas. Acceptable.
        }

        // Connect SignalR FIRST. The server defers the claude spawn
        // until it gets the first Resize call from us — so until we're
        // connected and subscribed, no spawn can happen. The staggered
        // fits below explicitly send Resize via this connection (not
        // via xterm's onResize, which doesn't fire if dims didn't
        // change from the 80×24 default).
        const conn = new window.signalR.HubConnectionBuilder()
            .withUrl('/hubs/console')
            .withAutomaticReconnect()
            .build();

        conn.on('Output', (data) => {
            if (typeof data === 'string' && data.length > 0) term.write(data);
        });
        conn.on('Status', (status) => {
            // Show a thin status banner inline as a dimmed line so the
            // user knows when claude exited or the session was removed.
            if (status === 'Exited' || status === 'Removed') {
                term.write(`\r\n\x1b[2;33m[session ${status.toLowerCase()}]\x1b[0m\r\n`);
            }
        });

        try {
            await conn.start();
            await conn.invoke('Subscribe', sessionId);
        } catch (err) {
            term.write(`\r\n\x1b[31mFailed to attach: ${err}\x1b[0m\r\n`);
            return;
        }

        // Keystrokes from the terminal go to the PTY's stdin.
        term.onData((data) => {
            conn.invoke('Send', sessionId, data).catch(() => { /* connection torn down */ });
        });

        // safeFit fits xterm AND explicitly sends the current dims to
        // the server. We can't rely on term.onResize for the initial
        // size — xterm only fires onResize when dims actually change,
        // so if the first fit returns the default 80×24 (because the
        // mount hadn't laid out yet), no event fires and the deferred
        // claude spawn never triggers. Always-send fixes that.
        const safeFit = () => {
            try {
                fit.fit();
                if (term.cols > 0 && term.rows > 0) {
                    conn.invoke('Resize', sessionId, term.cols, term.rows).catch(() => { });
                }
            } catch (_) { /* not laid out */ }
        };

        // Initial fit is staggered across four timing windows because
        // WebGL + winpty + Blazor render passes each settle on their
        // own clock. Why all of them are needed:
        //   - 0ms (post rAFs): the terminal DOM is laid out
        //   - 100ms: WebGL addon's glyph atlas has built
        //   - 300ms: cell-size service has stabilised after first paint
        //   - 600ms: defensive net for slower machines / dev-tools open
        const scheduleInitialFit = () => {
            requestAnimationFrame(() => requestAnimationFrame(() => {
                safeFit();
                setTimeout(safeFit, 100);
                setTimeout(safeFit, 300);
                setTimeout(safeFit, 600);
            }));
        };
        if (document.fonts && document.fonts.ready) {
            document.fonts.ready.then(scheduleInitialFit);
        } else {
            scheduleInitialFit();
        }

        // ResizeObserver catches the mount element's actual size changes
        // — initial layout, MudGrid breakpoint shifts, sidebar collapse,
        // browser zoom — without relying on the window resize event
        // (which doesn't fire for layout-only changes). Debounced via
        // rAF so a burst of size events coalesces into a single fit().
        let pendingFit = false;
        const ro = new ResizeObserver(() => {
            if (pendingFit) return;
            pendingFit = true;
            requestAnimationFrame(() => {
                pendingFit = false;
                safeFit();
            });
        });
        ro.observe(mountElement);

        handles.set(mountId, { term, fit, conn, sessionId, ro });
        term.focus();
    }

    async function detach(mountId) {
        const h = handles.get(mountId);
        if (!h) return;
        handles.delete(mountId);
        try { h.ro.disconnect(); } catch (_) { }
        try { await h.conn.invoke('Unsubscribe', h.sessionId); } catch (_) { }
        try { await h.conn.stop(); } catch (_) { }
        try { h.term.dispose(); } catch (_) { }
    }

    function fit(mountId) {
        const h = handles.get(mountId);
        if (!h) return;
        try { h.fit.fit(); } catch (_) { }
    }

    // Programmatic input injection. Used by the Agent / Workflow Builder
    // pages to auto-send the activating slash command (e.g. /agent-builder)
    // immediately after attach, so the user sees the skill take over
    // without having to type. PTY buffers stdin until claude reads, so
    // sending right after attach is safe even if claude hasn't fully
    // booted its prompt loop yet.
    function sendInput(mountId, text) {
        const h = handles.get(mountId);
        if (!h || !text) return Promise.resolve(false);
        return h.conn.invoke('Send', h.sessionId, text)
            .then(() => true)
            .catch(() => false);
    }

    window.doxieOs.terminals.attach = attach;
    window.doxieOs.terminals.detach = detach;
    window.doxieOs.terminals.fit = fit;
    window.doxieOs.terminals.sendInput = sendInput;
})();
