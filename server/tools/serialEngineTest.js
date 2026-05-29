'use strict';
/**
 * serialEngineTest.js — exercise the serial command engine without hardware.
 * Verifies: response pairing, ERR handling, command serialisation, and that a
 * late reply from a timed-out command does NOT get mis-paired with the next one.
 *   node tools/serialEngineTest.js
 */
const { SerialSession, _feedResponseLines } = require('../services/serialBridge')._test;

const fails = [];
const ck = (n, c, d) => { if (!c) fails.push(`${n} — ${d}`); };
const feed = (s, line) => _feedResponseLines(s, [line]);

// Fake session backed by a fake port that records writes (never auto-replies).
function makeSession() {
  const s = new SerialSession('/dev/fake');
  s.writes = [];
  s.port = { write: (buf, cb) => { s.writes.push(buf.toString('utf8')); cb && cb(); } };
  return s;
}
const wait = (ms) => new Promise(r => setTimeout(r, ms));

(async () => {
  // 1) basic OK pairing
  {
    const s = makeSession();
    const p = s.command('read 0x10', 1000);
    await wait(5); feed(s, 'OK 0x1234');
    ck('basic.ok', (await p) === '0x1234', 'expected 0x1234');
  }
  // 2) ERR rejection
  {
    const s = makeSession();
    const p = s.command('bad', 1000).then(() => 'resolved', e => `rej:${e.message}`);
    await wait(5); feed(s, 'ERR nope');
    ck('basic.err', (await p) === 'rej:nope', 'expected reject with "nope"');
  }
  // 3) serialisation — second command must not be written until first resolves,
  //    and replies pair in order.
  {
    const s = makeSession();
    const p1 = s.command('first', 1000);
    const p2 = s.command('second', 1000);
    await wait(5);
    ck('serial.one-in-flight', s.writes.length === 1, `wrote ${s.writes.length} (expected 1)`);
    feed(s, 'OK A'); ck('serial.p1', (await p1) === 'A', 'p1 should get A');
    await wait(5);
    ck('serial.second-written', s.writes.length === 2, 'second should be written after first resolved');
    feed(s, 'OK B'); ck('serial.p2', (await p2) === 'B', 'p2 should get B');
  }
  // 4) timeout straggler isolation — first times out; its late reply must be
  //    ignored, and the next command must get ITS OWN reply, not the straggler.
  {
    const s = makeSession();
    const p1 = s.command('slow', 60).then(() => 'resolved', e => `rej:${e.message}`);
    ck('to.p1-timeout', (await p1).startsWith('rej:'), 'p1 should reject on timeout');
    // Late reply to the timed-out command arrives now:
    feed(s, 'OK STRAGGLER');
    // Next command issued after timeout; must drain past _DRAIN_MS then write.
    const p2 = s.command('next', 1000);
    await wait(300); // > _DRAIN_MS
    ck('to.p2-written', s.writes.includes('next\r\n'), 'next command should be written after drain');
    feed(s, 'OK REAL');
    ck('to.p2-correct', (await p2) === 'REAL', 'p2 must get REAL, not STRAGGLER');
  }
  // 5) unsolicited line with no in-flight command is ignored (no crash)
  {
    const s = makeSession();
    feed(s, 'OK noise'); // must not throw
    ck('unsolicited.ignored', true, '');
  }

  if (fails.length) {
    console.log(`\n  serialEngineTest: ${fails.length} FAILED\n`);
    fails.forEach(f => console.log('   ✗ ' + f));
    process.exit(1);
  }
  console.log('\n  serialEngineTest: all checks passed — serialised, correlated, timeout-safe\n');
})();
