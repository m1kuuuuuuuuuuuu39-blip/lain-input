// ================================================================
// lain-anim-layer — main process
// Transparent fullscreen overlay + global keyboard hook
// Caret position via caret-probe.exe (UI Automation)
// ================================================================
const { app, BrowserWindow, screen, Tray, Menu, nativeImage, ipcMain } = require('electron');
const { GlobalKeyboardListener } = require('node-global-key-listener');
const { spawn } = require('child_process');
const path = require('path');

let mouseX = 0, mouseY = 0; // DIP coords

let win = null;
let probe = null;
let probeReady = false;
let pendingResolvers = [];
let tray = null;

// Key name → printable character mapping
const KEYMAP = {
  'SPACE': ' ', 'TAB': '\t',
  'NUMPAD0': '0', 'NUMPAD1': '1', 'NUMPAD2': '2', 'NUMPAD3': '3', 'NUMPAD4': '4',
  'NUMPAD5': '5', 'NUMPAD6': '6', 'NUMPAD7': '7', 'NUMPAD8': '8', 'NUMPAD9': '9',
  'DECIMAL': '.', 'ADD': '+', 'SUBTRACT': '-', 'MULTIPLY': '*', 'DIVIDE': '/'
};
const SHIFT_CHARS = {
  '1':'!','2':'@','3':'#','4':'$','5':'%','6':'^','7':'&','8':'*','9':'(','0':')',
  '-':'_','=':'+','[':'{',']':'}','\\':'|',';':':',"'":'"',',':'<','.':'>','/':'?','`':'~'
};

// Punctuation keys — e.name uses standardName from node-global-key-listener
// (WinGlobalKeyLookup: 0xBC → "COMMA", 0xBE → "DOT", etc.)
// Shift variants handled via SHIFT_CHARS on the base char
const OEM_KEYS = {
  'COMMA': ',',
  'DOT': '.',
  'FORWARD SLASH': '/',
  'SEMICOLON': ';',
  'QUOTE': "'",
  'SQUARE BRACKET OPEN': '[',
  'SQUARE BRACKET CLOSE': ']',
  'BACKSLASH': '\\',
  'MINUS': '-',
  'EQUALS': '=',
  'SECTION': '`',
  'BACKTICK': '`'
};

// Special keys → symbolic characters
const SPECIAL_KEYS = {
  'ENTER': '↵',
  'TAB': '⇥',
  'ARROWUP': '↑',
  'ARROWDOWN': '↓',
  'ARROWLEFT': '←',
  'ARROWRIGHT': '→',
  'DELETE': '⌫',
  'HOME': '⇱',
  'END': '⇲',
  'PAGEUP': '⇞',
  'PAGEDOWN': '⇟',
  'ESCAPE': 'ESC',
  'INSERT': '⧉'
};

function createWindow() {
  const { width, height } = screen.getPrimaryDisplay().bounds;
  win = new BrowserWindow({
    x: 0, y: 0,
    width: width,
    height: height,
    transparent: true,
    frame: false,
    resizable: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    hasShadow: false,
    focusable: false,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: __dirname + '/preload.js'
    }
  });
  win.loadFile('overlay.html');
  win.setIgnoreMouseEvents(true, { forward: true });
}

// ================================================================
// Caret probe process (UI Automation)
// ================================================================
function startProbe() {
  try {
    probe = spawn(path.join(__dirname, 'caret-probe.exe'), [], { stdio: ['pipe', 'pipe', 'pipe'] });
    probeReady = true;

    // Diagnostics from probe stderr
    probe.stderr.on('data', (chunk) => {
      process.stdout.write(chunk);
    });

    let buf = '';
    probe.stdout.on('data', (chunk) => {
      buf += chunk.toString();
      let idx;
      while ((idx = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, idx).trim();
        buf = buf.slice(idx + 1);
        if (pendingResolvers.length > 0) {
          pendingResolvers.shift()(line);
        }
      }
    });
    probe.on('exit', () => { probeReady = false; probe = null; });
    console.log('[PROBE] caret-probe.exe started');
  } catch (e) {
    console.log('[PROBE] failed to start: ' + e.message);
  }
}

