'use strict';

const crypto = require('crypto');
const { EventEmitter } = require('events');

const workers = new Map();
const pending = new Map();
const events  = new EventEmitter();
events.setMaxListeners(500);

function attach(wss) {
  wss.on('connection', (ws, req) => {
    const url = new URL(req.url || '/', 'http://localhost');
    if (url.pathname !== '/ws/worker') {
      ws.send(JSON.stringify({ type: 'connected', time: new Date().toISOString() }));
      return;
    }

    const workerId = url.searchParams.get('workerId') || `worker-${crypto.randomUUID()}`;
    const worker = { id: workerId, ws, connectedAt: new Date().toISOString(), lastSeen: new Date().toISOString(), info: {}, eventLog: [] };
    workers.set(workerId, worker);
    events.emit('worker:connect', workerId);

    ws.on('message', (raw) => {
      let msg;
      try { msg = JSON.parse(raw.toString()); } catch { return; }
      worker.lastSeen = new Date().toISOString();

      if (msg.type === 'hello' || msg.type === 'status') {
        worker.info = { ...worker.info, ...(msg.payload || {}) };
        events.emit(`info:${workerId}`, worker.info);
        return;
      }

      if (msg.type === 'event') {
        const entry = { time: new Date().toISOString(), payload: msg.payload || {} };
        worker.eventLog.unshift(entry);
        worker.eventLog = worker.eventLog.slice(0, 200);
        // broadcast to SSE subscribers
        events.emit(`event:${workerId}`, entry.payload);
        events.emit('event', { workerId, payload: entry.payload });
        return;
      }

      if (msg.replyTo && pending.has(msg.replyTo)) {
        const req = pending.get(msg.replyTo);
        pending.delete(msg.replyTo);
        clearTimeout(req.timer);
        req.resolve(msg);
      }
    });

    ws.on('close', () => {
      const current = workers.get(workerId);
      if (current?.ws === ws) { workers.delete(workerId); events.emit('worker:disconnect', workerId); }
    });
    ws.on('error', () => {});
    ws.send(JSON.stringify({ type: 'welcome', workerId, time: new Date().toISOString() }));
  });
}

function listWorkers() {
  return Array.from(workers.values()).map(w => ({
    id: w.id, connectedAt: w.connectedAt, lastSeen: w.lastSeen, info: w.info, eventCount: w.eventLog.length
  }));
}

function getWorker(id) { return workers.get(id); }

function hasWorker(id) { return workers.has(id) && workers.get(id).ws.readyState === 1 /* OPEN */; }

function sendCommand(workerId, command, payload = {}, timeoutMs = 15000) {
  const worker = workers.get(workerId);
  if (!worker || worker.ws.readyState !== 1) return Promise.reject(new Error(`Worker not connected: ${workerId}`));
  const id = crypto.randomUUID();
  const message = { id, type: 'command', command, payload, time: new Date().toISOString() };
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Worker command timeout: ${command}`)); }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
    worker.ws.send(JSON.stringify(message), (err) => { if (err) { pending.delete(id); clearTimeout(timer); reject(err); } });
  });
}

module.exports = { attach, listWorkers, getWorker, hasWorker, sendCommand, events };
