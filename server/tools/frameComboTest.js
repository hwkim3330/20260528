'use strict';
/**
 * frameComboTest.js — Exhaustive frame-combination validator for frameBuilder.js
 *
 * Builds frames for every protocol / block combination and INDEPENDENTLY decodes
 * and verifies them (Ethernet/VLAN layout, IPv4 total-length + header checksum,
 * UDP/TCP/ICMP length + checksum, ARP layout, min-frame padding).
 *
 * No root, no network, no server — pure logic check against the builder.
 *   node tools/frameComboTest.js
 * Exit code 0 = all pass, 1 = at least one failure.
 */

const { buildFrame } = require('../services/frameBuilder');

// ── Independent Internet checksum (RFC 1071) ────────────────────────────────────
function inetChecksum(buf) {
  let sum = 0;
  for (let i = 0; i < buf.length; i += 2)
    sum += (i + 1 < buf.length) ? ((buf[i] << 8) + buf[i + 1]) : (buf[i] << 8);
  while (sum >> 16) sum = (sum & 0xFFFF) + (sum >> 16);
  return (~sum) & 0xFFFF;
}
// A header/segment that already carries its checksum must checksum back to 0.
function checksumValid(buf) { return inetChecksum(buf) === 0; }

const failures = [];
const passes   = [];
function check(name, cond, detail) {
  if (cond) passes.push(name);
  else failures.push(`${name} — ${detail}`);
}

