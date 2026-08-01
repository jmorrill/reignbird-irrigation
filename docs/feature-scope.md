# Feature Scope — What Rain Bird Hardware Can Support

What this app can offer, and where each capability comes from. The protocol itself is
documented in [`rainbird-protocol.md`](rainbird-protocol.md).

The point of this document is to be honest about the boundary. A consumer irrigation
app implies a lot of features, and on this hardware some are free, some have to be
built, and a few are impossible. Knowing which is which up front is what stops the
interface promising something it cannot deliver.

Legend:

- **Native** — a SIP/RPC command does it directly.
- **Derived** — our backend provides it by combining device data with its own state.
- **Backend** — entirely our own feature; the controller is not involved.
- **No** — the hardware cannot support it.

---

## 1. Capability map

| Feature | Support | How |
|---|---|---|
| Quick Run a zone for N minutes | **Native** | SIP `39` ManuallyRunStation |
| Run a whole schedule/program now | **Native** | SIP `38` ManuallyRunProgram |
| Test all zones | **Native** | SIP `3A` TestStations |
| Stop watering | **Native** | SIP `40` StopIrrigation |
| Skip to next zone | **Native** | SIP `42` AdvanceStation |
| Live "now watering" with countdown | **Native** | SIP `4C` — active station + remaining runtime |
| Upcoming runs queue | **Native** | SIP `3B` CurrentQueue |
| Rain delay / water delay | **Native** | SIP `36`/`37` |
| Rain sensor status | **Native** | SIP `3E` |
| Seasonal adjust (global) | **Native** | SIP `30`/`31` water budget, per program |
| Seasonal adjust (per zone) | **Native** | SIP `32`/`33` |
| Enable/disable the controller | **Native** | SIP `49` |
| Zone list & count | **Native** | SIP `03` available-stations bitmask |
| Disable individual zones | **Native** | schedule station-disable page (SIP `21`) |
| Schedule editing (days, start times, run times) | **Native** | SIP `20`/`21` page protocol |
| Frequency: custom days / every-N-days / odd / even | **Native** | program info page, frequency byte |
| Multiple programs (A/B/C/D) | **Native** | model-dependent, 3–40 programs |
| Controller clock / timezone | **Native** | SIP `10`–`13`, `2B`, `FC` |
| Soil type per program | **Native** | RPC `setSoilType` / `setProgramInfo` |
| Flow rate & flow monitoring | **Native\*** | SIP `60`–`65` — only on flow-capable models; feature-detect with SIP `04` |
| Irrigation statistics | **Native** | SIP `4D` |
| Firmware update | **Native** | RPC `requestFwUpdate` |
| WiFi setup / AP provisioning | **Native** | RPC `getWifiParams` / `setWifiParams` / `getScanResults` |
| **Watering history / calendar** | **Derived** | every observed run is logged to our own DB and rendered as a calendar |
| **Water usage (gallons)** | **Derived** | runtime × per-zone nozzle flow rate (user-entered, or measured on flow-capable models) |
| **Water saved** | **Derived** | baseline schedule minus what actually ran after weather skips |
| **Weather forecast strip** | **Backend** | our server calls a weather API by lat/long |
| **Rain skip / freeze skip / wind skip** | **Backend** | our server evaluates the forecast, then issues a rain delay or suppresses a program |
| **Zone photos** | **Backend** | stored by us, keyed to station number |
| **Yard map** | **Backend** | out of scope for v1 |
| **Zone detail: plant type, soil, sun, slope, nozzle** | **Backend** | our metadata; soil type also pushed to the controller |
| **Notifications / event feed** | **Derived** | generated from our polling loop |
| **Multi-controller support** | **Derived** | our DB holds many controllers |
| **Software-driven schedules** | **Backend** | our plan engine opens and closes valves itself; see the plan engine in `RainBird.Server` |
| Soil moisture sensors | **No** | no such hardware on Rain Bird residential controllers |
| Valve monitoring / wiring diagnostics | **Partial** | SIP `3D` CurrentStationError gives a fault flag, not per-valve current readings |
| Smart lighting control | **No** | not a Rain Bird product |

**The conclusion that shaped the architecture:** almost everything a modern irrigation
app offers is reachable on this hardware. What comparable products do in a cloud —
weather, history, usage, scheduling intelligence — this app does in its own ASP.NET
backend, which for a local-first design is strictly better: it works with no account
and no internet. The only genuinely unreachable features are those that depend on
sensors Rain Bird residential controllers do not have.

---

## 2. Deliberate design choices

- **Local-first, no account.** The app talks to the controller on the LAN and keeps
  everything in a local database. Nothing is required to be online for core function.
- **Honest usage numbers.** Water usage is an estimate derived from the nozzle flow
  rate the user configured, and it is labelled as an estimate. There is no flow meter
  on most of this hardware, and a confident-looking number that is really a guess is
  worse than an honest one.
- **Show the protocol.** A developer-facing panel exposing raw SIP exchanges is
  genuinely useful when the wire format has no published specification, and costs
  almost nothing.
- **Desktop-capable.** Most irrigation apps are phone-only. A responsive web app that
  is also good on a wide screen is a real advantage for setting up schedules.
- **The accent colour does the work.** Everything else stays neutral, and data gets
  colour only when the colour carries meaning.
