'use strict';

function sendProxyError(res, err) {
  res.status(err.statusCode || 503).json({
    ok: false,
    error: err.message,
    source: 'csharp-local-api'
  });
}

function proxyGet(router, publicPath, csharpPath, csharp) {
  router.get(publicPath, async (_req, res) => {
    try {
      res.json(await csharp.get(csharpPath));
    } catch (err) {
      sendProxyError(res, err);
    }
  });
}

function proxyPost(router, publicPath, csharpPath, csharp) {
  router.post(publicPath, async (req, res) => {
    try {
      res.json(await csharp.post(csharpPath, req.body || {}));
    } catch (err) {
      sendProxyError(res, err);
    }
  });
}

module.exports = { proxyGet, proxyPost, sendProxyError };
