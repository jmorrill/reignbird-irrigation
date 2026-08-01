# Rainbird Replacement App — Design

**Date:** 2026-07-31
**Status:** Approved for implementation

A local-first app for Rain Bird irrigation controllers: an ASP.NET Core backend that
speaks the LNK WiFi protocol directly to the controller over the LAN, and a
React/TypeScript front end.

Companion documents:
- `docs/rainbird-protocol.md` — the protocol reference
- `docs/feature-scope.md` — what the hardware supports, and where each feature comes from

---

## 1. Goals

1. Control a Rain Bird controller over the LAN with **no cloud account and no
   internet dependency** for core function.
2. Meet the clarity and polish expected of a modern consumer irrigation app.
3. Deliver every feature the Rain Bird hardware can actually support (see the
   capability map in `feature-scope.md`), providing in our own backend what
   comparable products provide from a cloud: weather, history, usage, and skip logic.
4. Be genuinely good on a desktop browser, not just a phone.

### Non-goals for v1

- Bluetooth controllers (TBOS-BT, ESP-BAT-BT) — a different transport entirely.
- Yard Map polygon drawing.
- Rain Bird cloud/relay access. The protocol supports it; we deliberately don't.
- Multi-user accounts and sharing.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Browser — React 19 + TypeScript + MobX + Framer Motion     │
└───────────────┬─────────────────────────┬───────────────────┘
                │ REST (JSON)             │ SignalR (live state)
┌───────────────▼─────────────────────────▼───────────────────┐
│  RainBird.Server  (ASP.NET Core 10)                         │
│                                                             │
│   Controllers/  REST endpoints          Hubs/  SignalR      │
│   Services/     PollingService · WeatherService             │
│                 SkipEvaluator  · UsageEstimator             │
│                 HistoryRecorder                             │
│   Data/         EF Core + SQLite                            │
└───────────────┬─────────────────────────────────────────────┘
                │ RainBird.Protocol (class library)
                │   LnkClient → JSON-RPC → AES-256-CBC → HTTP
