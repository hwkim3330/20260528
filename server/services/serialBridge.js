'use strict';
/**
 * serialBridge.js — Native Node.js serial port manager.
 *
 * Two-tier strategy:
 *  1. If `serialport` npm is installed → use it (best, works on all OS)
 *  2. Else (Linux only) → scan /dev for ttyUSB, ttyACM, ttyS devices and open via
 *     stty + fs streams (no native build required)
 *
 * Public API: list, open, close, write, setSignals, command, getStatus, getSession, isAvailable, events
 */

const { EventEmitter } = require('events');
const fs   = require('fs');
const path = require('path');
const { execFile, spawn } = require('child_process');
const os   = require('os');

let SerialPort;
try { ({ SerialPort } = require('serialport')); } catch {}

const events = new EventEmitter();
events.setMaxListeners(200);

const sessions = new Map(); // Map<sessionId, SerialSession>

// ── Linux /dev scanner ────────────────────────────────────────────────────────

/** Read a single-line sysfs file, trimmed. Returns '' on error. */
function sysfsRead(filePath) {
  try { return fs.readFileSync(filePath, 'utf8').trim(); } catch { return ''; }
}

/**
 * For a given tty name (e.g. 'ttyUSB0'), walk sysfs to find USB product info.
 * Path: /sys/class/tty/{name}/device -> symlink to USB interface directory
 * USB device attrs are one level up (e.g. ../idVendor, ../product)
 */
function usbInfoFromSysfs(ttyName) {
  try {
    const devDir  = `/sys/class/tty/${ttyName}/device`;
    const target  = fs.readlinkSync(devDir); // e.g. ../../../1-1.2:1.0
    // The parent dir of the interface is the USB device
    const usbDir  = path.resolve(path.dirname(devDir), target, '..');
    const product = sysfsRead(`${usbDir}/product`);
    const mfr     = sysfsRead(`${usbDir}/manufacturer`);
    const vendor  = sysfsRead(`${usbDir}/idVendor`);
    const prodId  = sysfsRead(`${usbDir}/idProduct`);
    return { product, manufacturer: mfr, usbVendorId: vendor, usbProductId: prodId };
  } catch {
    return {};
  }
}

/**
 * Scan /dev for known TTY patterns and enrich with sysfs metadata.
 * Priority order: ttyUSB > ttyACM > ttyAMA > ttyS (skip ttyS if no sysfs device link)
 */
async function listLinuxTty() {
  if (os.platform() !== 'linux') return [];
  let entries;
  try { entries = await fs.promises.readdir('/dev'); } catch { return []; }

  const USB = entries.filter(n => /^ttyUSB\d+$/.test(n)).sort();
  const ACM = entries.filter(n => /^ttyACM\d+$/.test(n)).sort();
  const AMA = entries.filter(n => /^ttyAMA\d+$/.test(n)).sort();
  // Only include ttyS* that have a real device symlink in sysfs
  const SER = entries.filter(n => /^ttyS\d+$/.test(n)).sort().filter(n => {
    try { fs.readlinkSync(`/sys/class/tty/${n}/device`); return true; } catch { return false; }
  });

  const all = [...USB, ...ACM, ...AMA, ...SER];
  return all.map(name => {
    const devPath = `/dev/${name}`;
    // Check if accessible (don't require root — just test stat)
    try { fs.statSync(devPath); } catch { return null; }
    const usb  = usbInfoFromSysfs(name);
    const label = usb.product
      ? `${name}  (${usb.product}${usb.manufacturer ? ' / ' + usb.manufacturer : ''})`
      : name;
    return {
      path:         devPath,
      name:         devPath,
      displayName:  label,
      manufacturer: usb.manufacturer || '',
      usbProduct:   usb.product || '',
      usbVendorId:  usb.usbVendorId || '',
      usbProductId: usb.usbProductId || '',
    };
  }).filter(Boolean);
}

