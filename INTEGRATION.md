# GoldSrcProbe — msboost integration

This document describes how to run the **GoldSrcProbe** worker on Windows and feed results into **CSTRIKE/msboost** (`public_html`).

## What it detects

| Signal | Meaning |
|--------|---------|
| `connectOk: true` | Probe bot completed signon and ran HLDS `status` |
| `statusPlayers` | Real player lines from `status` (humans + bots with `BOT` id) |
| `a2sPlayers` | Player count from A2S INFO |
| `playerCountMismatch` | `abs(a2s - status)` **only when connect succeeded** |
| `fakePlayerSuspect: true` | Mismatch ≥ 3 after verified connect |
| `connectReachability: a2s-shield` | Server answers A2S but blocks `getchallenge` (proxy) |

## Architecture

```text
[Task Scheduler / manual]
    GoldSrcProbe.exe  (--mode both)
        → output/status_probe.json

Option A — file copy to server:
    data/probe/status_probe.json
    php cron/cron_probe_import.php

Option B — HTTP POST:
    POST /api/probe-report.php?merge=1
    Header: X-Probe-Key: <PROBE_API_KEY>
    Body: status_probe.json content
```

## 1. Build & deploy probe (Windows)

```powershell
cd C:\Users\StefaK\Desktop\goldsrc-probe
dotnet publish GoldSrcProbe -c Release -r win-x64 --self-contained false
```

Copy to a worker folder:

- `GoldSrcProbe.exe`
- `config.json`
- `servers.txt`
- `output\` directory

### config.json (recommended)

```json
{
  "PlayerName": "MsBoostProbe",
  "ConnectTimeoutMs": 45000,
  "A2STimeoutMs": 4000,
  "DelayBetweenServersMs": 500,
  "Mode": "both",
  "AuthEmulator": "auto",
  "CsGamePath": "C:\\Path\\To\\Counter-Strike 1.6",
  "BindPort": 27005,
  "OutputFile": "output/status_probe.json"
}
```

### servers.txt

One `ip:port` per line. Export from msboost:

```powershell
powershell -File scripts\export-servers.ps1
```

Or use `backend/servers.php` output manually.

## 2. Scheduled run (Windows Task Scheduler)

Use `scripts\scheduled-probe.bat` or:

```bat
cd C:\path\to\goldsrc-probe
dotnet run --project GoldSrcProbe -c Release -- --no-pause --mode both
```

Then copy `output\status_probe.json` to the web server (`data/probe/`).

## 3. PHP configuration

In `includes/config.php` (already added):

```php
define('PROBE_JSON_FILE', __DIR__ . '/../data/probe/status_probe.json');
define('PROBE_API_KEY', 'your-long-random-key');  // set before using API
```

Create directory:

```bash
mkdir -p data/probe
chmod 755 data/probe
```

### Import into cache

```bash
php cron/cron_probe_import.php
# or explicit path:
php cron/cron_probe_import.php /path/to/status_probe.json
```

This merges `probe` object into each matching `ipport` in `servers_cache_new.json`.

### HTTP upload (optional)

```bash
curl -X POST "https://msboost.eu/api/probe-report.php?merge=1" \
  -H "X-Probe-Key: YOUR_KEY" \
  -H "Content-Type: application/json" \
  --data-binary @output/status_probe.json
```

## 4. UI

Server page (`pages/server.php`) shows a banner when probe data exists:

- **Verified count** from connect probe
- **Suspect** highlight when A2S count >> status count

## 5. Limitations

- **A2S-only proxies** — no `getchallenge` → `connectReachability: a2s-shield`, no mismatch score
- **Rate** — full connect takes ~15–45s per server; batch 100 servers ≈ 30–60 min
- **Rechecker** — set `CsGamePath` to real CS 1.6 for MD5 consistency; fallback hash used otherwise
- **ReUnion** — server must allow RevEmu/ReUnion (your test HLDS does)
- **One probe per IP** — ghost sessions: `kick MsBoostProbe` on server if stuck

## 6. JSON schema (v1.0)

See `README.md` for field list. Key additions:

- `signonState` — 0–3 signon step reached
- `statusHumanCount` / `statusBotCount`
- `a2sListGap` — A2S INFO count minus A2S PLAYER list length
- `fakePlayerSuspect` — boolean heuristic
- `probeVersion` on report root

## 7. Test checklist

```powershell
# Reachability
dotnet run -- --no-pause --check-connect --host YOUR_IP:27015

# Full join (TAB visible ~30s)
dotnet run -- --no-pause --join --host YOUR_IP:27015 --hold 30

# Batch JSON
dotnet run -- --no-pause --host YOUR_IP:27015 --mode both
```

Expected on direct ReUnion HLDS: `connectOk: true`, `signonState: 3`, `statusPlayers` ≥ 0.

## 8. Cron order (production)

1. `cron_query.php` — A2S poll (existing, every 5 min)
2. GoldSrcProbe worker — connect probe (every 30–60 min, subset of servers)
3. `cron_probe_import.php` — merge probe JSON into cache

Do **not** replace A2S cron with probe — probe is slower but verifies real slots.
