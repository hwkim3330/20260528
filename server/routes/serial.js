'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }

router.get('/serial/status', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialstatus', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/connect', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialopen', req.body || {}, 8000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/disconnect', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialclose', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/send', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialwrite', req.body || {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/clear', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialclear', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/break', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialcontrol', { cmd: 'break' }, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.post('/serial/control', async (req, res) => {
  try { res.json({ ok: true, ...(await req.app.locals.localCmd('serialcontrol', req.body || {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});

module.exports = router;