// ── Shared command engine (used by both session types) ───────────────────────
// The switch console protocol has no request IDs, so responses are matched to the
// command that is in flight. To keep that matching correct we:
//   1. serialise commands — only ONE is on the wire at a time (a promise chain);
//   2. treat an OK/ERR line as the reply to the single in-flight command;
//   3. ignore any OK/ERR that arrives with no command in flight (unsolicited);
//   4. after a timeout, drain briefly before the next write so a late reply from
//      the timed-out command can't be mis-paired with the next command.
const _DRAIN_MS = 250;

function _feedResponseLines(session, parts) {
  for (const line of parts) {
    const t = line.trim();
    if (!t) continue;
    if (!(t.startsWith('OK') || t.startsWith('ERR'))) continue; // banners / echo — not a reply
    const inf = session._inflight;
    if (!inf) continue;                 // unsolicited (e.g. straggler) — ignore
    session._inflight = null;
    clearTimeout(inf.timer);
    if (t.startsWith('OK')) inf.resolve(t.slice(2).trim());
    else inf.reject(new Error(t.slice(3).trim() || 'ERR'));
  }
}

function _runCommand(session, writeFn, cmd, timeoutMs = 3000) {
  const prev = session._cmdChain || Promise.resolve();
  const result = prev.catch(() => {}).then(async () => {
    // After a prior timeout, wait out the drain window (inflight is null so any late
    // reply is ignored), then drop any partial straggler before issuing this command.
    if (session._drainUntil && Date.now() < session._drainUntil) {
      await new Promise(r => setTimeout(r, session._drainUntil - Date.now()));
      session.lineBuffer = '';
    }
    session._drainUntil = 0;
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (fn, arg) => {
        if (settled) return; settled = true;
        clearTimeout(timer);
        if (session._inflight === inf) session._inflight = null;
        fn(arg);
      };
      const timer = setTimeout(() => {
        session._drainUntil = Date.now() + _DRAIN_MS;
        finish(reject, new Error('Serial command timeout'));
      }, timeoutMs);
      const inf = { resolve: (v) => finish(resolve, v), reject: (e) => finish(reject, e), timer };
      session._inflight = inf;
      const line = cmd.endsWith('\n') ? cmd : cmd + '\r\n';
      writeFn(Buffer.from(line, 'utf8'), (err) => { if (err) finish(reject, err); });
    });
  });
  session._cmdChain = result.catch(() => {}); // keep the chain alive regardless of outcome
  return result;
}

function _rejectInflight(session, err) {
  const inf = session._inflight;
  if (!inf) return;
  session._inflight = null;
  clearTimeout(inf.timer);
  inf.reject(err);
}

// ── Serialport-based session ──────────────────────────────────────────────────

class SerialSession {
  constructor(devPath) {
    this.path        = devPath;
    this.lineBuffer  = '';
    this._inflight   = null;             // single in-flight command
    this._cmdChain   = Promise.resolve(); // serialises commands
    this._drainUntil = 0;                 // post-timeout drain deadline
    this._type       = 'serialport'; // or 'stty'
  }

  open(opts = {}) {
    if (!SerialPort) return Promise.reject(new Error('serialport npm not installed — run: npm install serialport'));
    return new Promise((resolve, reject) => {
      this.port = new SerialPort({
        path:     this.path,
        baudRate: opts.baudRate ?? 115200,
        dataBits: opts.dataBits ?? 8,
        stopBits: opts.stopBits ?? 1,
        parity:   opts.parity   ?? 'none',
        autoOpen: false,
      });

      this.port.on('data', chunk => this._onData(chunk));
      this.port.on('close', () => {
        sessions.delete(this.path);
        _rejectInflight(this, new Error('Serial port closed'));
        events.emit('serial', { kind: 'serial', type: 'closed', session: this.path });
      });
      this.port.on('error', err => {
        _rejectInflight(this, new Error(err.message));
        events.emit('serial', { kind: 'serial', type: 'error', message: err.message, session: this.path });
      });
      this.port.open(err => { if (err) reject(err); else resolve(); });
    });
  }

  _onData(chunk) {
    const hex = chunk.toString('hex');
    events.emit('serial', { kind: 'serial', rxType: 'rx', hex, session: this.path });

    this.lineBuffer += chunk.toString('utf8');
    const parts = this.lineBuffer.split(/\r\n|\n|\r/);
    this.lineBuffer = parts.pop() ?? '';
    _feedResponseLines(this, parts);
  }

