const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('doxie', {
  invoke: (action, ...args) => ipcRenderer.invoke('doxie:invoke', action, args),
  onEvent(handler) {
    if (typeof handler !== 'function') throw new TypeError('An event listener is required.');
    const listener = (_event, payload) => handler(payload);
    ipcRenderer.on('doxie:event', listener);
    return () => ipcRenderer.removeListener('doxie:event', listener);
  },
});
