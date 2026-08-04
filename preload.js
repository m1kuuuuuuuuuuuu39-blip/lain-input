// preload.js — expose animation events to renderer
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('AnimLayer', {
  onChar: (cb) => ipcRenderer.on('anim', (e, data) => cb(data)),
  sendMouse: (x, y) => ipcRenderer.send('mouse-pos', x, y)
});