  close() {
    return new Promise(resolve => {
      if (!this.port) { resolve(); return; }
      this.port.close(() => resolve());
    });
  }

  write({ hex, text }) {
    if (!this.port) return Promise.reject(new Error(`Session not open: ${this.path}`));
    const data = hex ? Buffer.from(hex, 'hex') : Buffer.from(text ?? '', 'utf8');
    return new Promise((resolve, reject) => {
      this.port.write(data, err => err ? reject(err) : resolve());
    });
  }

  setSignals(signals) {
    if (!this.port) return Promise.resolve();
    return new Promise(resolve => { this.port.set(signals, () => resolve()); });
  }

  command(cmd, timeoutMs = 3000) {
    if (!this.port) return Promise.reject(new Error('Serial port not open'));
    return _runCommand(this, (buf, cb) => this.port.write(buf, cb), cmd, timeoutMs);
  }
}

// ── stty-based session (Linux fallback, no serialport npm) ────────────────────

class SttySession {
  constructor(devPath) {
    this.path        = devPath;
    this.lineBuffer  = '';
    this._inflight   = null;
    this._cmdChain   = Promise.resolve();
    this._drainUntil = 0;
    this._fd         = null;
    this._reader     = null;
  }

  open(opts = {}) {
    const baud = opts.baudRate ?? 115200;
    const dev  = this.path;

    return new Promise((resolve, reject) => {
      // 먼저 O_RDWR|O_NOCTTY 로 열어서 termios 설정 후 stty 로 baud rate 설정
      fs.open(dev, fs.constants.O_RDWR | fs.constants.O_NOCTTY | fs.constants.O_NONBLOCK, (ferr, fd) => {
        if (ferr) { return reject(new Error(`Cannot open ${dev}: ${ferr.message}`)); }
        // stty로 보레이트 및 모드 설정
        execFile('stty', ['-F', dev,
          String(baud), 'raw', '-echo', '-echoe', '-echok',
          'cs8', '-cstopb', '-parenb', '-crtscts',
        ], (err) => {
          if (err) {
            fs.close(fd, () => {});
            return reject(new Error(`stty failed: ${err.message}`));
          }
          this._fd = fd;
          this._startRead();
          events.emit('serial', { kind: 'serial', type: 'opened', session: dev });
          resolve();
        });
      });
    });
  }

  _startRead() {
    if (!this._fd) return;
    const buf = Buffer.alloc(256);
    const readLoop = () => {
      if (this._fd === null) return;
      fs.read(this._fd, buf, 0, buf.length, null, (err, bytesRead) => {
        if (err) {
          if (err.code === 'EAGAIN' || err.code === 'EWOULDBLOCK') {
            // No data yet, wait a bit
            setTimeout(readLoop, 20);
            return;
          }
          events.emit('serial', { kind: 'serial', type: 'error', message: err.message, session: this.path });
          // Resume polling after transient error (EIO etc.) instead of stopping permanently
          setTimeout(readLoop, 500);
          return;
        }
        if (bytesRead > 0) {
          const chunk = buf.slice(0, bytesRead);
          const hex   = chunk.toString('hex');
          events.emit('serial', { kind: 'serial', rxType: 'rx', hex, session: this.path });

          this.lineBuffer += chunk.toString('utf8');
          // Handle \r\n, \n, and \r-only line endings
          const parts = this.lineBuffer.split(/\r\n|\n|\r/);
          this.lineBuffer = parts.pop() ?? '';
          _feedResponseLines(this, parts);
        }
        setTimeout(readLoop, 10);
      });
    };
    readLoop();
  }

  close() {
    return new Promise(resolve => {
      const fd = this._fd;
      this._fd = null;
      _rejectInflight(this, new Error('Serial port closed'));
      if (fd === null) { resolve(); return; }
      fs.close(fd, () => {
        sessions.delete(this.path);
        events.emit('serial', { kind: 'serial', type: 'closed', session: this.path });
        resolve();
      });
    });
  }

