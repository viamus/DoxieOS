// DoxieOS — browser-native voice-to-text using the Web Speech API.
// Used by the VoiceInputButton Blazor component to give every PTY
// terminal (Agent Builder, Workflow Builder, Consoles) a "speak instead
// of type" affordance.
//
// Supported in Chromium-based browsers (Chrome, Edge) and Safari. Brave
// disables it by default. Firefox doesn't ship it. The component reports
// an unsupported state before ever asking for the mic.
//
// Privacy note: Chrome routes the audio to Google's STT service. For a
// single-user dev tool this is acceptable; if it ever needs to be local-
// only, swap out for a server-side Whisper later — the C# side just
// receives final transcripts so the wire shape doesn't change.
window.doxieOs = window.doxieOs || {};

window.doxieOs.voice = (function () {
    const handles = new Map(); // id -> SpeechRecognition

    function isSupported() {
        return 'SpeechRecognition' in window || 'webkitSpeechRecognition' in window;
    }

    /**
     * Start a recognition session, dispatching events back to a Blazor
     * DotNetObjectReference. Returns true if the recognizer started, false
     * if the browser doesn't support the API or start() rejected.
     */
    function start(id, dotnetRef, lang) {
        stop(id); // any leftover handle for this id (rapid toggle safety)
        const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SR) return false;
        const rec = new SR();
        rec.lang = lang || 'pt-BR';
        // Continuous + interim — feels live, lets the user stop only when
        // they're sure the whole thought is in. Without continuous, Chrome
        // auto-stops on the first ~1s pause and the recognizer dies mid-
        // sentence.
        rec.continuous = true;
        rec.interimResults = true;
        rec.maxAlternatives = 1;

        rec.onresult = (e) => {
            let interim = '';
            let finalText = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const transcript = e.results[i][0].transcript;
                if (e.results[i].isFinal) finalText += transcript;
                else interim += transcript;
            }
            if (interim) {
                dotnetRef.invokeMethodAsync('OnInterimResult', interim).catch(() => { });
            }
            if (finalText) {
                dotnetRef.invokeMethodAsync('OnFinalResult', finalText).catch(() => { });
            }
        };
        rec.onerror = (e) => {
            // Useful errors: 'no-speech' (silence), 'audio-capture' (no
            // mic), 'not-allowed' (permission denied), 'aborted' (we
            // called stop), 'network' (Google STT unreachable).
            const err = (e && e.error) ? e.error : 'unknown';
            dotnetRef.invokeMethodAsync('OnRecognitionError', err).catch(() => { });
        };
        rec.onend = () => {
            handles.delete(id);
            dotnetRef.invokeMethodAsync('OnRecognitionEnd').catch(() => { });
        };

        try {
            rec.start();
            handles.set(id, rec);
            return true;
        } catch (_) {
            return false;
        }
    }

    function startIntoInput(id, dotnetRef, lang, selector) {
        stop(id);
        const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SR) return false;

        const root = selector ? document.querySelector(selector) : null;
        const input = root && root.matches && root.matches('input, textarea')
            ? root
            : root && root.querySelector
                ? root.querySelector('textarea, input')
                : null;
        if (!input) return false;

        const rec = new SR();
        rec.lang = lang || 'pt-BR';
        rec.continuous = true;
        rec.interimResults = true;
        rec.maxAlternatives = 1;

        const baseValue = (input.value || '').trimEnd();
        let committed = '';

        const compose = (interim) => {
            const parts = [];
            if (baseValue) parts.push(baseValue);
            if (committed) parts.push(committed);
            if (interim) parts.push(interim.trim());
            const value = parts.join(' ').trimStart();
            input.value = value;
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            dotnetRef.invokeMethodAsync('OnInputUpdated', value).catch(() => { });
        };

        rec.onresult = (e) => {
            let interim = '';
            let finalText = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const transcript = e.results[i][0].transcript;
                if (e.results[i].isFinal) finalText += transcript;
                else interim += transcript;
            }

            if (finalText) {
                committed = `${committed} ${finalText}`.trim();
                dotnetRef.invokeMethodAsync('OnFinalResult', finalText).catch(() => { });
            }
            if (interim) {
                dotnetRef.invokeMethodAsync('OnInterimResult', interim).catch(() => { });
            }
            compose(interim);
        };

        rec.onerror = (e) => {
            const err = (e && e.error) ? e.error : 'unknown';
            dotnetRef.invokeMethodAsync('OnRecognitionError', err).catch(() => { });
        };
        rec.onend = () => {
            handles.delete(id);
            dotnetRef.invokeMethodAsync('OnRecognitionEnd').catch(() => { });
        };

        try {
            rec.start();
            handles.set(id, rec);
            return true;
        } catch (_) {
            return false;
        }
    }

    function stop(id) {
        const rec = handles.get(id);
        if (!rec) return;
        try { rec.stop(); } catch (_) { /* might already be stopping */ }
        handles.delete(id);
    }

    return { isSupported, start, startIntoInput, stop };
})();
