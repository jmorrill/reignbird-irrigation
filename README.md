# Reignbird

A local-first app for Rain Bird irrigation controllers. It talks directly to a Rain
Bird LNK/LNK2 WiFi controller over your LAN — no cloud account, no internet
dependency for anything that matters — in a modern, responsive interface.

Rain Bird publishes no specification for the device protocol. The reference this
project works from, checked throughout against a physical controller, is in
[`docs/rainbird-protocol.md`](docs/rainbird-protocol.md).

Reignbird is an independent project. It is not affiliated with, endorsed by, or
supported by Rain Bird Corporation.

---

## What it does

| | |
|---|---|
| **Control** | Run a zone, run a program, test every zone, stop, skip to the next zone |
| **Live state** | What is watering right now, with a countdown that ticks in real time |
| **Plans** | Watering schedules this app runs itself: several passes a day, cycle and soak, watering windows — arrangements the hardware cannot express |
| **Schedules** | Read and write the controller's own programs, on firmware that exposes them |
| **History** | Every run logged, with a month calendar and estimated water use |
| **Weather** | Five-day forecast, and rain / freeze / wind / saturation skips |
| **Zones** | Names, photos, plant and soil type, sprinkler head, nozzle flow rate |
| **Diagnostics** | A console showing the raw SIP bytes going to and from the controller |
| **Installable** | A PWA: installs to a home screen or dock, runs in its own window, and its shell loads offline |
| **Accounts** | Username and password sign-in, JWT sessions, and account management from Settings |
| **Alerts** | Push notifications when a plan fails, a controller goes quiet, or a zone reports a fault |

The controller itself stores none of the metadata, history or weather. The things a
comparable product would keep in its cloud, this app keeps in a local SQLite file.

## What it can't do

Rain Bird residential hardware has no soil moisture sensor and no flow meter, so
anything depending on those is either absent or clearly labelled an estimate. The
feature-by-feature analysis is in [`docs/feature-scope.md`](docs/feature-scope.md).
Bluetooth-only controllers (TBOS-BT, ESP-BAT-BT) are out of scope: they have no HTTP
endpoint.

---

## Running it

Requirements: .NET 10 SDK and Node 20+.

### With the simulator — no hardware needed

```bash
cd web && npm install && npm run build
cd ../src/RainBird.Server
RainBird__UseSimulator=true dotnet run --urls http://127.0.0.1:5056
```

Open <http://127.0.0.1:5056>. A virtual ESP-ME3 with eight named zones and six weeks
of watering history is seeded on first run, so every screen has something real in it.

### Against a real controller

```bash
cd web && npm install && npm run build
cd ../src/RainBird.Server && dotnet run
```

Open <http://localhost:5056>. It binds every interface, so it is also reachable at
this machine's address on your network — see [Security notes](#security-notes) before
leaving it that way.

Then add your controller from **Settings → Add controller**: its IP address on your
network and the device password set on the LNK module. Coordinates are optional and
only needed for the forecast and weather skips.

### With Docker

```bash
cp .env.example .env      # set TZ, and PUID/PGID on a Linux host
docker compose up -d
```

Open <http://localhost:5056> and create the first account. The image is
self-contained — it carries its own copy of .NET, so the base image has no runtime in
it at all — and runs as a non-root user on a chiseled base with no shell and no
package manager.

**Set `TZ`.** Watering plans run in local time. Without it every new controller
defaults to UTC, which does not fail in any visible way; it just waters at the wrong
hour. Any IANA name works.

#### Where the data lives

On the host, not inside the container and not in a Docker volume — `./data` by
default, or wherever `REIGNBIRD_DATA` points:

```
data/store/     the SQLite database, the keys that encrypt controller passwords,
                and the token signing key
data/media/     zone photos
```

**That directory is the whole backup.** Everything else can be rebuilt from this
repository; the contents of `data/store` cannot. Lose it and every controller has to
be added again, because the keys that decrypt their passwords went with it.

A bind mount keeps the host's ownership, so the container has to run as a user that
can write there. On Linux set `PUID` and `PGID` in `.env` to your own (`id -u`,
`id -g`) and nothing needs chowning; on Docker Desktop the defaults are fine. Get it
wrong and the app says so on the first line of its log rather than failing later with
something about SQLite.

Running it directly rather than in a container, `REIGNBIRD_DATA_DIR` and
`REIGNBIRD_MEDIA_DIR` do the same job.

#### Creating the first account unattended

