# Telemetry Server (local dev)

Push-only telemetry collector for Supermarket Sim. The game firehoses JSON events over WebSocket; the server aggregates in memory and serves a simple browser viewer.

## Run
```bash
cd tools/telemetry-server
npm install
npm start
# visit http://localhost:7777/
```

## Config
- `TELEMETRY_MAX_BYTES` (default: 52428800) sets max HTTP ingest payload size.

## Endpoints
- `ws://localhost:7777/ingest` — game connects here and sends events (JSON envelopes)
- `ws://localhost:7777/stream` — browser subscribes to live events
- `http://localhost:7777/events` — current in-memory buffer (bounded)
- `http://localhost:7777/artifacts` — artifact history (unbounded, versioned)
- `http://localhost:7777/` — minimal viewer

## Event envelope (from game)
```json
{
  "Type": "llm_send",
  "Agent": "/root/Node/AgenticNPC",
  "Session": "guid",
  "TimestampMs": 1733680000000,
  "Payload": { "messageCount": 12, "toolCount": 3 }
}
```

The collector is intentionally dumb: it stores a ring buffer (`MAX_EVENTS`) and broadcasts to any connected stream clients. Extend as needed for persistence or richer queries.
