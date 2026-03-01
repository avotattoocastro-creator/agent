# AvoTelemetryAgent

A lightweight .NET 8 Windows agent that reads **Assetto Corsa** shared memory directly
and streams telemetry to connected clients over WebSockets, exposes a REST API,
and advertises itself on the LAN via UDP.

---

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows (Assetto Corsa shared-memory is Windows-only) |
| Runtime | [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Assetto Corsa | Must be running for live data; the agent starts without it |

---

## Quick start

```bash
# 1. Restore & build
dotnet build AvoTelemetryAgent.csproj

# 2. Run (Assetto Corsa should already be running)
dotnet run --project AvoTelemetryAgent.csproj
```

The agent listens on **http://0.0.0.0:8181** by default.

---

## Configuration (`appsettings.json`)

```json
{
  "AvoAgent": {
    "Token":            "change-me",   // auth token for WS and setup endpoints
    "HttpPort":         8181,          // HTTP / WebSocket port
    "DiscoveryPort":    8182,          // UDP LAN-discovery port
    "PhysicsHz":        60,            // physics read rate (Hz)
    "GraphicsHz":       20,            // graphics read rate (Hz)
    "StaticIntervalMs": 2000           // static re-read interval (ms)
  }
}
```

Override any value in `appsettings.Development.json` or via environment variables
(`AvoAgent__Token`, `AvoAgent__HttpPort`, …).

---

## Endpoints

### REST

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/ping` | — | Health check; returns `{ok, version, timeUtc}` |
| GET | `/api/info` | — | Agent status; returns machine name, connected flag, client count |
| POST | `/api/setup/apply` | ✓ | Save an AC setup `.ini` file to the setups folder |

#### POST `/api/setup/apply` — request body

```json
{
  "carId":               "ks_ferrari_488_gt3",
  "trackId":             "ks_nurburgring",
  "fileName":            "my_setup",
  "setupText":           "[CAR]\n...",
  "relativePathOptional": null
}
```

The file is saved to:
`%USERPROFILE%\Documents\Assetto Corsa\setups\{carId}\{trackId}\{fileName}.ini`

Set `relativePathOptional` to override the destination (still constrained to the
`setups` base directory — path traversal is rejected).

### WebSocket

```
ws://localhost:8181/ws?token=<your-token>
```

Or pass the token in the `X-AVO-TOKEN` request header.

Once connected, the agent streams JSON frames at up to **60 Hz**:

```json
{
  "tUtc":    "2025-01-01T00:00:00.000Z",
  "seq":     1,
  "carId":   "ks_ferrari_488_gt3",
  "trackId": "ks_nurburgring",
  "physics": {
    "speedKmh": 120.5, "rpm": 8200, "gear": 4,
    "throttle": 0.9, "brake": 0.0, "steer": -0.05,
    "clutch": 0.0, "latG": 0.3, "longG": -0.1, "yawRate": 0.02,
    "wheelSlip": [0,0,0,0], "tyreTemp": [85,86,85,86], "tyrePressure": [26,26,26,26]
  },
  "graphics": {
    "session": "AC_RACE", "acStatus": "AC_LIVE",
    "lap": 3, "lapTimeMs": 93000, "bestLapMs": 91500,
    "position": 1, "completedLaps": 2, "currentTimeMs": 93000
  },
  "statics": {
    "carModel": "ks_ferrari_488_gt3", "track": "ks_nurburgring",
    "maxRpm": 8500, "maxFuel": 110
  },
  "status": { "connected": true, "message": "ok" }
}
```

A **heartbeat** frame is sent every **1 second** even when AC is not running
(`status.connected = false`).

Slow clients will have stale frames **dropped** — only the latest frame is ever queued.

### LAN Discovery (UDP)

The agent broadcasts a UDP beacon every **2 seconds** on port **8182**:

```
AVO_AGENT|8181|MACHINE-NAME
```

---

## Folder structure

```
AvoTelemetryAgent/
├── AvoTelemetryAgent.csproj
├── Program.cs
├── appsettings.json
├── appsettings.Development.json
├── README.md
├── SharedMemory/
│   ├── AcEnums.cs               AC_STATUS, AC_SESSION_TYPE, AC_FLAG_TYPE, …
│   ├── SPageFilePhysics.cs      acpmf_physics  – sequential Pack=1, fixed buffers
│   ├── SPageFileGraphics.cs     acpmf_graphics – sequential Pack=1, fixed buffers
│   ├── SPageFileStatic.cs       acpmf_static   – sequential Pack=1, fixed buffers
│   └── AcSharedMemoryReader.cs  Opens MMFs and reads structs via raw pointer copy
├── Models/
│   ├── AcTelemetrySnapshot.cs   Unified snapshot + timestamp
│   └── TelemetryFrame.cs        JSON DTO streamed to WebSocket clients
└── Services/
    ├── AgentOptions.cs           Config binding
    ├── TelemetryService.cs       Background service: 60 Hz physics / 20 Hz graphics
    ├── WebSocketHub.cs           Multi-client WS manager, drop-old-frame channel
    └── LanDiscoveryService.cs    UDP broadcast every 2 s
```
