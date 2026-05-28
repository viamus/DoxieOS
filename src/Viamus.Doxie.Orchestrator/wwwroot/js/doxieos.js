// DoxieOS — small JS helpers invoked from Blazor via IJSRuntime.
window.doxieOs = window.doxieOs || {};

// Apply highlight.js syntax coloring to a <pre><code> element. Called
// from WorkspaceFiles.razor after a code file is rendered. Safe to call
// before hljs is loaded — no-op until the script tag executes. We wipe
// the data-highlighted flag because Blazor reuses the same DOM node when
// the user clicks a different code file.
window.doxieOs.highlightCode = function (element) {
    if (!element) return;
    if (typeof window.hljs === 'undefined' || !window.hljs.highlightElement) return;
    try {
        // hljs marks the element after running; clear so re-render works.
        element.removeAttribute('data-highlighted');
        window.hljs.highlightElement(element);
    } catch (_) { /* swallow — never break the page over coloring */ }
};

window.doxieOs.scrollToBottom = function (element) {
    if (element && typeof element.scrollTo === 'function') {
        element.scrollTo({ top: element.scrollHeight, behavior: 'auto' });
    } else if (element) {
        element.scrollTop = element.scrollHeight;
    }
};

window.doxieOs.setInputValue = function (selector, value) {
    if (!selector) return false;
    const root = document.querySelector(selector);
    const input = root && root.matches && root.matches('input, textarea')
        ? root
        : root && root.querySelector
            ? root.querySelector('textarea, input')
            : null;
    if (!input) return false;
    input.value = value || '';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
};

window.doxieOs.copyText = async function (text) {
    if (!text) return false;
    try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch (_) { /* fall through */ }
    // Legacy fallback
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
    document.body.removeChild(ta);
    return ok;
};

window.doxieOs.downloadText = function (filename, content) {
    const blob = new Blob([content ?? ''], { type: 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename || 'memory.md';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 0);
};

window.doxieOs.pickAndUploadLibrary = function (force, catalogId) {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.zip,application/zip,application/x-zip-compressed';
        input.onchange = async (e) => {
            const file = e.target && e.target.files && e.target.files[0];
            if (!file) { resolve(null); return; }
            try {
                const fd = new FormData();
                fd.append('file', file, file.name);
                const params = new URLSearchParams();
                if (force) params.set('force', 'true');
                if (catalogId) params.set('catalogId', catalogId);
                const qs = params.toString() ? `?${params.toString()}` : '';
                const resp = await fetch(`/api/libraries/import${qs}`, {
                    method: 'POST',
                    body: fd,
                });
                let message = null;
                if (!resp.ok) {
                    try { message = await resp.text(); } catch (_) { message = resp.statusText; }
                }
                resolve({ ok: resp.ok, status: resp.status, filename: file.name, message });
            } catch (err) {
                resolve({ ok: false, status: 0, filename: file.name, message: String(err) });
            }
        };
        input.click();
    });
};

window.doxieOs.confirm = function (message) {
    return Promise.resolve(window.confirm(message || 'Are you sure?'));
};

window.doxieOs.createWorkspace = async function (payload) {
    try {
        const resp = await fetch('/api/workspaces', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload || {}),
        });
        let message = null;
        let name = null;
        if (resp.ok) {
            try {
                const body = await resp.json();
                name = body && body.name ? body.name : null;
            } catch (_) { /* empty body is fine */ }
        } else {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, name, message };
    } catch (err) {
        return { ok: false, status: 0, name: null, message: String(err) };
    }
};

window.doxieOs.createConsole = async function (workspaceId) {
    try {
        const resp = await fetch('/api/consoles', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ workspaceId }),
        });
        let message = null;
        let body = null;
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            id: body && body.id ? body.id : null,
            label: body && body.label ? body.label : null,
            message,
        };
    } catch (err) {
        return { ok: false, status: 0, id: null, label: null, message: String(err) };
    }
};