┌───────────────▼─────────────────────────────────────────────┐
│  Controller on the LAN   ·  or  RainBird.Simulator          │
└─────────────────────────────────────────────────────────────┘
```

### 2.1 Projects

| Project | Purpose |
|---|---|
| `RainBird.Protocol` | Pure protocol library. Crypto, JSON-RPC, SIP codec, typed client. No ASP.NET, no database. |
| `RainBird.Protocol.Tests` | xUnit. Round-trip crypto tests, SIP encode/decode tests against real captured frames. |
| `RainBird.Simulator` | An in-process fake controller implementing the wire protocol. Lets the whole stack run and be tested without hardware. |
| `RainBird.Server` | ASP.NET Core API, SignalR hub, EF Core/SQLite, background services. Hosts the built SPA. |
| `web` | Vite + React + TypeScript + MobX + Framer Motion. |

### 2.2 Why this split

`RainBird.Protocol` knows nothing about our application. It can be lifted out and
reused, and — more importantly — it can be tested exhaustively in isolation, which is
what a protocol with no published specification needs. `RainBird.Simulator` exists because
verifying against real hardware is slow and the hardware may not be present; every
feature must be demonstrable end-to-end without it.

---

## 3. The protocol layer

### 3.1 Crypto — `RainBirdCipher`

Implements §2 of the protocol doc: AES-256-CBC, key = SHA-256(password), zero
padding, wire format `SHA256(plaintext) || IV || ciphertext`.

The one subtlety worth restating: the response hash covers the decrypted string
*including* its trailing NUL padding, because the app hashes the string before
stripping NULs. Our implementation must do the same or every response fails
verification.

### 3.2 SIP codec — `SipCodec`

Field offsets are **in nibbles**, which is a genuine foot-gun. The codec exposes a
`SipField(int NibblePosition, int NibbleLength)` type and does the conversion once,
so no call site ever does hex arithmetic by hand.

Command and response definitions live in a single C# table, covering a considerably
wider command surface than the community protocol lists in circulation.

### 3.3 Client — `LnkClient`

- One outstanding request at a time, enforced by a `SemaphoreSlim`, with the app's
  50 ms inter-request delay. The controller genuinely cannot handle concurrency.
- The app's retry policy: `IOException` → 1.5 s, max 3; HTTP 503 → 50 ms, max 3;
  three timeouts → drop the connection.
- Wrong password surfaces as `RainBirdAuthenticationException`, not a generic
  failure, because a hash mismatch is the *only* signal the protocol gives.
- A `CombinedState` call (SIP `4C`) is the primary poll: one round trip yields time,
  date, rain delay, sensor state, irrigation state, seasonal adjust, remaining
  runtime and active station.

### 3.4 Capability detection

Models differ substantially (`ControllerType`, §7 of the protocol doc). On connect we
read model/version (`02`), available stations (`03`), and probe optional commands
with `CommandSupportRequest` (`04`). The result is a `ControllerCapabilities` record
that the API returns to the client, so the UI can hide what a given model can't do
rather than showing controls that will NAK.

---

## 4. Server layer

### 4.1 Background services

| Service | Responsibility |
|---|---|
| `PollingService` | Polls `4C` every 5 s while any client is connected, 60 s otherwise. Pushes deltas over SignalR. |
| `HistoryRecorder` | Watches the poll stream for station transitions and writes completed runs to the database. This is how we get a watering history the controller does not itself retain. |
| `WeatherService` | Fetches forecast from Open-Meteo (no API key, free, no attribution burden) for the controller's coordinates. Cached, refreshed hourly. |
| `SkipEvaluator` | Applies rain/freeze/wind rules to the forecast each morning and, when triggered, sets a rain delay and records a skip event. |

### 4.2 Data model (SQLite via EF Core)

```
Controller   id, name, host, password(encrypted at rest), modelId, serial,
             latitude, longitude, capabilitiesJson, lastSeenUtc
Zone         id, controllerId, stationNumber, name, photoPath, plantType,
             soilType, sunExposure, slope, nozzleFlowGpm, enabled
Run          id, controllerId, stationNumber, startedUtc, endedUtc,
             durationSeconds, trigger(Manual|Program|Test), estimatedGallons
SkipEvent    id, controllerId, dateUtc, reason(Rain|Freeze|Wind|Saturation), details
WeatherDay   controllerId, date, tempHi, tempLo, precipMm, precipProbability,
             windKph, conditionCode
