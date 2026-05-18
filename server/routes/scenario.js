'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }
function localCmd(req, cmd, body, ms) { return req.app.locals.localCmd(cmd, body || {}, ms || 10000); }

// Automation — via C# AutomationViewModel
router.post('/auto/run', async (req, res) => {
  try { res.json({ ok: true, ...(await localCmd(req, 'autorun', req.body, 60000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.get('/auto/status', async (req, res) => {
  try { res.json({ ok: true, ...(await localCmd(req, 'autostatus', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});
router.get('/auto/results', async (req, res) => {
  try { res.json({ ok: true, ...(await localCmd(req, 'autoresults', {}, 5000) || {}) }); }
  catch (e) { wErr(res, e); }
});

// Legacy /api/scenarios/* aliases
router.post('/scenarios/run',     async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'autorun',     req.body, 60000) || {}) }); } catch (e) { wErr(res, e); } });
router.get('/scenarios/status',   async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'autostatus',  {}, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.get('/scenarios/results',  async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'autoresults', {}, 5000) || {}) }); } catch (e) { wErr(res, e); } });

// Test case management — via C# MainViewModel.TestCaseMgrVM
router.get ('/testcases/status',        async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasesstatus', {},        5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/testcases/add-group',     async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasesaddgroup', req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/testcases/add',           async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasesadd', req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/testcases/select',        async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasesselect', req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/testcases/save-current',  async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasessavecurrent', {}, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/testcases/delete',        async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'testcasesdelete', req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });

// App / Sequence status + run
router.get ('/app/status',      async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'appstatus',      {},       5000) || {}) }); } catch (e) { wErr(res, e); } });
router.get ('/sequence/status', async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequencestatus', {},       5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/sequence/run',    async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequencerun',    {},      10000) || {}) }); } catch (e) { wErr(res, e); } });

// 6-port switch link status (via MDIO)
router.get ('/ports/link-status', async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'portslinkstatus', {}, 15000) || {}) }); } catch (e) { wErr(res, e); } });

// Sequence event builder
router.get ('/sequence/full',         async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequencegetfull',     {},       5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/sequence/event/add',    async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequenceaddevent',    req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/sequence/event/remove', async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequenceremoveevent', req.body, 5000) || {}) }); } catch (e) { wErr(res, e); } });
router.post('/sequence/events/clear', async (req, res) => { try { res.json({ ok: true, ...(await localCmd(req, 'sequenceclearevents', {},       5000) || {}) }); } catch (e) { wErr(res, e); } });

module.exports = router;
