'use strict';

const http = require('http');

const baseUrl = process.env.CSHARP_API_BASE_URL || 'http://localhost:18080';
const timeoutMs = Number(process.env.CSHARP_API_TIMEOUT_MS || 2500);

function request(method, apiPath, body) {
  return new Promise((resolve, reject) => {
    const url = new URL(apiPath, baseUrl);
    const payload = body == null ? '' : JSON.stringify(body);

    const req = http.request({
      hostname: url.hostname,
      port: Number(url.port || 80),
      path: `${url.pathname}${url.search}`,
      method,
      timeout: timeoutMs,
      headers: {
        accept: 'application/json',
        'content-type': 'application/json',
        'content-length': Buffer.byteLength(payload)
      }
    }, (res) => {
      let raw = '';
      res.setEncoding('utf8');
      res.on('data', (chunk) => { raw += chunk; });
      res.on('end', () => {
        let data;
        try {
          data = raw ? JSON.parse(raw) : {};
        } catch {
          data = { ok: false, raw };
        }

        if (res.statusCode >= 400) {
          const err = new Error(data.error || `C# API returned HTTP ${res.statusCode}`);
          err.statusCode = res.statusCode;
          err.data = data;
          reject(err);
          return;
        }

        resolve(data);
      });
    });

    req.on('timeout', () => req.destroy(new Error(`C# API timeout after ${timeoutMs}ms`)));
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

async function health() {
  try {
    const data = await request('GET', '/api/health');
    return { connected: true, baseUrl, data };
  } catch (err) {
    return { connected: false, baseUrl, error: err.message };
  }
}

module.exports = {
  baseUrl,
  get: (apiPath) => request('GET', apiPath),
  post: (apiPath, body) => request('POST', apiPath, body),
  health
};
