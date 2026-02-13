const http = require('http');
const path = require('path');
const fs = require('fs');
const WebSocket = require('ws');
const { URL } = require('url');

const PORT = process.env.PORT || 7777;
const MAX_EVENTS = 2000;
const MAX_BODY_BYTES = Number(process.env.TELEMETRY_MAX_BYTES || 50 * 1024 * 1024);

const events = [];
const artifacts = new Map();
const streamClients = new Set();
let nextConnId = 1;
let nextEventId = 1;

function recordAndBroadcast(evt) {
  evt.Id = nextEventId++;
  events.push(evt);
  if (events.length > MAX_EVENTS) events.shift();
  const json = JSON.stringify(evt);
  for (const client of streamClients) {
    if (client.readyState === WebSocket.OPEN) client.send(json);
  }
}

function recordArtifactEmit(evt) {
  const payload = evt && evt.Payload ? evt.Payload : null;
  if (!payload || typeof payload !== 'object') return;
  const id = payload.id;
  if (!id) return;
  const state = artifacts.get(id) || { versions: [] };
  const version = state.versions.length + 1;
  const entry = {
    id,
    version,
    language: payload.language || 'text',
    content: payload.content ?? '',
    meta: payload.meta || null,
    agent: evt.Agent || 'unknown',
    timestampMs: evt.TimestampMs || Date.now()
  };
  state.versions.push(entry);
  state.latestVersion = version;
  artifacts.set(id, state);

  const updateEvt = {
    ...evt,
    Type: 'artifact_update',
    Topic: evt.Topic || 'artifacts',
    Payload: entry
  };
  recordAndBroadcast(updateEvt);
}

function handleIncomingEvent(evt) {
  if (!evt || typeof evt !== 'object') return;
  if (evt.Type === 'artifact_emit') {
    recordArtifactEmit(evt);
    return;
  }
  recordAndBroadcast(evt);
}

async function readJson(req, limitBytes = MAX_BODY_BYTES) {
  let size = 0;
  let body = '';
  for await (const chunk of req) {
    size += chunk.length;
    if (size > limitBytes) throw new Error('payload too large');
    body += chunk.toString();
  }
  if (!body) throw new Error('empty body');
  return JSON.parse(body);
}

const server = http.createServer(async (req, res) => {
  if (req.url === '/' || req.url === '/index.html') {
    const file = path.join(__dirname, 'viewer.html');
    const html = fs.readFileSync(file, 'utf8');
    res.writeHead(200, { 'Content-Type': 'text/html' });
    return res.end(html);
  }

  if (req.url.startsWith('/events')) {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const after = Number(url.searchParams.get('after') ?? url.searchParams.get('afterId') ?? '0');
    const filtered = Number.isFinite(after) && after > 0
      ? events.filter(e => e.Id > after)
      : events.slice(Math.max(0, events.length - MAX_EVENTS));

    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    return res.end(JSON.stringify({ events: filtered, lastId: nextEventId - 1 }));
  }

  if (req.url.startsWith('/artifacts')) {
    const snapshot = {};
    for (const [id, state] of artifacts.entries()) {
      snapshot[id] = state.versions || [];
    }
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    return res.end(JSON.stringify({ artifacts: snapshot }));
  }

  if (req.url === '/ingest' && req.method === 'POST') {
    try {
      const payload = await readJson(req);
      handleIncomingEvent(payload);
      console.log(`[telemetry-server] http ingest type=${payload.Type || 'unknown'} agent=${payload.Agent || 'n/a'}`);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ ok: true, id: payload.Id }));
    } catch (err) {
      console.error('[telemetry-server] ingest error', err.message);
      res.writeHead(400, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ ok: false, error: err.message }));
    }
  }

  res.writeHead(404);
  res.end('not found');
});

const wss = new WebSocket.Server({ noServer: true });

server.on('upgrade', (req, socket, head) => {
  const { url } = req;
  if (url === '/ingest' || url === '/stream') {
    wss.handleUpgrade(req, socket, head, (ws) => {
      ws.connId = nextConnId++;
      ws.path = url;
      wss.emit('connection', ws, req);
    });
  } else {
    socket.destroy();
  }
});

wss.on('connection', (ws) => {
  console.log(`[telemetry-server] ws connected id=${ws.connId} path=${ws.path}`);

  if (ws.path === '/stream') {
    streamClients.add(ws);
    // Send backlog on connect
    ws.send(JSON.stringify({ type: 'backlog', payload: events }));
    console.log(`[telemetry-server] stream client id=${ws.connId} backlog sent ${events.length} events`);
  }

  ws.on('message', (data) => {
    if (ws.path !== '/ingest') return;
    try {
      const parsed = JSON.parse(data.toString());
      handleIncomingEvent(parsed);
      console.log(`[telemetry-server] ingest id=${ws.connId} type=${parsed.Type || 'unknown'} agent=${parsed.Agent || 'n/a'}`);
    } catch (err) {
      console.error(`[telemetry-server] Invalid ingest payload from id=${ws.connId}:`, err.message);
    }
  });

  ws.on('close', () => {
    streamClients.delete(ws);
    console.log(`[telemetry-server] ws closed id=${ws.connId} path=${ws.path}`);
  });

  ws.on('error', (err) => {
    console.error(`[telemetry-server] ws error id=${ws.connId} path=${ws.path}:`, err.message);
  });
});

server.listen(PORT, () => {
  console.log(`[telemetry-server] listening on http://localhost:${PORT}`);
  console.log('Endpoints:');
  console.log('  POST http://localhost:%d/ingest  <- game client', PORT);
  console.log('  GET  http://localhost:%d/events?after=<id>  <- browser viewer (poll)', PORT);
  console.log('  ws://localhost:%d/stream  <- optional live viewer', PORT);
  console.log('  http://localhost:%d/       <- simple viewer', PORT);
});