// ── Frame decoder + invariant verifier ──────────────────────────────────────────
function verify(name, frame, expect) {
  try {
    if (!Buffer.isBuffer(frame)) return check(name, false, 'build returned non-Buffer');
    // Min Ethernet frame (no FCS): 60 bytes, unless _preview was set
    if (!expect.preview)
      check(`${name}/min60`, frame.length >= 60, `frame ${frame.length}B < 60B (padding missing)`);

    if (frame.length < 14) return check(`${name}/ethlen`, false, `frame too short ${frame.length}B`);

    let et = frame.readUInt16BE(12);
    let l3 = 14;

    // Optional 802.1Q VLAN
    if (expect.vlan) {
      check(`${name}/vlan.tpid`, et === 0x8100, `TPID=0x${et.toString(16)} expected 0x8100`);
      const tci = frame.readUInt16BE(14);
      check(`${name}/vlan.vid`, (tci & 0xFFF) === expect.vlan.vid,
            `vid=${tci & 0xFFF} expected ${expect.vlan.vid}`);
      et = frame.readUInt16BE(16);
      l3 = 18;
    }

    if (expect.etherType != null)
      check(`${name}/etherType`, et === expect.etherType,
            `etherType=0x${et.toString(16)} expected 0x${expect.etherType.toString(16)}`);

    // ── IPv4 ──
    if (et === 0x0800) {
      const ihl = (frame[l3] & 0x0F) * 4;
      check(`${name}/ip.version`, (frame[l3] >> 4) === 4, `version=${frame[l3] >> 4}`);
      const totLen = frame.readUInt16BE(l3 + 2);
      const proto  = frame[l3 + 9];
      // Header checksum must validate
      check(`${name}/ip.csum`, checksumValid(frame.subarray(l3, l3 + ihl)), 'IPv4 header checksum invalid');
      // Total length must fit inside the (unpadded) frame and match expectation
      check(`${name}/ip.totlen.fits`, l3 + totLen <= frame.length,
            `ip.totalLength ${totLen} overflows frame (l3=${l3}, frame=${frame.length})`);
      if (expect.ipTotLen != null)
        check(`${name}/ip.totlen`, totLen === expect.ipTotLen,
              `ip.totalLength=${totLen} expected ${expect.ipTotLen}`);
      if (expect.proto != null)
        check(`${name}/ip.proto`, proto === expect.proto, `ip.proto=${proto} expected ${expect.proto}`);

      const l4 = l3 + ihl;
      // ── UDP ──
      if (proto === 17) {
        const udpLen = frame.readUInt16BE(l4 + 4);
        check(`${name}/udp.len`, l4 + udpLen <= frame.length, `udp.len ${udpLen} overflows`);
        if (expect.udpLen != null)
          check(`${name}/udp.lenval`, udpLen === expect.udpLen, `udp.len=${udpLen} expected ${expect.udpLen}`);
        // Verify UDP checksum over pseudo-header + segment (bounded by udpLen, not frame padding)
        const seg = frame.subarray(l4, l4 + udpLen);
        const pseudo = Buffer.concat([
          frame.subarray(l3 + 12, l3 + 16), frame.subarray(l3 + 16, l3 + 20),
          Buffer.from([0, 17]), Buffer.from([udpLen >> 8, udpLen & 0xFF]),
        ]);
        const csfield = frame.readUInt16BE(l4 + 6);
        if (csfield !== 0) // 0 = checksum disabled
          check(`${name}/udp.csum`, checksumValid(Buffer.concat([pseudo, seg])), 'UDP checksum invalid');
      }
      // ── TCP ──
      else if (proto === 6) {
        const dataOff = (frame[l4 + 12] >> 4) * 4;
        check(`${name}/tcp.dataoff`, dataOff >= 20, `tcp data offset ${dataOff} < 20`);
        const segLen = totLen - ihl;
        const seg = frame.subarray(l4, l4 + segLen);
        const pseudo = Buffer.concat([
          frame.subarray(l3 + 12, l3 + 16), frame.subarray(l3 + 16, l3 + 20),
          Buffer.from([0, 6]), Buffer.from([segLen >> 8, segLen & 0xFF]),
        ]);
        check(`${name}/tcp.csum`, checksumValid(Buffer.concat([pseudo, seg])), 'TCP checksum invalid');
      }
      // ── ICMP ──
      else if (proto === 1) {
        const segLen = totLen - ihl;
        check(`${name}/icmp.csum`, checksumValid(frame.subarray(l4, l4 + segLen)), 'ICMP checksum invalid');
        if (expect.icmpType != null)
          check(`${name}/icmp.type`, frame[l4] === expect.icmpType,
                `icmp.type=${frame[l4]} expected ${expect.icmpType}`);
      }
    }
    // ── ARP ──
    else if (et === 0x0806) {
      check(`${name}/arp.htype`, frame.readUInt16BE(l3) === 0x0001, 'ARP htype != Ethernet');
      check(`${name}/arp.ptype`, frame.readUInt16BE(l3 + 2) === 0x0800, 'ARP ptype != IPv4');
      check(`${name}/arp.lens`, frame[l3 + 4] === 6 && frame[l3 + 5] === 4, 'ARP hlen/plen wrong');
    }
  } catch (e) {
    check(`${name}/exception`, false, `verifier threw: ${e.message}`);
  }
}

// ── helpers to build the block array the front-end produces ─────────────────────
const ETH  = (o = {}) => ({ type: 'Ethernet', dstMac: 'ff:ff:ff:ff:ff:ff', srcMac: 'aa:bb:cc:dd:ee:ff', etherType: '0x0800', ...o });
const VLAN = (o = {}) => ({ type: 'VLAN', vlanId: 100, priority: 0, innerEtherType: '0x0800', ...o });
const IPV4 = (o = {}) => ({ type: 'IPv4', srcIp: '192.168.1.10', dstIp: '192.168.1.20', ttl: 64, tos: 0, protocol: 'udp', ...o });
const UDP  = (o = {}) => ({ type: 'UDP', srcPort: 1111, dstPort: 2222, ...o });
const TCP  = (o = {}) => ({ type: 'TCP', srcPort: 1111, dstPort: 80, flags: 2, seqNum: 0, ackNum: 0, ...o });
const ICMP = (o = {}) => ({ type: 'ICMP', icmpType: 8, icmpCode: 0, ...o });
const ARP  = (o = {}) => ({ type: 'ARP', operation: 1, senderMac: 'aa:bb:cc:dd:ee:ff', senderIp: '192.168.1.10', targetMac: '00:00:00:00:00:00', targetIp: '192.168.1.20', ...o });
const PL   = (mode, data) => ({ type: 'Payload', mode, data });