For a container that comes up with nobody watching, set `REIGNBIRD_ADMIN_USER` and
`REIGNBIRD_ADMIN_PASSWORD` in `.env`. The account is only ever created, never
modified, so leaving them set cannot silently undo a password you changed later.
Otherwise the first person to open the app is asked to create the account — do that
before exposing the port, since whoever gets there first claims it.

#### Building for another architecture

`linux/amd64` and `linux/arm64` are both supported — a Raspberry Pi 4 or 5 on 64-bit
Linux is a good home for this.

```bash
docker buildx build --platform linux/arm64 -t reignbird:arm64 --load .
```

Cross-building costs nothing here. Every stage that runs a command is pinned to the
machine's own architecture and only the final stage takes the target's, so the .NET
SDK cross-publishes and the last stage merely copies files. Nothing runs under QEMU.
Both architectures at once, straight to a registry:

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t <registry>/reignbird:1.0 --push .
```

#### Without a Dockerfile

The .NET SDK builds images itself, which is the mechanism Visual Studio's container
publishing uses. The project already carries the image metadata, so this is enough:

```bash
cd web && npm ci && npm run build
cd ../src
dotnet publish RainBird.Server -c Release -r linux-arm64 --self-contained -t:PublishContainer
```

That produces the same kind of image without Docker doing the building — useful in a
pipeline that has the SDK but no daemon. It loads single-architecture images into a
local daemon; for a multi-architecture manifest, publish it to a registry instead
with `-p:ContainerRegistry=...`.

### Installing it as an app

Open it in Chrome or Edge and use **Settings → Install**, or the install icon in the
address bar. On iOS, Share → Add to Home Screen.

**It has to be served over HTTPS, or from localhost.** Browsers refuse to register a
service worker on any other origin, so over plain HTTP at a LAN or tailnet address
there is no install option and no offline support — the app still works, it just
stays an ordinary tab. Settings says so explicitly, naming the address it is on,
rather than leaving an install button mysteriously absent.

Over Tailscale the tidy fix is `tailscale serve`, which fronts the app with a real
certificate for your machine's tailnet name:

```bash
tailscale serve --bg 5056        # then open https://<machine>.<tailnet>.ts.net
```

That needs HTTPS certificates enabled for the tailnet (admin console → DNS → HTTPS
Certificates).

What being installed actually buys you:

- The app shell — HTML, JS, CSS, fonts, icons — is precached, so it opens instantly
  and renders even with the server down.
- **Controller state is never cached.** Every `/api` request is network-only. A
  cached response could show a zone as idle while it is watering, and the whole point
  of that screen is to be trusted. Offline, the app says it cannot reach the server
  rather than showing stale state as though it were live.
- Zone photos are cached and revalidated in the background.
- A new build announces itself with a banner instead of reloading underneath you,
  which would otherwise discard whatever you were in the middle of editing.

### Developing

Two terminals:

```bash
cd src/RainBird.Server && RainBird__UseSimulator=true dotnet run --urls http://127.0.0.1:5056
cd web && npm run dev          # http://localhost:5273
```

The Vite dev server proxies `/api`, `/media` and `/hubs` to the backend, so
development is same-origin exactly like production. The service worker is disabled in
dev — otherwise every change would be served from a cache you had to clear by hand.

App icons are generated from one source SVG and committed, so a normal build never
regenerates them. After editing `web/public/icon.svg`:

```bash
cd web && npm run icons
```

### Tests

```bash
cd src && dotnet test
```

185 tests. The protocol suite covers the crypto (including both integrity-hash
conventions), the SIP codec against frames captured from real hardware, the universal
transport against known-good request templates, NAK handling,
capability probing, schedule round-trips and the full HTTP path. The server suite
covers the scheduling logic: cycle-and-soak interleaving, seasonal adjust, frequency
and start-time selection.

---

## Layout

```
docs/                      protocol reference, feature scope, design spec
src/
  RainBird.Protocol/       the protocol: crypto, SIP codec, universal CDT, typed client
  RainBird.Protocol.Tests/ xUnit
  RainBird.Simulator/      a virtual controller speaking the real wire protocol
  RainBird.Server/         ASP.NET Core 10 — API, SignalR, EF Core/SQLite, plan engine
  RainBird.Server.Tests/   xUnit — the scheduling logic
