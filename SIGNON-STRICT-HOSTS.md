# Signon pe host-uri stricte (ex. 57.129.61.75)

## Simptom

```
FAIL — Disconnect: Reliable channel overflowed
```

Auth-ul trece (RevEmu OK, fastdl apare). Eșecul e la **signon** (endres → spawn), nu la ReUnion.

## Cauză (confirmată în debug)

Pe ReHLDS agresiv, **al doilea pachet reliable** trimis prea devreme (de obicei retransmitere `spawn`) umple canalul serverului.

Secvența tipică greșită:

```
out rel  … new
out rel  … endres
out rel  … spawn      ← OK
out rel  … spawn      ← DUPLICAT → overflow
```

Retransmiterea reliable în signon interpretează greșit ACK-urile serverului (`ackRel` alternă).

## Metodă concretă (implementată în cod)

### 1. Auth

Pentru servere care resping RevEmu2013:

```json
"AuthEmulator": "revemu"
```

### 2. Signon — o singură reliable odată

| Pas | Client | Așteptare |
|-----|--------|-----------|
| 1 | `new` | ACK |
| 2 | primește serverinfo + burst | ~800ms liniște (`IsSignonBurstSettled`) |
| 3 | `endres` | ACK + min **5s** de la endres |
| 4 | primește resource list | min **3s** de la listă |
| 5 | consistency (doar dacă cerută) | ACK sau skip instant |
| 6 | `spawn` | **un singur** pachet, fără retry |
| 7 | join complet (stare 3) | după ACK spawn + 1s |

**Fără retry reliable** — `RetryPendingReliable()` returnează null.

### 3. Config recomandat (`config.json`)

```json
{
  "AuthEmulator": "revemu",
  "ConnectTimeoutMs": 90000,
  "BindPort": 27006,
  "CsGamePath": "C:\\Games\\Counter Strike 1.6"
}
```

Join pe host mare (1000+ resurse): **45–90 secunde** e normal.

### 4. Retry la verify

`VerifyRunner` reîncearcă automat (max 3×) doar la overflow:

- pauză 6s
- port UDP: `BindPort`, `BindPort+3`, `BindPort+6` (evită ghost pe server)

### 5. TEST.bat

- IP fără port → se adaugă automat `:27015`
- Salvează `last-host.txt` mereu ca `ip:port`

## Comenzi

```bat
cd goldsrc-probe
TEST.bat 57.129.61.75:27015 verify
```

Debug rețea:

```bat
TEST.bat 57.129.61.75:27015 debug
```

## Ce NU e problema

| Mesaj | Realitate |
|-------|-----------|
| `STEAM validation rejected` la RevEmu2013 | Normal — folosește `revemu` |
| `Bad file data` | Consistency — nu trimite packet gol |
| Steam real | Nu e suportat; bot headless cu ReUnion |

## Verificare rapidă

JOIN OK arată:

```
JOIN OK | auth:RevEmu | players:...
```

sau server gol:

```
JOIN OK | auth:RevEmu | players:0 ...
Note: Joined OK — 0 jucatori in status
```
