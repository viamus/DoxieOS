const { mkdirSync } = require('node:fs');
const { dirname, join } = require('node:path');
const { randomUUID } = require('node:crypto');

const WINDOWS_APP_ID = 'org.doxieos.desktop';
const WINDOWS_TOAST_CLSID = '{412B13B4-6D88-4264-A5F4-EC6BB8D36253}';

function notificationRoute(href) {
  if (typeof href !== 'string' || /[\\\u0000-\u001f\u007f]/.test(href)) return '/notifications';
  return /^\/(?:agents|connectors|workflows|workspaces|libraries|history|notifications|consoles|settings)(?:[/?#]|$)/.test(href)
    ? href : '/notifications';
}

function shortText(value, limit) {
  if (typeof value !== 'string') return '';
  const text = value.replace(/\s+/g, ' ').trim();
  const characters = Array.from(text);
  return characters.length <= limit ? text : `${characters.slice(0, limit - 1).join('')}…`;
}

// The portable app has no installer to register its Start Menu identity.
// Use a per-user shortcut so Windows can deliver native toasts without admin rights.
function prepareWindowsNotificationShortcut({ app, shell, icon, platform = process.platform, executable = process.execPath, onError = () => {} }) {
  if (platform !== 'win32' || !app.isPackaged) return false;
  try {
    const shortcut = join(app.getPath('appData'), 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'DoxieOS Desktop.lnk');
    mkdirSync(dirname(shortcut), { recursive: true });
    const options = {
      target: executable, cwd: dirname(executable), description: 'DoxieOS desktop workbench',
      appUserModelId: WINDOWS_APP_ID, toastActivatorClsid: WINDOWS_TOAST_CLSID,
      ...(icon ? { icon, iconIndex: 0 } : {}),
    };
    if (!shell.writeShortcutLink(shortcut, 'create', options)) throw new Error('Windows could not register the DoxieOS notification shortcut.');
    return true;
  } catch (error) {
    try { onError(error); } catch {}
    return false;
  }
}

function createDesktopNotifications({ Notification, window, navigate = () => {}, icon, enabled = true, onError = () => {} }) {
  const active = new Map();
  const seen = new Set();
  let closed = false;
  const report = error => { try { onError(error); } catch {} };

  function dismiss(id) {
    const notification = active.get(id);
    if (!notification) return false;
    active.delete(id);
    try { notification.close(); } catch (error) { report(error); }
    return true;
  }

  function show(data = {}) {
    if (closed || !enabled || !data || typeof data !== 'object') return false;
    const id = typeof data.id === 'string' && data.id ? data.id : randomUUID();
    if (seen.has(id)) return false;
    let notification;
    try {
      if (!Notification?.isSupported()) return false;
      notification = new Notification({
        id, groupId: data.kind === 'question' ? 'doxie-questions' : 'doxie-runs',
        title: shortText(data.title, 80) || 'DoxieOS',
        body: shortText(data.body || data.message, 200),
        silent: false, timeoutType: data.kind === 'question' ? 'never' : 'default',
        ...(icon ? { icon } : {}),
      });
      notification.on('click', () => {
        try {
          const target = window();
          if (!target || target.isDestroyed()) return;
          if (target.isMinimized()) target.restore();
          target.show();
          target.focus();
          navigate(notificationRoute(data.href));
        } catch (error) { report(error); }
      });
      notification.on('close', details => {
        // Windows keeps timed-out notifications in Action Center. Retain the
        // object so their click handler still works while this process is open.
        if (details?.reason !== 'timedOut') active.delete(id);
      });
      notification.on('failed', (_event, error) => {
        active.delete(id);
        seen.delete(id);
        report(new Error(shortText(error, 300) || 'The operating system could not show the notification.'));
      });
      active.set(id, notification);
      seen.add(id);
      while (seen.size > 500) seen.delete(seen.values().next().value);
      while (active.size > 250) dismiss(active.keys().next().value);
      notification.show();
      return true;
    } catch (error) {
      active.delete(id);
      seen.delete(id);
      try { notification?.close(); } catch {}
      report(error);
      return false;
    }
  }

  function close() {
    closed = true;
    for (const id of active.keys()) dismiss(id);
    seen.clear();
  }

  return { show, dismiss, close };
}

module.exports = { createDesktopNotifications, prepareWindowsNotificationShortcut, notificationRoute, WINDOWS_APP_ID, WINDOWS_TOAST_CLSID };
