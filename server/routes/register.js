'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }

router.get('/register/status', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('registerstatus', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});

router.post('/register/read', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('registerread', req.body || {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

router.post('/register/write', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('registerwrite', req.body || {}, 15000) || {}) }); }
  catch (e) { wErr(res, e); }
});

module.exports = router;