web/                       React 19 + TypeScript + MobX + Framer Motion
Dockerfile                 multi-arch, self-contained, chiseled non-root image
compose.yaml               the same image with volumes for state and photos
```

`RainBird.Protocol` has no dependency on ASP.NET or the database and can be lifted
out and reused. `RainBird.Simulator` exists so the whole stack is verifiable without
a sprinkler controller on the desk — which is also what makes the test suite
meaningful.

---

## How it talks to the controller

Summarised from [`docs/rainbird-protocol.md`](docs/rainbird-protocol.md):

- **Transport** — JSON-RPC 2.0 in an encrypted body, `POST http://<ip>/stick`, or
  `https://…` on newer firmware, whose self-signed certificate is pinned against the
  the hardware itself presents
- **Encryption** — AES-256-CBC, key = SHA-256(password), zero padding, framed as
  `SHA256(plaintext) || IV || ciphertext`
- **Commands** — a `tunnelSip` RPC wrapping single-byte SIP commands; 46 of them,
  against the 23 in the resource file public libraries were derived from
- **Schedules** — page-structured. Program info at page `15+n`, start times at
  `95+n`, and run times at `128 + ⌊(station−1)/2⌋`, two stations per page with one
  16-bit run time per program

Findings worth calling out, because they will bite any other implementation:

1. **The integrity hash is asymmetric.** The app hashes the *unpadded* plaintext when
   sending and the *padded* plaintext when verifying. The two coincide only when a
   payload is exactly block-aligned, which is why it is easy to miss.
2. **Disabling a zone is not a flag.** It is zeroed run times across every program.
3. **Station masks are little-endian.** Byte 0 covers stations 1-8. Read the other
   way, a ten-zone controller decodes as stations 17, 18 and 25-32.
4. **`ManuallyRunStation` takes a 16-bit station**, giving a four-byte command. The
   three-byte form is rejected outright.
5. **Current firmware drops the legacy schedule pages entirely.** See
   [`docs/rainbird-universal-protocol.md`](docs/rainbird-universal-protocol.md) for
   the interface that replaces them.

---

## Security notes

- **Sign-in is required for everything.** Every API route, the live-update hub and the
  zone photos need an account; only the health check and the sign-in routes are open.
  Passwords are hashed with PBKDF2 through ASP.NET Core's own hasher, and each token
  carries a security stamp, so changing a password or removing an account cuts off
  existing sessions immediately rather than whenever the token would have expired.
- **All accounts are equal.** Anyone signed in can water, schedule, and add or remove
  accounts. There are no roles — only give an account to someone you would hand the
  controller to. You cannot delete the account you are signed in with, and the last
  account cannot be deleted.
- **There is no TLS here.** Over plain HTTP a password and its token cross the network
  in the clear, so the sign-in gate protects against the neighbour on your wifi, not
  against someone watching the wire. Put a reverse proxy in front of it, or reach it
  over Tailscale, before it leaves a network you trust. It logs a warning at startup
  whenever it is listening beyond this machine.
- Binds to `0.0.0.0:5056`, so it is reachable from the whole local network and from a
  Tailscale tailnet. It logs a warning at startup saying so. To keep it on this
  machine only, set `Urls` in `appsettings.json` to `http://127.0.0.1:5056`; to reach
  it over Tailscale without also serving the LAN, bind just those two addresses:

  ```jsonc
  "Urls": "http://100.x.y.z:5056;http://127.0.0.1:5056"   // your tailnet IP
  ```
- Controller passwords are encrypted at rest with ASP.NET Core Data Protection, keyed
  to the `keys` folder in the data directory. Losing it means re-adding your
  controllers. The token signing key lives beside it; deleting `jwt-signing.key`
  signs everybody out at once, which is the way to do that deliberately. `vapid.json`
  is the notification keypair — deleting it silently stops every subscribed device
  receiving anything until each one turns notifications on again.
- **Notifications need HTTPS**, like installing does. Browsers do not expose push on
  a plain `http://` origin at all, so the setting says so rather than offering a
  switch that cannot work.
- Rain Bird's cloud relay is deliberately not implemented. The protocol supports it;
  this app stays on your network.

## Status

Verified against a physical **ESP-ME3** (model `0009`, protocol 2.12, firmware 3.11)
as well as the simulator. On that hardware the app probes capabilities, reads live
state, controls zones, and runs a watering plan end to end — opening each zone in
turn and closing it on schedule.

That controller exposes neither the legacy schedule pages nor a way to switch itself
off, which is what makes the plan engine the only route to scheduling on it. Its own
run times can still be cleared through the universal transport, so there is one
schedule rather than two.

Other model families are handled by capability probing rather than assumption, but
have not been exercised: the ESP-TM2 packs run times differently, and the
non-program-based models (RZXe, ST8x) schedule per zone.
