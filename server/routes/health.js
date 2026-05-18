'use strict';
const { Router } = require('express');
const router = Router();

router.get('/health', (req, res) => {
  const { workerHub, localWorkerId } = req.app.locals;
  const connected = workerHub.hasWorker(localWorkerId);
  const worker    = workerHub.getWorker(localWorkerId);
  res.json({
    ok: true,
    server:      { name: 'packet-lab-manager', port: Number(process.env.PORT || 8080) },
    localWorker: { connected, id: localWorkerId, info: worker?.info || {} },
    csharpWorker: { connected, note: 'EthernetPacketGenerator WebSocket worker' },
    time: new Date().toISOString()
  });
});

module.exports = router;
