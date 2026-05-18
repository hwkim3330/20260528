'use strict';
const { Router } = require('express');
const router = Router();

function workerErr(res, err) {
  res.status(err.workerError ? 502 : 503).json({ ok: false, error: err.message });
}

// GET /api/interfaces
router.get('/interfaces', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('getInterfaces');
    const interfaces = data?.interfaces ?? [];
    res.json({ ok: true, interfaces, stdout: { interfaces } });
  } catch (err) { workerErr(res, err); }
});

// POST /api/build
router.post('/build', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('build', req.body || {});
    res.json({ ok: true, ...(data || {}), stdout: data || {} });
  } catch (err) { workerErr(res, err); }
});

// POST /api/send
router.post('/send', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('send', req.body || {}, 30000);
    res.json({ ok: true, ...(data || {}), stdout: data || {} });
  } catch (err) { workerErr(res, err); }
});

// POST /api/packet/send (alias)
router.post('/packet/send', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('send', req.body || {}, 30000);
    res.json({ ok: true, ...(data || {}), stdout: data || {} });
  } catch (err) { workerErr(res, err); }
});

// POST /api/probe-node — fetch remote node's interfaces, returns { url, interfaces }
router.post('/probe-node', async (req, res) => {
  try {
    const { url } = req.body || {};
    if (!url) return res.status(400).json({ ok: false, error: 'url required' });
    const base = url.replace(/\/$/, '');
    const resp = await fetch(`${base}/api/interfaces`, { signal: AbortSignal.timeout(5000) });
    const data = await resp.json();
    const ifaces = (data.interfaces ?? []).map(i => ({
      key:  i.key || i.name || i.deviceName || '',
      name: i.name || i.deviceName || i.key || '',
      mac:  i.mac || '',
      state: i.state || 'unknown',
      ipv4: i.ipv4 || [],
      description: i.description || ''
    }));
    res.json({ ok: true, url: base, interfaces: ifaces });
  } catch (err) { res.status(502).json({ ok: false, error: err.message }); }
});

// GET /api/arp-lookup?ip=192.168.1.100
// Parses the OS ARP table for the given IP address and returns its MAC.
router.get('/arp-lookup', async (req, res) => {
  const { ip } = req.query;
  if (!ip) return res.json({ ok: false, error: 'ip required' });
  try {
    const { execFile } = require('child_process');
    const { promisify } = require('util');
    const exec = promisify(execFile);
    // Works on both Windows (arp -a <ip>) and Linux/macOS
    const { stdout } = await exec('arp', ['-a', ip]);
    // Match any colon- or dash-separated MAC in the output
    const match = stdout.match(/([0-9a-f]{2}[:\-][0-9a-f]{2}[:\-][0-9a-f]{2}[:\-][0-9a-f]{2}[:\-][0-9a-f]{2}[:\-][0-9a-f]{2})/i);
    if (match) {
      const mac = match[1].replace(/-/g, ':').toLowerCase();
      return res.json({ ok: true, mac, ip });
    }
    res.json({ ok: false, error: 'not in ARP table', ip });
  } catch (err) {
    res.json({ ok: false, error: err.message });
  }
});

// GET /api/worker/status — worker capture state
router.get('/worker/status', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('status');
    res.json({ ok: true, ...(data || {}) });
  } catch (err) { workerErr(res, err); }
});

module.exports = router;