// Returns Promise<{x, y} | null> — screen coords (DIP)
function queryCaret() {
  return new Promise((resolve) => {
    if (!probeReady || !probe) { resolve(null); return; }
    pendingResolvers.push((line) => {
      if (line === 'NONE') resolve(null);
      else {
        const parts = line.split(',');
        if (parts.length === 2) {
          const scale = screen.getPrimaryDisplay().scaleFactor;
          resolve({ x: Math.round(parseInt(parts[0]) / scale), y: Math.round(parseInt(parts[1]) / scale) });
        } else resolve(null);
      }
    });
    // Send mouse position (physical px) so probe can do ElementFromPoint
    const scale = screen.getPrimaryDisplay().scaleFactor;
    probe.stdin.write('mouse ' + Math.round(mouseX * scale) + ' ' + Math.round(mouseY * scale) + '\n');
    probe.stdin.write('query\n');
    // Timeout fallback (probe crashed/hung)
    setTimeout(() => {
      const idx = pendingResolvers.length - 1;
      if (idx >= 0 && pendingResolvers[idx]) {
        pendingResolvers.splice(idx, 1);
        resolve(null);
      }
    }, 200);
  });
}

// ================================================================
// Key handler
// ================================================================
async function handleKey(name, shift) {
  const caret = await queryCaret();
  const data = { type: 'char', x: caret ? caret.x : null, y: caret ? caret.y : null };

  // Delete: dot flies away (like backspace)
  if (name === 'DELETE' || name === 'BACKSPACE') {
    data.type = 'backspace';
    if (win) win.webContents.send('anim', data);
    return;
  }

  // Printable characters
  if (/^[A-Z]$/.test(name)) {
    data.char = shift ? name : name.toLowerCase();
  } else if (/^[0-9]$/.test(name)) {
    data.char = shift ? SHIFT_CHARS[name] : name;
  } else if (KEYMAP[name]) {
    data.char = KEYMAP[name];
  } else if (name.length === 1 && /[^a-zA-Z0-9]/.test(name)) {
    data.char = shift ? (SHIFT_CHARS[name] || name) : name;
  } else if (OEM_KEYS[name]) {
    const base = OEM_KEYS[name];
    data.char = shift ? (SHIFT_CHARS[base] || base) : base;
  } else if (SPECIAL_KEYS[name]) {
    data.char = SPECIAL_KEYS[name];
    data.special = true;
  } else {
    return; // function keys, modifiers, etc.
  }

  if (win) win.webContents.send('anim', data);
}

// Tray icon for easy show/quit (PNG 32x32 cyan dot)
const TRAY_ICON_B64 = 'iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAHfSURBVFhH7ZY/SAMxFMYzOjo6Orso6GAnC4KbILg4uung4Ci4dBAFly6CuNhF0UEUdBAHKQhS7KUU3Jw6iYsguIhLnvmSXHteXy931zoI/eAHhd73vlz+vJwYaqjcUnJWUFD8xZ8LoapRESr4ECSpCyW/NGf695JzDEj0NKZDr7oCk1Cyqn1TrkIfMm8t39gQH2ZG6iuuUg6ZtWUKZyXXIKg5nvvN42AmMi9H1jX3oWTTVU6htFP/cklCnpB4Puf/72bVJXhkjxJXwILA5XkS0xMdFgokHo/550NUUHMJCaLqiF0zpgBAeDQ4znWZ97XReytRpsNxRs13jcTiHB8cUpwh8X7P+4H3RKCLcUbge/uQuwPeD1R90yX1kArWWSO42OcD4xxt835DUHJJPYQpYo0abDIuMA4GyvktWy6ph5KO4OcDicIkHxqldcP7LZ6jSM1RxtTBtwy7G7yvTZqOaG4yzuyolPiZKK3Zk8J5gJItl+BR0kYMeb0lcbpjNxwGhK7IPRdFBXsuIYXQu7kieTEXm25yqTWoq7hDynsgKjQNvlg2VKPsKuYQzi1XNC1KHrpKfQjLkfXjBB+u2MwDEzYQZgNHiQsMwUAx5egnfyY0E7M0uq9HwU061P+SED/3LsDW8F/mdAAAAABJRU5ErkJggg==';

function createTray() {
  const icon = nativeImage.createFromBuffer(Buffer.from(TRAY_ICON_B64, 'base64'));
  tray = new Tray(icon);
  tray.setToolTip('Lain Anim Layer');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Lain Anim Layer', enabled: false },
    { type: 'separator' },
    { label: 'Quit', click: () => app.quit() }
  ]));
  tray.on('click', () => {
    tray.popUpContextMenu();
  });
  console.log('[TRAY] tray icon ready');
}

// Overlay reports mouse position (DIP)
ipcMain.on('mouse-pos', (e, x, y) => {
  mouseX = x;
  mouseY = y;
});

app.commandLine.appendSwitch('autoplay-policy', 'no-user-gesture-required');
app.whenReady().then(() => {
  createWindow();
  createTray();
  startProbe();

  const listener = new GlobalKeyboardListener();
  listener.addListener((e, down) => {
    if (e.state !== 'DOWN' || e.repeat) return;
    handleKey(e.name, e.shiftKey);
  });
});

app.on('window-all-closed', () => {});