// Emulate front-end buildPacketPayload mapping (blocks → builder profile)
function profileFromBlocks(blocks, extra = {}) {
  const ipv4B = blocks.find(b => b.type === 'IPv4');
  const udpB  = blocks.find(b => b.type === 'UDP');
  const tcpB  = blocks.find(b => b.type === 'TCP');
  const icmpB = blocks.find(b => b.type === 'ICMP');
  const plB   = blocks.find(b => b.type === 'Payload') || {};
  const p = {
    interface: 'test0', blocks,
    srcMac: blocks.find(b => b.type === 'Ethernet')?.srcMac,
    dstMac: blocks.find(b => b.type === 'Ethernet')?.dstMac,
    ipv4: ipv4B ? { src: ipv4B.srcIp, dst: ipv4B.dstIp, ttl: ipv4B.ttl, tos: ipv4B.tos } : {},
    payload: { mode: plB.mode || 'text', data: plB.data || '' },
    ...extra,
  };
  if (udpB)  p.udp  = { srcPort: udpB.srcPort, dstPort: udpB.dstPort };
  if (tcpB)  p.tcp  = { srcPort: tcpB.srcPort, dstPort: tcpB.dstPort, flags: tcpB.flags, seq: tcpB.seqNum, ack: tcpB.ackNum };
  if (icmpB) p.icmp = { type: icmpB.icmpType, code: icmpB.icmpCode };
  return p;
}

// payload byte length the builder will produce, for a given mode/data/seq
function plLen(mode, data, seq) {
  if (mode === 'hex')    return Buffer.from((data || '').replace(/[:\s]/g, ''), 'hex').length;
  if (mode === 'random') return 64;
  return Buffer.from((data || '') + (seq != null ? `_${seq}` : ''), 'utf8').length;
}

// ════════════════════════════════════════════════════════════════════════════════
// 1) BLOCK-MODE COMBINATIONS
// ════════════════════════════════════════════════════════════════════════════════
const SEQS = [0, 7];
const PLOADS = [['text', 'HELLO'], ['hex', 'deadbeef'], ['random', ''], ['text', '']];

for (const seq of SEQS) {
  for (const [mode, data] of PLOADS) {
    const pl  = plLen(mode, data, seq);
    const tag = `blk[${mode}:${data || '∅'}/seq${seq}]`;

    // Eth + IPv4 + UDP + Payload
    {
      const blocks = [ETH(), IPV4({ protocol: 'udp' }), UDP(), PL(mode, data)];
      const f = buildFrame(profileFromBlocks(blocks), seq);
      verify(`${tag} ETH/IP/UDP`, f, { etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + pl, udpLen: 8 + pl });
    }
    // Eth + IPv4 + TCP + Payload
    {
      const blocks = [ETH(), IPV4({ protocol: 'tcp' }), TCP(), PL(mode, data)];
      const f = buildFrame(profileFromBlocks(blocks), seq);
      verify(`${tag} ETH/IP/TCP`, f, { etherType: 0x0800, proto: 6, ipTotLen: 20 + 20 + pl });
    }
    // Eth + IPv4 + ICMP + Payload
    {
      const blocks = [ETH(), IPV4({ protocol: 'icmp' }), ICMP(), PL(mode, data)];
      const f = buildFrame(profileFromBlocks(blocks), seq);
      verify(`${tag} ETH/IP/ICMP`, f, { etherType: 0x0800, proto: 1, ipTotLen: 20 + 8 + pl, icmpType: 8 });
    }
    // Eth + VLAN + IPv4 + UDP + Payload
    {
      const blocks = [ETH(), VLAN({ vlanId: 200 }), IPV4(), UDP(), PL(mode, data)];
      const f = buildFrame(profileFromBlocks(blocks), seq);
      verify(`${tag} ETH/VLAN/IP/UDP`, f,
        { vlan: { vid: 200 }, etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + pl, udpLen: 8 + pl });
    }
    // Eth + VLAN + IPv4 + TCP + Payload
    {
      const blocks = [ETH(), VLAN({ vlanId: 300 }), IPV4({ protocol: 'tcp' }), TCP(), PL(mode, data)];
      const f = buildFrame(profileFromBlocks(blocks), seq);
      verify(`${tag} ETH/VLAN/IP/TCP`, f, { vlan: { vid: 300 }, etherType: 0x0800, proto: 6, ipTotLen: 20 + 20 + pl });
    }
  }
}