  write({ hex, text }) {
    if (this._fd === null) return Promise.reject(new Error(`Session not open: ${this.path}`));
    const data = hex ? Buffer.from(hex, 'hex') : Buffer.from(text ?? '', 'utf8');
    return new Promise((resolve, reject) => {
      fs.write(this._fd, data, err => err ? reject(err) : resolve());
    });
  }

  setSignals() { return Promise.resolve(); }

  command(cmd, timeoutMs = 3000) {
    if (this._fd === null) return Promise.reject(new Error('Serial port not open'));
    return _runCommand(this, (buf, cb) => fs.write(this._fd, buf, cb), cmd, timeoutMs);
  }
}

// ── Public API ─────────────────────────────────────────────────────────────────

async function list() {
  // Try serialport first (all platforms, richest metadata)
  if (SerialPort) {
    try {
      const ports = await SerialPort.list();
      if (ports.length > 0) {
        return ports.map(p => ({
          path:         p.path,
          name:         p.path,
          displayName:  p.friendlyName || p.manufacturer
            ? `${p.path}${p.manufacturer ? '  (' + p.manufacturer + ')' : ''}`
            : p.path,
          manufacturer: p.manufacturer  || '',
          usbProduct:   p.friendlyName  || '',
          usbVendorId:  p.vendorId      || '',
          usbProductId: p.productId     || '',
          serialNumber: p.serialNumber  || '',
        }));
      }
    } catch { /* fall through to sysfs scan */ }
  }

  // Linux fallback: scan /dev directly
  const linuxPorts = await listLinuxTty();
  if (linuxPorts.length > 0) return linuxPorts;

  return [];
}

async function open(devPath, opts = {}) {
  if (!devPath) throw new Error('포트 경로가 비어있습니다');
  if (sessions.has(devPath)) return { sessionId: devPath, session: devPath };

  let session;
  if (SerialPort) {
    session = new SerialSession(devPath);
    try {
      await session.open(opts);
      sessions.set(devPath, session);
      return { sessionId: devPath, session: devPath };
    } catch (err) {
      // serialport fails on some Linux ttyS* drivers (TCSETS2 ioctl not supported)
      // fall through to stty fallback if on Linux
      if (os.platform() !== 'linux') throw err;
      console.warn(`[serialBridge] serialport open failed (${err.message}), retrying with stty fallback`);
    }
  }

  if (os.platform() === 'linux') {
    session = new SttySession(devPath);
  } else {
    throw new Error('serialport npm이 설치되지 않았습니다. 실행: npm install serialport');
  }

  await session.open(opts);
  sessions.set(devPath, session);
  return { sessionId: devPath, session: devPath };
}

async function close(sessionId) {
  const s = sessions.get(sessionId);
  if (!s) return;
  await s.close();
  sessions.delete(sessionId);
}

function write(sessionId, data) {
  const s = sessions.get(sessionId) ?? sessions.values().next().value;
  if (!s) throw new Error('Serial port not open');
  return s.write(data);
}

function setSignals(sessionId, signals) {
  const s = sessions.get(sessionId) ?? sessions.values().next().value;
  if (!s) return Promise.resolve();
  return s.setSignals(signals);
}

function command(sessionId, cmd, timeoutMs) {
  const s = sessions.get(sessionId) ?? sessions.values().next().value;
  if (!s) return Promise.reject(new Error('Serial port not open'));
  return s.command(cmd, timeoutMs);
}

function getStatus() {
  const open = Array.from(sessions.keys());
  return { sessions: open, open: open.length > 0, session: open[0] ?? null };
}

function getSession(preferredId) {
  if (preferredId && sessions.has(preferredId)) return preferredId;
  return sessions.keys().next().value ?? null;
}

/** Returns true if at least one communication method is available */
function isAvailable() {
  return !!SerialPort || os.platform() === 'linux';
}

module.exports = { list, open, close, write, setSignals, command, getStatus, getSession, isAvailable, events };
// Exported for unit testing the command engine without hardware.
module.exports._test = { SerialSession, _feedResponseLines, _runCommand };
