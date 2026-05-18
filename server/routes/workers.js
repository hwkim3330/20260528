'use strict';
const { Router } = require('express');

module.exports = (workerHub) => {
  const router = Router();

  router.get('/workers', (_req, res) => {
    res.json({ ok: true, workers: workerHub.listWorkers() });
  });

  router.get('/workers/:id/events', (req, res) => {
    const worker = workerHub.getWorker(req.params.id);
    if (!worker) return res.status(404).json({ ok: false, error: 'Worker not connected' });
    res.json({ ok: true, workerId: req.params.id, events: worker.events });
  });

  router.post('/workers/:id/command', async (req, res) => {
    try {
      const { command, payload, timeoutMs } = req.body || {};
      if (!command) return res.status(400).json({ ok: false, error: 'command is required' });
      const reply = await workerHub.sendCommand(req.params.id, command, payload || {}, timeoutMs || 10000);
      res.json({ ok: true, workerId: req.params.id, reply });
    } catch (err) {
      res.status(502).json({ ok: false, error: err.message });
    }
  });

  return router;
};