// Combinations independent of payload/seq
{
  // Eth + ARP
  verify('blk ETH/ARP', buildFrame(profileFromBlocks([ETH({ etherType: '0x0806' }), ARP()]), 0),
    { etherType: 0x0806 });
  // Eth + VLAN + ARP
  verify('blk ETH/VLAN/ARP', buildFrame(profileFromBlocks([ETH(), VLAN({ vlanId: 50 }), ARP()]), 0),
    { vlan: { vid: 50 }, etherType: 0x0806 });
  // Eth + IPv4 + UDP (no payload block)
  verify('blk ETH/IP/UDP(no-pl)', buildFrame(profileFromBlocks([ETH(), IPV4(), UDP()]), 0),
    { etherType: 0x0800, proto: 17, ipTotLen: 28, udpLen: 8 });
  // Eth + IPv4 (no L4)  → proto defaults to 0
  verify('blk ETH/IP(only)', buildFrame(profileFromBlocks([ETH(), IPV4({ protocol: '' })]), 0),
    { etherType: 0x0800, ipTotLen: 20 });
  // Eth + Payload (raw)
  verify('blk ETH/Payload', buildFrame(profileFromBlocks([ETH({ etherType: '0x88b5' }), PL('hex', 'aabbccdd')]), 0),
    { etherType: 0x88b5 });
}

// ════════════════════════════════════════════════════════════════════════════════
// 2) LEGACY PROTOCOL PATH (no blocks)
// ════════════════════════════════════════════════════════════════════════════════
for (const seq of SEQS) {
  for (const [mode, data] of PLOADS) {
    const pl = plLen(mode, data, seq);
    const base = { srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
                   srcIp: '192.168.1.10', dstIp: '192.168.1.20', payload: { mode, data } };
    const tag = `leg[${mode}:${data || '∅'}/seq${seq}]`;

    verify(`${tag} udp`, buildFrame({ ...base, protocol: 'udp', udp: { srcPort: 1, dstPort: 2 } }, seq),
      { etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + pl, udpLen: 8 + pl });
    verify(`${tag} tcp`, buildFrame({ ...base, protocol: 'tcp', tcp: { srcPort: 1, dstPort: 2 } }, seq),
      { etherType: 0x0800, proto: 6, ipTotLen: 20 + 20 + pl });
    verify(`${tag} icmp`, buildFrame({ ...base, protocol: 'icmp', icmp: { type: 8, code: 0 } }, seq),
      { etherType: 0x0800, proto: 1, ipTotLen: 20 + 8 + pl, icmpType: 8 });

    // VLAN via legacy profile.vlan
    verify(`${tag} udp+vlan`,
      buildFrame({ ...base, protocol: 'udp', udp: { srcPort: 1, dstPort: 2 }, vlan: { enabled: true, id: 123 } }, seq),
      { vlan: { vid: 123 }, etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + pl, udpLen: 8 + pl });
  }
}
// ARP / raw / ipv4 legacy
verify('leg arp', buildFrame({ protocol: 'arp', srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
  arp: { operation: 1, senderIp: '1.1.1.1', targetIp: '2.2.2.2' } }, 0), { etherType: 0x0806 });
