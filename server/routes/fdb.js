'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }

router.post('/fdb/read', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('fdbread', req.body || {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

router.post('/fdb/write', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('fdbwrite', req.body || {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

router.post('/fdb/delete', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('fdbdelete', req.body || {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

router.post('/fdb/flush', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('fdbflush', {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

module.exports = router;
