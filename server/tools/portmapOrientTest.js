'use strict';
/**
 * portmapOrientTest.js — verify the 2-PC port map re-orients per node.
 * The SAME owner-tagged canonical map must yield:
 *   - on Node A: P0–P3 local, P4–P5 → Node B
 *   - on Node B: P4–P5 local, P0–P3 → Node A   (the symmetric flip)
 *   node tools/portmapOrientTest.js
 */
const { orientPortmap, resolveEndpoints, DEFAULT_MAP } = require('../routes/portmap');

const NODE_A = 'http://169.254.88.222:8080';
const NODE_B = 'http://169.254.1.168:8080';
const A_IFACES = new Set(['lo', 'enp12s0f0', 'enp12s0f1', 'enp12s0f2', 'enp12s0f3']);
const B_IFACES = new Set(['lo', 'eno1', 'enp3s0f0', 'enp3s0f1']);
const fakeReq  = { headers: {} };

const fails = [];
const ck = (name, cond, detail) => { if (!cond) fails.push(`${name} — ${detail}`); };

function localPorts(map)  { return map.filter(e => !e.nodeUrl).map(e => e.port).sort(); }
function remoteUrl(map, port) { return map.find(e => e.port === port)?.nodeUrl; }
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// ── Node A perspective ──
{
  const m = orientPortmap(DEFAULT_MAP, A_IFACES, NODE_B);
  const { self, peer } = resolveEndpoints(DEFAULT_MAP, A_IFACES, fakeReq);
  ck('A/local', eq(localPorts(m), [0, 1, 2, 3]), `local ports = ${localPorts(m)} (expected 0-3)`);
  ck('A/p4->B', remoteUrl(m, 4) === NODE_B, `P4 nodeUrl = ${remoteUrl(m, 4)}`);
  ck('A/p5->B', remoteUrl(m, 5) === NODE_B, `P5 nodeUrl = ${remoteUrl(m, 5)}`);
  ck('A/self',  self === NODE_A, `self = ${self} (expected Node A)`);
  ck('A/peer',  peer === NODE_B, `peer = ${peer} (expected Node B)`);
}

// ── Node B perspective (the new symmetric behaviour) ──
{
  const m = orientPortmap(DEFAULT_MAP, B_IFACES, NODE_A);
  const { self, peer } = resolveEndpoints(DEFAULT_MAP, B_IFACES, fakeReq);
  ck('B/local', eq(localPorts(m), [4, 5]), `local ports = ${localPorts(m)} (expected 4,5)`);
  ck('B/p0->A', remoteUrl(m, 0) === NODE_A, `P0 nodeUrl = ${remoteUrl(m, 0)}`);
  ck('B/p3->A', remoteUrl(m, 3) === NODE_A, `P3 nodeUrl = ${remoteUrl(m, 3)}`);
  ck('B/self',  self === NODE_B, `self = ${self} (expected Node B)`);
  ck('B/peer',  peer === NODE_A, `peer = ${peer} (expected Node A)`);
}

// ── Old flat file (P0–P3 have NO nodeUrl) must still flip once backfilled by port ──
{
  const ownerByPort = new Map(DEFAULT_MAP.map(e => [e.port, e.nodeUrl]));
  const flat = [
    { port: 0, iface: 'enp12s0f0' }, { port: 1, iface: 'enp12s0f1' },
    { port: 2, iface: 'enp12s0f2' }, { port: 3, iface: 'enp12s0f3' },
    { port: 4, iface: 'enp3s0f1', nodeUrl: NODE_B }, { port: 5, iface: 'enp3s0f0', nodeUrl: NODE_B },
  ];
  const backfilled = flat.map(e => e.nodeUrl ? e : { ...e, nodeUrl: ownerByPort.get(e.port) });
  const m = orientPortmap(backfilled, B_IFACES, NODE_A);
  ck('old/B/p0->A', remoteUrl(m, 0) === NODE_A, `P0 nodeUrl = ${remoteUrl(m, 0)} (backfill failed)`);
  ck('old/B/local', eq(localPorts(m), [4, 5]), `local ports = ${localPorts(m)}`);
}

if (fails.length) {
  console.log(`\n  portmapOrientTest: ${fails.length} FAILED\n`);
  fails.forEach(f => console.log('   ✗ ' + f));
  process.exit(1);
}
console.log('\n  portmapOrientTest: all checks passed — map flips correctly on Node A and Node B\n');
