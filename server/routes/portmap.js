'use strict';
const { Router } = require('express');
const os   = require('os');
const fs   = require('fs');
const path = require('path');
const router = Router();

const PORTMAP_FILE = path.join(__dirname, '../logs/portmap.json');
const NODE_A = 'http://169.254.88.222:8080';   // P0–P3 owner
const NODE_B = 'http://169.254.1.168:8080';     // P4–P5 owner

// Canonical map: every port carries the URL of the node that physically OWNS it.
// GET /api/portmap re-orients this for whichever node is asking (see orientPortmap):
// ports whose iface is local lose nodeUrl, the rest point at the peer. So the SAME
// config is correct on Node A and Node B — open the UI on either and it self-flips.
const DEFAULT_MAP = [
  { port: 0, iface: 'enp12s0f0', nodeUrl: NODE_A },
  { port: 1, iface: 'enp12s0f1', nodeUrl: NODE_A },
  { port: 2, iface: 'enp12s0f2', nodeUrl: NODE_A },
  { port: 3, iface: 'enp12s0f3', nodeUrl: NODE_A },
  { port: 4, iface: 'enp3s0f1',  nodeUrl: NODE_B },   // 192.168.1.244
  { port: 5, iface: 'enp3s0f0',  nodeUrl: NODE_B },   // 192.168.1.254
];

// Lower-cased set of interface names that exist on THIS machine.
function localIfaceNames() {
  return new Set(Object.keys(os.networkInterfaces() || {}).map(n => n.toLowerCase()));
}

function reqOrigin(req) {
  const host = req.headers['x-forwarded-host'] || req.headers.host;
  return host ? `${req.protocol || 'http'}://${host}` : null;
}

function loadCanonical() {
  let m = DEFAULT_MAP;
  try {
    if (fs.existsSync(PORTMAP_FILE)) {
      const parsed = JSON.parse(fs.readFileSync(PORTMAP_FILE, 'utf8'));
      if (Array.isArray(parsed) && parsed.length) m = parsed;
    }
  } catch { /* fall through to default */ }
  // Backfill the owner URL for any entry that lacks one (e.g. an older flat file
  // saved before owner-tagging), keyed by port number from DEFAULT_MAP. Without this,
  // the peer node can't tell who owns a port that has no nodeUrl.
  const ownerByPort = new Map(DEFAULT_MAP.map(e => [Number(e.port), e.nodeUrl]));
  return m.map(e => e.nodeUrl ? e : { ...e, nodeUrl: ownerByPort.get(Number(e.port)) });
}

/**
 * Re-orient a canonical (owner-tagged) map for the node identified by `localSet`.
 * Pure + exported for tests.
 *   - iface present locally → local port (nodeUrl omitted)
 *   - otherwise            → remote port (nodeUrl = its owner, or `peerFallback`)
 */
function orientPortmap(canonical, localSet, peerFallback) {
  return canonical.map((e) => {
    const isLocal = localSet.has(String(e.iface || '').toLowerCase());
    if (isLocal) return { port: e.port, iface: e.iface };
    return { port: e.port, iface: e.iface, nodeUrl: e.nodeUrl || peerFallback || null };
  });
}

// self = owner URL of a local port; peer = owner URL of a remote port.
function resolveEndpoints(canonical, localSet, req) {
  let self = null, peer = null;
  for (const e of canonical) {
    const isLocal = localSet.has(String(e.iface || '').toLowerCase());
    if (isLocal && !self) self = e.nodeUrl || null;
    if (!isLocal && !peer) peer = e.nodeUrl || null;
  }
  if (!self) self = reqOrigin(req);
  return { self, peer };
}

router.get('/portmap', (req, res) => {
  try {
    const canonical = loadCanonical();
    const localSet  = localIfaceNames();
    const { self, peer } = resolveEndpoints(canonical, localSet, req);
    const portmap = orientPortmap(canonical, localSet, peer);
    res.json({ ok: true, portmap, self, peer });
  } catch (e) { res.status(500).json({ ok: false, error: e.message }); }
});

router.post('/portmap', (req, res) => {
  try {
    const { portmap } = req.body || {};
    if (!Array.isArray(portmap)) return res.status(400).json({ ok: false, error: 'portmap must be array' });

    // Persist in canonical (owner-tagged) form so the OTHER node can re-orient it too.
    // Incoming local ports have no nodeUrl → tag them with this node's own URL; keep any
    // prior owner so saving from one node doesn't erase the other node's port owners.
    const prior   = loadCanonical();
    const priorBy = new Map(prior.map(e => [Number(e.port), e]));
    const ownUrl  = reqOrigin(req);
    const canonical = portmap.map((e) => ({
      port:  e.port,
      iface: e.iface,
      nodeUrl: e.nodeUrl || priorBy.get(Number(e.port))?.nodeUrl || ownUrl || undefined,
    }));

    const dir = path.dirname(PORTMAP_FILE);
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(PORTMAP_FILE, JSON.stringify(canonical, null, 2));
    res.json({ ok: true });
  } catch (e) { res.status(500).json({ ok: false, error: e.message }); }
});

module.exports = router;
module.exports.orientPortmap   = orientPortmap;
module.exports.resolveEndpoints = resolveEndpoints;
module.exports.DEFAULT_MAP      = DEFAULT_MAP;
