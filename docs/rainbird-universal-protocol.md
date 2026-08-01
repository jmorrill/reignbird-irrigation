# Rain Bird Universal Message Transport & Controller Data Table

The modern configuration interface, carried inside SIP command `0C`.

Current firmware drops the legacy schedule pages entirely. A physical **ESP-ME3,
model `0009`, protocol 2.12, firmware 3.11** rejects SIP `20` and `21` with
*command not supported* — so on that hardware this is the **only** way to read or
write the controller's schedule.

Verified against that controller. Companion to [`rainbird-protocol.md`](rainbird-protocol.md).

---

## 1. Why this matters

| | Legacy pages (SIP `20`/`21`) | Universal (SIP `0C`) |
|---|---|---|
| ESP-ME3 protocol 2.12 | **not supported** | supported |
| Granularity | fixed page layouts | any table entry, any index range |
| Run times | packed 2 stations/page, 16-bit | 32-bit seconds, per program × station |
| Cycle & soak | not exposed | data IDs 10 and 11 |

Practically: without this, an app cannot stop a modern controller from watering on
its own, because it cannot zero the run times.

---

## 2. Frame layout

A universal message is a SIP `0C` command whose payload is:

```
0C │ routing header (20 bytes) │ length (2, LE) │ type │ handler │ payload…
```

- **Routing header** — `20 00 01 00 | 08 05 00 00 00 00 | 0C 00 00 00 00 00 | 05 00 00 00`

  The two six-byte fields in the middle swap places in the reply, which is how they
  were identified as source and destination. The destination's first byte selects the
  manager:

  | Byte | Manager |
  |---|---|
  | `0C` | Controller Data Table |
  | `09` | Irrigation |
  | `04` | Field devices (two-wire) |
  | `13` | GUI / screen |
  | `0D` | Firmware |

  The remaining bytes are constant across every request.

- **Length** — little-endian, counts from the `type` byte to the end.
- **Type** — see below. **Handler** — always `0B` for Controller Data Table traffic.

Responses come back as SIP `8C` with the same shape and the addresses swapped.

### Payload types

| Value | Meaning |
|---|---|
| `01` / `02` | data set request / response |
| `03` / `04` | data get request / response |
| `05` / `06` | **bunch set** request / response |
| `07` / `08` | **bunch get** request / response |
| `09` / `0A` | data info get |
| `0B` … `0E` | supported-data-ID queries |

The bunch forms are what controllers accept in practice, and what this project
implements.

---

## 3. Bunch get

```
07 0B │ blockCount │ block × blockCount
```

Each block selects a rectangular slice of one table entry:

```
dataId (2, LE) │ rank │ (indexStart (2, LE), indexEnd (2, LE)) × rank
```

`rank` is the entry's number of dimensions. **Rank 0 is legal** and means a scalar
with no index at all — encoded with no range bytes, not with a one-element range.

The response repeats the block header, then adds the value width and the values:

```
08 0B │ blockCount │ dataId │ rank │ ranges… │ valueWidth │ value × N
```

Values are little-endian, `valueWidth` bytes each, in row-major order over the
ranges. `N` is the product of the range sizes. The controller chooses the width —
run times come back as 4 bytes, seasonal adjust as 2, sensor bypass as 1.

## 4. Bunch set

```
05 0B │ 01 │ dataId │ rank │ ranges… │ valueWidth │ value × N
```

The response is:

```
06 0B │ status
```

`status` of `00` means every entry was written. Anything else is followed by a count
and a list of per-entry failures.

---

## 5. Controller Data Table entries

The entries that matter for scheduling — see `CdtDataId` in
`RainBird.Protocol.Universal` for those this project names:

| ID | Name | Rank | Units |
|---|---|---|---|
| 10 | Irrigation cycle time | 1 (station) | seconds |
| 11 | Irrigation soak time | 1 (station) | seconds |
| 12 | Inter-station delay | 0 | seconds |
| 13 | Global sensor bypass | 0 | flag |
| 15 | Program cycle advanced days | 1 | days |
| 16 | Program cycle custom days | 2 | day flags |
| 17 | Water cycle: cycle count | 1 | |
| 18 | Water cycle: cycle days | 1 | days |
| 19 | Water cycle: type | 1 | enum |
| 20 | Rain delay | 0 | days |
| **21** | **Run times** | **2 (program × station)** | **seconds** |
| 22 | Seasonal adjust by month | | percent |
| 24 | Seasonal adjust by program | 1 (program) | percent |
| **29** | **Start times** | **3 (program × ? × slot)** | minutes from midnight |
| 30 | Station priority | | |
| 32 | Solenoids max | | |
| 33 | Stations max | | |
| 34 / 35 | Water restriction start / end | | |
| 37 | Flow monitor enabled | | flag |
| 61 | Station flow | 1 (station) | |
| 62 | Stations learned | 1 (station) | |

Index ranges are **0-based**: program 0 is the faceplate's program A, station index 0
is station 1.

---

## 6. Worked examples

Known-good frames, reproduced byte for byte by this project's encoder (see
`UniversalProtocolTests`).

**Read program A's run times** (ME3 addresses 22 stations, so `0..21`):

```
0C 200001000805000000000C000000000005000000 0E00 070B 01 1500 02 0000 0000 0000 1500
                                             │    │    │  │    │  └─ station 0..21
                                             │    │    │  │    └──── rank 2
                                             │    │    │  └───────── data ID 21
                                             │    │    └──────────── one block
                                             │    └───────────────── bunch get
                                             └────────────────────── payload length 14
```

**Response captured from hardware** — all zeros, meaning nothing waters automatically:

```
8C 200000000C000000000008050000000005000000 6700 080B 01 1500 02 0000 0000 0000 1500 04 00000000 × 22
                                                                                      └─ 4-byte values
```

**Read start times** — four programs, six slots each:

```
… 1200 070B 01 1D00 03 0000 0300 0000 0000 0000 0500
```

**Read cycle and soak together** — two blocks in one round trip:

```
… 1100 070B 02 0A00 01 0000 1500 0B00 01 0000 1500
```

**Write** seasonal adjust for program A, and the acknowledgement:

```
→ … 0A00 050B 01 1800 01 0000 0000 02 6400
← 8C … 0300 060B 00
```

---

## 7. Notes for an implementation

- **Zeroing run times is the disarm switch.** With every program's run times at zero,
  the controller's own programs do nothing, while manual station commands (SIP `39`,
  `4B`) still work — which is exactly what a software scheduler needs.
- **Ask the controller for the value width**; do not assume it. It varies per entry.
- **Rank 0 entries** must be encoded with no range bytes at all.
- **The station dimension is the model's maximum, not its fitted count.** An ESP-ME3
  reports ten fitted stations via SIP `03` but the run-time table is addressed
  `0..21`. Requesting the fitted count works, but asking for the full range is what
  the hardware expects and is the safer choice.
- Requests are still subject to the SIP tunnel's one-at-a-time rule.

## 8. Front panel

There is also a front-panel session protocol — `0114` start, `0314` end, `0714`
beacon — which locks the faceplate while changes are being
made. Not implemented here; noted because it is the mechanism for
preventing someone changing the schedule at the controller while software owns it.
