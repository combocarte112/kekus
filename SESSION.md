# Session notes — GoldSrcProbe (2026-06-20)

## Status: WORKING on test HLDS

Test server: `179.61.132.147:27015` (ReUnion, direct IP)

| Test | Result |
|------|--------|
| `--check-connect` | OK — getchallenge |
| `--join --hold 30` | JOIN OK — signon 3/3, TAB visible |
| `--mode both` | A2S OK + Connect OK, signon 3 |

## Critical fix (2026-06-17)

**ACK packets must be 16 bytes** (8 header + 8× svc_nop munged). ReHLDS `Netchan_Transmit` pads small packets during signon. Without this, server sends only fragment 1/N and stops.

## Also fixed (2026-06-20)

- UDP socket recreation after `ResetState()` (ProbeConnect + ProbeStatus on same client)
- `MAKE_FRAGID` byte order for outgoing fragments
- `svc_resourcelocation` (0x38) parsing in signon blob
- `sendres` deferred until after Privileges ACK
- `playerCountMismatch` only when `connectOk`
- PHP: `ProbeImport.php`, `cron_probe_import.php`, `api/probe-report.php`
- Server page probe banner

## Commands when back at PC

```powershell
cd C:\Users\StefaK\Desktop\goldsrc-probe

# Quick join test
dotnet run -- --no-pause --join --host 179.61.132.147:27015 --hold 30

# Batch probe → JSON
dotnet run -- --no-pause --mode both

# On server (after uploading JSON to data/probe/):
php cron/cron_probe_import.php
```

## Before production

1. Set `PROBE_API_KEY` in `includes/config.php`
2. Task Scheduler → `scripts\scheduled-probe.bat`
3. Copy/sync `output/status_probe.json` to server `data/probe/`
4. Run import after each probe batch
5. Optional: populate servers via `scripts\export-servers.ps1`

## Known limits

- A2S-only proxy servers: `a2s-shield`, no connect verification
- Full connect ~60s/server — run on subset or off-peak
- Empty server may show `connectOk` with 0 status lines (signon 3 = joined)
- Ghost probe: `kick MsBoostProbe` on HLDS console

## Files changed

**goldsrc-probe:** `NetChannel.cs`, `GoldSrcClient.cs`, `ProbeModels.cs`, `ProbeRunner.cs`, `ProtocolConstants.cs`, `README.md`, `INTEGRATION.md`, `servers.txt`, `scripts/*`

**public_html:** `includes/config.php`, `includes/ProbeImport.php`, `cron/cron_probe_import.php`, `api/probe-report.php`, `pages/server.php`
