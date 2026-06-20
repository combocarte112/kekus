# GoldSrcProbe — bot de verificare jucători CS 1.6

**Scop:** botul intră pe server ca client real (RevEmu/ReUnion), rulează `status` și extrage **steamid / ping / time** pentru fiecare jucător. Comparăm dacă sunt reali sau fake (boost A2S).

**Nu folosim A2S query ca sursă principală** — doar join + status HLDS.

## Quick start

```powershell
cd C:\Users\StefaK\Desktop\goldsrc-probe
dotnet run -- --no-pause --verify --host 179.61.132.147:27015
```

Output: `output/verify_players.json`

## Ce face `--verify`

1. `getchallenge` + connect (RevEmu2013)
2. Signon complet (new → sendres → spawn)
3. `sendents` + `status` (retry până la 4×)
4. Parse linii `# N "name" STEAM_0:x:x frags time ping loss adr`
5. Clasificare per jucător:
   - **OK** — steamid valid, fără semnale suspecte
   - **BOT** — uniqueid `BOT`
   - **FAK** — steamid zero/duplicat, ping identic la toți, adr lipsă, etc.
6. `likelyFakeServer: true` dacă ≥50% jucători sunt suspicious

## Exemplu consolă

```
[JOIN OK | players:8 real:3 bot:0 suspicious:5 *** LIKELY FAKE ***]
  [OK ] # 1 Player1              STEAM_0:1:12345        ping:  45 time:12:34
  [FAK] # 2 FakeName             STEAM_0:0:0            ping:   5 time:00:00 [zero_steamid, identical_ping_all]
```

## Comenzi utile

| Comandă | Scop |
|---------|------|
| `--verify --host IP:27015` | Verificare jucători (principal) |
| `--join --hold 30` | Test TAB — stă pe server |
| `--check-connect` | Doar getchallenge |
| `--mode a2s` | Opțional — query A2S vechi |

## config.json

| Key | Default | Rol |
|-----|---------|-----|
| `Mode` | `verify` | Mod implicit |
| `VerifyOutputFile` | `output/verify_players.json` | JSON verify |
| `PlayerName` | `MsBoostProbe` | Nume bot |
| `CsGamePath` | — | Folder CS 1.6 (Rechecker MD5) |
| `ConnectTimeoutMs` | 45000 | Timeout signon+status |

## JSON verify

```json
{
  "servers": [{
    "address": "1.2.3.4:27015",
    "joined": true,
    "signonState": 3,
    "summary": {
      "total": 8,
      "real": 3,
      "bots": 0,
      "suspicious": 5,
      "likelyFakeServer": true,
      "players": [{
        "name": "X",
        "steamId": "STEAM_0:0:0",
        "ping": 5,
        "time": "00:00",
        "trust": "Suspicious",
        "reasons": ["zero_steamid"]
      }]
    }
  }]
}
```

## Limitări

- Server **proxy A2S-only** → nu răspunde la getchallenge → skip
- Server **gol** → join OK, 0 jucători (normal)
- **RevEmu** pe serverul țintă obligatoriu
- ~60s / server pentru signon complet

## Fix critic signon

ACK-ul trebuie **16 bytes** (8 header + 8× svc_nop munged) — altfel serverul trimite doar 1/N fragmente BZ2.