verify('leg raw', buildFrame({ protocol: 'raw', srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
  etherType: '0x88b5', payload: { mode: 'hex', data: 'aabbccddeeff' } }, 0), { etherType: 0x88b5 });
verify('leg ipv4(proto=udp17,no-l4)', buildFrame({ protocol: 'ipv4', srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
  srcIp: '1.1.1.1', dstIp: '2.2.2.2', ipv4: { ipProto: 17 }, payload: { mode: 'text', data: 'x' } }, 0),
  { etherType: 0x0800, proto: 17 });

// ════════════════════════════════════════════════════════════════════════════════
// 3) LARGE / EDGE payloads
// ════════════════════════════════════════════════════════════════════════════════
{
  const big = 'A'.repeat(1400);
  verify('edge udp 1400B', buildFrame({ protocol: 'udp', srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
    srcIp: '1.1.1.1', dstIp: '2.2.2.2', udp: { srcPort: 1, dstPort: 2 }, payload: { mode: 'text', data: big } }, null),
    { etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + big.length, udpLen: 8 + big.length });
  // odd-length payload (exercises odd-byte checksum path)
  verify('edge udp odd-len', buildFrame({ protocol: 'udp', srcMac: 'aa:bb:cc:dd:ee:ff', dstMac: 'ff:ff:ff:ff:ff:ff',
    srcIp: '1.1.1.1', dstIp: '2.2.2.2', udp: { srcPort: 1, dstPort: 2 }, payload: { mode: 'hex', data: 'aabbcc' } }, null),
    { etherType: 0x0800, proto: 17, ipTotLen: 20 + 8 + 3, udpLen: 8 + 3 });
}

// ════════════════════════════════════════════════════════════════════════════════
// 4) QinQ (double VLAN) — verified explicitly (decoder above handles single tag only)
// ════════════════════════════════════════════════════════════════════════════════
{
  const blocks = [ETH(), VLAN({ vlanId: 10 }), VLAN({ vlanId: 20 }), IPV4(), UDP(), PL('text', 'Q')];
  const f = buildFrame(profileFromBlocks(blocks), 0);
  // eth(14): ...0x8100 | vlan1(14..18): tci10, innerET should be 0x8100 (next is VLAN)
  // vlan2(18..22): tci20, innerET should be 0x0800 (next is IPv4) | IPv4 @ 22
  check('qinq/eth.tpid',   f.readUInt16BE(12) === 0x8100, `eth TPID 0x${f.readUInt16BE(12).toString(16)}`);
  check('qinq/v1.vid',     (f.readUInt16BE(14) & 0xFFF) === 10, `v1 vid ${f.readUInt16BE(14) & 0xFFF}`);
  check('qinq/v1.innerET', f.readUInt16BE(16) === 0x8100, `v1 innerET 0x${f.readUInt16BE(16).toString(16)} (expected 0x8100)`);
  check('qinq/v2.vid',     (f.readUInt16BE(18) & 0xFFF) === 20, `v2 vid ${f.readUInt16BE(18) & 0xFFF}`);
  check('qinq/v2.innerET', f.readUInt16BE(20) === 0x0800, `v2 innerET 0x${f.readUInt16BE(20).toString(16)} (expected 0x0800)`);
  check('qinq/ip.version', (f[22] >> 4) === 4, `ip version ${f[22] >> 4}`);
  check('qinq/ip.csum',    checksumValid(f.subarray(22, 22 + (f[22] & 0x0F) * 4)), 'IPv4 csum invalid');
}

// ── report ──────────────────────────────────────────────────────────────────────
console.log(`\n  frameComboTest: ${passes.length} checks passed, ${failures.length} failed\n`);
if (failures.length) {
  console.log('  FAILURES:');
  for (const f of failures) console.log('   ✗ ' + f);
  console.log('');
  process.exit(1);
} else {
  console.log('  ✓ all frame combinations valid\n');
}