window.doxieOs.deleteConsole = async function (sessionId) {
    try {
        const resp = await fetch(`/api/consoles/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
        return { ok: resp.ok, status: resp.status };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.openWorkspace = async function (workspaceId, target) {
    try {
        const qs = `?target=${encodeURIComponent(target || 'folder')}`;
        const resp = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/open${qs}`, {
            method: 'POST',
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.setWorkspaceLibraries = async function (workspaceId, libraries) {
    try {
        const resp = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/libraries`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ libraries: libraries || [] }),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.setWorkspaceAgents = async function (workspaceId, agents) {
    try {
        const resp = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ agents: agents || [] }),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.deleteWorkspace = async function (workspaceId) {
    try {
        const resp = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}`, {
            method: 'DELETE',
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.exportWorkspace = function (workspaceId, includeAgents) {
    const a = document.createElement('a');
    const qs = includeAgents === false ? '?includeAgents=false' : '';
    a.href = `/api/workspaces/${encodeURIComponent(workspaceId)}/export${qs}`;
    a.download = `${workspaceId}-workspace.zip`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
};

window.doxieOs.pickWorkspaceZip = function () {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.zip,application/zip,application/x-zip-compressed';
        input.style.display = 'none';
        let settled = false;
        const settle = (val) => { if (!settled) { settled = true; resolve(val); } };
        input.addEventListener('change', () => {
            settle(input.files && input.files[0] ? input.files[0] : null);
        });
        input.addEventListener('cancel', () => settle(null));
        document.body.appendChild(input);
        input.click();
        setTimeout(() => settle(null), 60000);
    });
};

window.doxieOs.importWorkspace = async function (file, overwrite, catalogId) {
    if (!file) return { ok: false, status: 0, message: 'No file selected' };
    try {
        const fd = new FormData();
        fd.append('file', file, file.name);
        if (overwrite) fd.append('overwrite', 'true');
        if (catalogId) fd.append('catalogId', catalogId);
        const resp = await fetch('/api/workspaces/import', { method: 'POST', body: fd });
        let body = null;
        try { body = await resp.json(); } catch (_) { /* server may have returned plain text */ }
        return {
            ok: resp.ok,
            status: resp.status,
            workspaceId: body && body.workspaceId,
            href: body && body.href,
            workspaceAction: body && body.workspaceAction,
            agents: body && body.agents ? body.agents : [],
            code: body && body.code,
            message: body && (body.error || body.message),
        };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.pickAndImportWorkspace = async function (overwrite, catalogId) {
    const file = await window.doxieOs.pickWorkspaceZip();
    if (!file) return { ok: false, status: 0, message: 'cancelled', cancelled: true };
    return await window.doxieOs.importWorkspace(file, overwrite, catalogId);
};

window.doxieOs.createWorkflow = async function (payload) {
    try {
        const resp = await fetch('/api/workflows', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload || {}),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.exportWorkflow = function (workflowId, includeAgents) {
    const a = document.createElement('a');
    const qs = includeAgents === false ? '?includeAgents=false' : '';
    a.href = `/api/workflows/${encodeURIComponent(workflowId)}/export${qs}`;
    a.download = `${workflowId}-workflow.zip`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
};

window.doxieOs.pickWorkflowZip = function () {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.zip,application/zip,application/x-zip-compressed';
        input.style.display = 'none';
        let settled = false;
        const settle = (val) => { if (!settled) { settled = true; resolve(val); } };
        input.addEventListener('change', () => {
            settle(input.files && input.files[0] ? input.files[0] : null);
        });
        input.addEventListener('cancel', () => settle(null));
        document.body.appendChild(input);
        input.click();
        setTimeout(() => settle(null), 60000);
    });
};

window.doxieOs.importWorkflow = async function (file, overwrite, catalogId) {
    if (!file) return { ok: false, status: 0, message: 'No file selected' };
    try {
        const fd = new FormData();
        fd.append('file', file, file.name);
        if (overwrite) fd.append('overwrite', 'true');
        if (catalogId) fd.append('catalogId', catalogId);
        const resp = await fetch('/api/workflows/import', { method: 'POST', body: fd });
        let body = null;
        try { body = await resp.json(); } catch (_) { /* server may have returned plain text */ }
        return {
            ok: resp.ok,
            status: resp.status,
            workflowId: body && body.workflowId,
            href: body && body.href,
            workflowAction: body && body.workflowAction,
            agents: body && body.agents ? body.agents : [],
            code: body && body.code,
            message: body && (body.error || body.message),
        };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.pickAndImportWorkflow = async function (overwrite, catalogId) {
    const file = await window.doxieOs.pickWorkflowZip();
    if (!file) return { ok: false, status: 0, message: 'cancelled', cancelled: true };
    return await window.doxieOs.importWorkflow(file, overwrite, catalogId);
};

window.doxieOs.updateWorkflow = async function (workflowId, payload) {
    try {
        const resp = await fetch(`/api/workflows/${encodeURIComponent(workflowId)}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload || {}),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.setWorkflowEnabled = async function (workflowId, enabled) {
    try {
        const resp = await fetch(`/api/workflows/${encodeURIComponent(workflowId)}/enabled`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled: !!enabled }),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, enabled: !!enabled, message };
    } catch (err) {
        return { ok: false, status: 0, enabled: !!enabled, message: String(err) };
    }
};

// Resolve a workflow approval-gate node — POST to /approve or /reject.
// `decision` is the literal string 'approve' or 'reject'. Returns
// { ok, status, message? }. The runner's RunUpdated event handles
// re-rendering, so the caller doesn't need to refetch the run.
window.doxieOs.resolveWorkflowGate = async function (workflowId, runId, nodeId, decision, comment) {
    try {
        const url = `/api/workflows/${encodeURIComponent(workflowId)}/runs/${encodeURIComponent(runId)}/nodes/${encodeURIComponent(nodeId)}/${decision}`;
        const resp = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ comment: comment || null }),
        });
        let message = null;
        if (!resp.ok) {
            try {
                const body = await resp.json();
                message = body && (body.error || body.message);
            } catch (_) {
                try { message = await resp.text(); } catch (_) { message = resp.statusText; }
            }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

window.doxieOs.deleteWorkflow = async function (workflowId) {
    try {
        const resp = await fetch(`/api/workflows/${encodeURIComponent(workflowId)}`, {
            method: 'DELETE',
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

// === Agent / Workflow Builder helpers ===========================================
//
// Thin fetch wrappers around /api/builder/* used by the Agent Builder
// and Workflow Builder Razor pages. The terminal itself is attached via
// the existing window.doxieOs.terminals.attach (xterm + SignalR hub),
// so these helpers only cover sandbox allocation, manifest polling,
// and Save promotion.

window.doxieOs.startAgentBuilder = async function (workspaceId, editAgentId) {
    try {
        const resp = await fetch('/api/builder/agent', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                workspaceId: workspaceId || null,
                editAgentId: editAgentId || null,
            }),
        });
        let body = null;
        let message = null;
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            sessionId: body && body.sessionId ? body.sessionId : null,
            sandboxTag: body && body.sandboxTag ? body.sandboxTag : null,
            label: body && body.label ? body.label : null,
            attachedWorkspaceId: body && body.attachedWorkspaceId ? body.attachedWorkspaceId : null,
            editAgentId: body && body.editAgentId ? body.editAgentId : null,
            message,
        };
    } catch (err) {
        return { ok: false, status: 0, sessionId: null, sandboxTag: null, label: null, attachedWorkspaceId: null, editAgentId: null, message: String(err) };
    }
};

window.doxieOs.listBuilderSessions = async function (kind) {
    try {
        const qs = `?kind=${encodeURIComponent(kind || 'agent')}`;
        const resp = await fetch(`/api/builder/sessions${qs}`);
        if (!resp.ok) return { ok: false, status: resp.status, sessions: [] };
        const sessions = await resp.json();
        return { ok: true, status: 200, sessions: Array.isArray(sessions) ? sessions : [] };
    } catch (err) {
        return { ok: false, status: 0, sessions: [], message: String(err) };
    }
};

window.doxieOs.readBuilderManifest = async function (sessionId) {
    if (!sessionId) return null;
    try {
        const resp = await fetch(`/api/builder/${encodeURIComponent(sessionId)}/manifest`);
        if (!resp.ok) return null;
        return await resp.json();
    } catch (_) {
        return null;
    }
};

window.doxieOs.saveBuilderAgent = async function (sessionId, overwrite, catalogId) {
    try {
        const resp = await fetch(`/api/builder/${encodeURIComponent(sessionId)}/save`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ overwrite: !!overwrite, catalogId: catalogId || 'default' }),
        });
        let body = null;
        let message = null;
        let errors = [];
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try {
                const err = await resp.json();
                message = err && err.error ? err.error : null;
                if (err && Array.isArray(err.errors)) errors = err.errors;
            } catch (_) {
                try { message = await resp.text(); } catch (__) { message = resp.statusText; }
            }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            agentId: body && body.agentId ? body.agentId : null,
            catalogId: body && body.catalogId ? body.catalogId : 'default',
            href: body && body.href ? body.href : null,
            message,
            errors,
        };
    } catch (err) {
        return { ok: false, status: 0, agentId: null, catalogId: 'default', href: null, message: String(err), errors: [] };
    }
};

window.doxieOs.updateBuilderManifestIcon = async function (sessionId, icon) {
    try {
        const resp = await fetch(`/api/builder/${encodeURIComponent(sessionId)}/manifest/icon`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ icon: icon || null }),
        });
        let body = null;
        let message = null;
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try {
                const err = await resp.json();
                message = err && err.error ? err.error : null;
            } catch (_) {
                try { message = await resp.text(); } catch (__) { message = resp.statusText; }
            }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            updatedAt: body && body.updatedAt ? body.updatedAt : null,
            message,
        };
    } catch (err) {
        return { ok: false, status: 0, updatedAt: null, message: String(err) };
    }
};

window.doxieOs.startWorkflowBuilder = async function (workspaceId, editWorkflowId) {
    try {
        const resp = await fetch('/api/builder/workflow', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                workspaceId: workspaceId || null,
                editWorkflowId: editWorkflowId || null,
            }),
        });
        let body = null;
        let message = null;
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            sessionId: body && body.sessionId ? body.sessionId : null,
            sandboxTag: body && body.sandboxTag ? body.sandboxTag : null,
            label: body && body.label ? body.label : null,
            attachedWorkspaceId: body && body.attachedWorkspaceId ? body.attachedWorkspaceId : null,
            editWorkflowId: body && body.editWorkflowId ? body.editWorkflowId : null,
            message,
        };
    } catch (err) {
        return { ok: false, status: 0, sessionId: null, sandboxTag: null, label: null, attachedWorkspaceId: null, editWorkflowId: null, message: String(err) };
    }
};

window.doxieOs.readWorkflowManifest = async function (sessionId) {
    if (!sessionId) return null;
    try {
        const resp = await fetch(`/api/builder/${encodeURIComponent(sessionId)}/workflow-manifest`);
        if (!resp.ok) return null;
        return await resp.json();
    } catch (_) {
        return null;
    }
};

window.doxieOs.saveBuilderWorkflow = async function (sessionId, overwrite, catalogId) {
    try {
        const resp = await fetch(`/api/builder/${encodeURIComponent(sessionId)}/save-workflow`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ overwrite: !!overwrite, catalogId: catalogId || 'default' }),
        });
        let body = null;
        let message = null;
        let errors = [];
        if (resp.ok) {
            try { body = await resp.json(); } catch (_) { /* empty */ }
        } else {
            try {
                const err = await resp.json();
                message = err && err.error ? err.error : null;
                if (err && Array.isArray(err.errors)) errors = err.errors;
            } catch (_) {
                try { message = await resp.text(); } catch (__) { message = resp.statusText; }
            }
        }
        return {
            ok: resp.ok,
            status: resp.status,
            workflowId: body && body.workflowId ? body.workflowId : null,
            catalogId: body && body.catalogId ? body.catalogId : 'default',
            href: body && body.href ? body.href : null,
            agentResults: body && body.agentResults ? body.agentResults : [],
            message,
            errors,
        };
    } catch (err) {
        return { ok: false, status: 0, workflowId: null, catalogId: 'default', href: null, agentResults: [], message: String(err), errors: [] };
    }
};

window.doxieOs.deleteAgent = async function (agentId) {
    try {
        const resp = await fetch(`/api/agents/${encodeURIComponent(agentId)}`, {
            method: 'DELETE',
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

// Open a folder in the OS file explorer. Server-side validates the
// path stays inside the workspace root and refuses anything that
// escapes upward. The user is the orchestrator's owner, so showing
// a folder is fair game.
window.doxieOs.openFolder = async function (path) {
    const resp = await fetch(`/api/files/open?path=${encodeURIComponent(path)}`, {
        method: 'POST',
    });
    if (!resp.ok) {
        const message = await resp.text().catch(() => resp.statusText);
        throw new Error(`Open folder failed: ${message}`);
    }
};

window.doxieOs.openRunFile = async function (rootKind, identifier, relativePath) {
    try {
        const resp = await fetch('/api/run-files/open', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                rootKind: rootKind || '',
                identifier: identifier || '',
                relativePath: relativePath || '',
            }),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

// PATCH the agent's category. Server enforces sealed-category rules
// (Doxie and Connector cannot be moved into or out of via the UI).
window.doxieOs.updateAgentCategory = async function (agentId, category, icon) {
    try {
        const resp = await fetch(`/api/agents/${encodeURIComponent(agentId)}/category`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ category, icon }),
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

// Trigger a browser download of <agentId>.zip. Uses a synthetic
// anchor click so the browser handles the Content-Disposition
// header and the existing CSP rules; no blob plumbing needed.
window.doxieOs.exportAgent = function (agentId) {
    const a = document.createElement('a');
    a.href = `/api/agents/${encodeURIComponent(agentId)}/export`;
    a.download = `${agentId}.zip`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
};

// Open a hidden <input type=file> and resolve with the chosen file
// (or null if the user cancelled). Lets Blazor avoid having to host
// a real <InputFile> just for the import button.
window.doxieOs.pickAgentZip = function () {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.zip,application/zip,application/x-zip-compressed';
        input.style.display = 'none';
        // Cancel detection — modern browsers fire 'cancel' on the
        // input. Older Safari falls back to focus-after-click which we
        // approximate with a one-shot focus listener.
        let settled = false;
        const settle = (val) => { if (!settled) { settled = true; resolve(val); } };
        input.addEventListener('change', () => {
            settle(input.files && input.files[0] ? input.files[0] : null);
        });
        input.addEventListener('cancel', () => settle(null));
        document.body.appendChild(input);
        input.click();
        // Fallback: if no event fires within 60s (e.g. user walks
        // away), resolve null so the Blazor side doesn't hang.
        setTimeout(() => settle(null), 60000);
    });
};

// Upload a previously-picked zip (from pickAgentZip) to the import
// endpoint. Returns { ok, status, agentId?, href?, code?, message? }.
window.doxieOs.importAgent = async function (file, overwrite, catalogId) {
    if (!file) return { ok: false, status: 0, message: 'No file selected' };
    try {
        const fd = new FormData();
        fd.append('file', file, file.name);
        if (overwrite) fd.append('overwrite', 'true');
        if (catalogId) fd.append('catalogId', catalogId);
        const resp = await fetch('/api/agents/import', { method: 'POST', body: fd });
        let body = null;
        try { body = await resp.json(); } catch (_) { /* server may have returned plain text */ }
        return {
            ok: resp.ok,
            status: resp.status,
            agentId: body && body.agentId,
            href: body && body.href,
            code: body && body.code,
            message: body && (body.error || body.message),
        };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

// Pick + import in one round-trip. Convenience for the Agents page
// "Import" button so Blazor only has to call a single helper.
window.doxieOs.pickAndImportAgent = async function (overwrite, catalogId) {
    const file = await window.doxieOs.pickAgentZip();
    if (!file) return { ok: false, status: 0, message: 'cancelled', cancelled: true };
    return await window.doxieOs.importAgent(file, overwrite, catalogId);
};

window.doxieOs.deleteLibrary = async function (libraryId, catalogId) {
    try {
        const qs = catalogId ? `?catalogId=${encodeURIComponent(catalogId)}` : '';
        const resp = await fetch(`/api/libraries/${encodeURIComponent(libraryId)}${qs}`, {
            method: 'DELETE',
        });
        let message = null;
        if (!resp.ok) {
            try { message = await resp.text(); } catch (_) { message = resp.statusText; }
        }
        return { ok: resp.ok, status: resp.status, message };
    } catch (err) {
        return { ok: false, status: 0, message: String(err) };
    }
};

