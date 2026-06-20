# ReUnion vs consistency — ce face fiecare

## Pe scurt

| Componentă | Când rulează | Ce face | Legat de kick-ul tău? |
|------------|--------------|---------|------------------------|
| **ReUnion** | La `connect` (înainte de signon) | Validează ticket RevEmu, atribuie SteamID | **NU** — dacă apare în `amx_last`, auth-ul a mers |
| **ReHLDS engine** | Signon (`resource list` → `clc_fileconsistency` → `spawn`) | Verifică fișiere (`mp_consistency`) | **DA** — `Bad file data` vine de aici |
| **ReAuthChecker** (opțional) | După connect, în timpul signon | Usermessage custom, anti-fake | Poate cere `clc_move`; nu trimite consistency |

**Concluzie:** `SV_ParseConsistencyResponse: sent bad file data` **nu e bug ReUnion**. Botul tău a trecut de ReUnion (SteamID în amx_last), apoi a fost dat afară la verificarea fișierelor din motorul HLDS.

---

## Ce face ReUnion (documentat din sursă)

ReUnion e plugin Metamod pentru **ReHLDS**. Înlocuiește DProto.

### Hook-uri (doar la connect)

Din `reunion/src/client_auth.cpp`:

1. `SV_CheckProtocol` — protocol 47/48
2. `SV_CheckKeyInfo` — citește `protinfo` (`\prot\3\raw\steam\cdkey\...`)
3. `SV_FinishCertificateCheck` — evită eroarea certificat steam
4. `Steam_NotifyClientConnect` — validează ticket-ul atașat la pachetul `connect`

Dacă ticket-ul e valid **RevEmu** sau **RevEmu2013**, ReUnion setează SteamID și lasă engine-ul să continue signon-ul normal.

### Ce trimite botul (GoldSrcProbe)

```
connect 48 <challenge> \prot\3\unique\-1\raw\steam\cdkey\<random> \qport\<port> \name\MsBoostProbe\...
+ ticket 194 bytes (RevEmu2013) sau 152 bytes (RevEmu classic)
```

Config în `config.json`:

```json
"AuthEmulator": "auto"     → RevEmu2013 (recomandat pe ReUnion modern)
"AuthEmulator": "revemu2013"
"AuthEmulator": "revemu"   → ticket classic
```

### ReUnion NU atinge

- `svc_resourcelist`
- `clc_fileconsistency`
- `mp_consistency`
- `SV_ParseConsistencyResponse`

---

## De ce apare în amx_last dar tot primești kick

Flux real:

```
1. getchallenge + connect + ticket     → ReUnion OK
2. S2C_CONNECTION                      → slot client deschis
3. signon: new → sendres → resourcelist
4. clc_fileconsistency                 → AICI eșuează la tine
5. spawn → intrare completă în joc     → nu se ajunge
```

amx_last loghează la **pasul 2–3** (connect parțial). Kick-ul la **pasul 4** e după ce ReUnion și-a terminat treaba.

Data „Disconnected înainte de Connected” în amx e artefact de logging/reconnect, nu că botul n-a intrat.

---

## reunion.cfg — setări relevante pentru bot

Pe serverul tău verifică:

```cfg
# Trebuie permis emulatorul pe care îl folosește botul:
AuthVersion = 3
SteamIdHashSalt = "minim_16_caractere_random"   # obligatoriu la AuthVersion 3+

# Nu respinge RevEmu2013:
# dp_rejmsg_revemu2013 — dacă apare reject la connect, problema E ReUnion, nu consistency

cid_RevEmu2013 = 1    # STEAM_ din HWID (default ok)
```

Dacă la connect ai fi avut problemă ReUnion, ai vedea:

- `Server reject: Sorry, RevEmu2013 clients are not allowed...`
- **nu** `Bad file data`

---

## Bad file data — cauze reale (ReHLDS)

Mesajul apare când `SV_ParseConsistencyResponse` nu poate citi răspunsul:

1. **Număr greșit de fișiere** în pachet vs `num_consistency`
2. **Index resursă invalid** (parser `resourcelist` desincronizat)
3. **COM_Munge greșit** (cheie = `spawncount` întreg, nu ultimul byte)
4. **Hash MD5 greșit** → de obicei `inconsistent file`, nu `bad file data`

### Ce trebuie pe bot

`config.json`:

```json
"CsGamePath": "C:\\cale\\la\\CS 1.6 IDENTIC cu serverul"
```

Fără fișiere corecte, hash-urile nu se potrivesc (după ce trece parserul).

### Debug

```bat
TEST.bat IP:PORT debug
```

Caută în consolă:

```
resource list: ... consistency=True, wire=N, marked=N
```

- `wire` = câte fișiere cere serverul
- `marked` = câte a înțeles parserul (trebuie **egale**)
- `output\consistency_debug.txt` = lista fișierelor trimise

---

## clc_move (`CMD_MAXBACKUP` / `badread on opcode clc_move`)

După ce consistency trece, serverul așteaptă **move-uri valide** ca un client CS real.

### Format corect (ReHLDS `SV_ParseMove`)

```
clc_move (0x02)
mlen (1 byte)          — lungime payload munged
checksum (1 byte)      — COM_BlockSequenceCRCByte(body, mlen, outgoing_sequence)
[body munged cu COM_Munge(body, mlen, outgoing_sequence)]
  packet_loss (1)
  numbackup (1)        — 0 pentru probe
  newcmds (1)          — 1 per pachet
  usercmd delta bits   — MSG_WriteUsercmd / delta.lst
```

**Greșit înainte:** `int32 command_number` + usercmd raw bytes → server citea `numcmds=104` din garbage → `CMD_MAXBACKUP hit`.

### Fișiere noi

- `ClcMoveBuilder.cs` — pachet complet
- `UsercmdDeltaWriter.cs` — delta `usercmd_t` din `valve/delta.lst`
- `ComBlockSequenceCrc.cs` — checksum move
- `UserCmd.cs` — structură usercmd

### Config

```json
"CsGamePath": "C:\\Games\\Counter Strike 1.6"
```

---

## Reliable channel overflow (alt bug, nu ReUnion)

Dacă vezi `MsBoostProbe overflowed` / `Reliable channel overflowed`:

- al doilea pachet reliable (de obicei **retransmitere spawn**) în timpul signon-ului
- spawn trimis înainte să termine burst-ul de la server

**Fix în cod:** un singur `spawn`, **fără retry reliable**, timing strict endres → spawn.

**Ghid complet (config, TEST.bat, host 57.x):** vezi [`SIGNON-STRICT-HOSTS.md`](SIGNON-STRICT-HOSTS.md)

---

## Checklist rapid

| Simptom | Cauză probabilă |
|---------|------------------|
| `Sorry, RevEmu...` la connect | reunion.cfg blochează emulatorul |
| Fără intrare în amx_last | connect respins / shield A2S |
| SteamID în amx_last + `Bad file data` | **consistency ReHLDS**, nu ReUnion |
| `inconsistent file` | CsGamePath / fișiere diferite de server |
| `overflowed` | timing signon (spawn prea devreme) |

---

## Test recomandat

```bat
cd goldsrc-probe
TEST.bat 179.61.132.147:27015 debug
```

Pe server cu `mp_consistency 1` și jucători, rulează pe IP-ul tău real și compară `wire` vs `marked` în log.