Setting      key, value
```

Zone metadata lives here rather than on the controller because the controller has no
storage for it — this is exactly the "our backend does what a cloud would have done"
principle.

### 4.3 API surface

```
GET    /api/controllers                    list, with live status
POST   /api/controllers                    add (host + password) → probes and saves
GET    /api/controllers/{id}/state         combined state
GET    /api/controllers/{id}/capabilities
GET    /api/controllers/{id}/zones
PUT    /api/controllers/{id}/zones/{n}     zone metadata
POST   /api/controllers/{id}/zones/{n}/run       { minutes }
POST   /api/controllers/{id}/stop
POST   /api/controllers/{id}/advance
POST   /api/controllers/{id}/test          { minutes }
GET    /api/controllers/{id}/programs      decoded schedule
PUT    /api/controllers/{id}/programs/{p}  write schedule pages
POST   /api/controllers/{id}/programs/{p}/run
GET    /api/controllers/{id}/queue
GET    /api/controllers/{id}/rain-delay
PUT    /api/controllers/{id}/rain-delay    { days }
PUT    /api/controllers/{id}/seasonal-adjust
GET    /api/controllers/{id}/history       ?from=&to=
GET    /api/controllers/{id}/usage         monthly rollup
GET    /api/controllers/{id}/weather
GET    /api/controllers/{id}/diagnostics   raw SIP log
```

SignalR hub `/hubs/controller` pushes `stateChanged`, `runStarted`, `runCompleted`,
`skipApplied`.

---

## 5. Front end

### 5.1 Stack

React 19, TypeScript (strict), Vite, MobX 6 (`makeAutoObservable`, no decorators),
Framer Motion 12, React Router. No component library — the visual identity is the
point, and a library would fight it.

### 5.2 Stores

| Store | Holds |
|---|---|
| `ControllerStore` | controller list, selection, live state, capabilities |
| `ZoneStore` | zones, metadata, per-zone run state |
| `ScheduleStore` | programs, start times, run times, frequency |
| `HistoryStore` | runs, calendar aggregation, usage rollups |
| `WeatherStore` | forecast, skip events |
| `UiStore` | active tab, modals, toasts, theme |

A single `RootStore` wires them together and owns the SignalR connection, so live
updates land in one place and fan out to observers.

### 5.3 Screens

Four tabs, adapted to also work wide:

- **Events** — weather strip, upcoming/past runs grouped by day, monthly usage tiles.
- **Zones** — photo-card grid, per-zone detail sheet, quick run.
- **Schedules** — program editor and month calendar.
- **Settings** — controller info, WiFi, clock, seasonal adjust, rain sensor,
  diagnostics.

Persistent quick-run FAB and a "now watering" bar that appears whenever the
controller is running, with the live countdown from the poll stream.

On wide viewports the tab bar becomes a left rail and the content area uses the extra
width for a two-pane layout (list + detail) rather than stretching cards.

### 5.4 Visual direction

Neutral surfaces, one confident accent, semantic color only where it means something.
That restraint is what makes an interface full of numbers read as calm. On top of it:
a deeper, more saturated palette than is usual for the category, real motion, and dark
mode treated as a first-class theme rather than an inversion.

Motion (Framer Motion), used with restraint:
- Tab transitions: shared-layout underline, content cross-fade with 8px rise.
- Zone cards: `layoutId` promotion into the detail sheet.
- Running zone: a slow breathing pulse on the card border — one clear signal that
  something is happening right now.
- Countdown ring: animated `pathLength` on an SVG circle.
- Respect `prefers-reduced-motion` throughout.

---

## 6. Testing

| Layer | Approach |
|---|---|
| Crypto | Round-trip; known-answer tests; verify the NUL-padding hash quirk explicitly. |
| SIP codec | Encode/decode against known-good frames and frames captured from hardware. |
| `LnkClient` | Against `RainBird.Simulator` — covers retries, serialization, auth failure. |
| Services | `HistoryRecorder` and `SkipEvaluator` unit tests over synthetic poll streams. |
| API | Integration tests via `WebApplicationFactory` with the simulator wired in. |
| Front end | Vitest for store logic. |

The simulator is what makes this tractable: the entire stack is verifiable without a
sprinkler controller on the desk.

---

## 7. Assumptions

Recorded explicitly because they were not specified and the work proceeds under them:

1. **No physical controller is available during development.** Everything is built
   and verified against `RainBird.Simulator`, which implements the wire protocol as
   documented. Pointing the app at real hardware is a configuration change, and the
   protocol document is detailed enough that this should work — but it will need one
   real-hardware pass to confirm, particularly the schedule page layouts, which vary
   by model family.
2. **Target model family is the ESP-ME3 / ESP-TM2 class** (LNK2 WiFi, program-based).
   These are the most common residential units. Other models degrade gracefully via
   capability detection.
3. **Single-user, trusted LAN.** No authentication on our own API in v1; it binds to
   localhost by default.
4. Controller passwords are encrypted at rest with ASP.NET Core Data Protection.

---

## 8. Build order

1. `RainBird.Protocol` — crypto, SIP codec, client. Tested in isolation.
2. `RainBird.Simulator` — enough of a controller to drive the stack.
3. `RainBird.Server` — data model, API, polling, SignalR.
4. `web` — design system, then Events, Zones, Schedules, Settings.
5. Weather, skip logic, usage estimation.
6. End-to-end verification against the simulator.
