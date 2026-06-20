# File consistency (`clc_fileconsistency`) — documentație tehnică

## Simptomul tău

```
SV_ParseConsistencyResponse: MsBoostProbe:82.77.237.180:27005 sent bad file data
Dropped MsBoostProbe from server
Reason: Bad file data
SV_ReadClientMessage: badread on MsBoostProbe, opcode clc_fileconsistency
```

**Nu e ReUnion.** Dacă apare în `amx_last` cu SteamID, auth-ul a reușit. Kick-ul vine din **ReHLDS** la parsarea răspunsului de consistență în signon.

| Eroare server | Cauză |
|---------------|-------|
| `Bad file data` | Structură greșită: număr intrări ≠ `num_consistency`, index invalid, resursă fără `RES_CHECKFILE`, `msg_badread` |
| `inconsistent file` / mesaj custom | Hash sau bounds **nu** se potrivesc — altă clasă de eroare |
| `Invalid length` | `MSG_ReadShort` ≤ 0 sau payload prea scurt |

---

## Flux signon (ReHLDS)

```
svc_resourcerequest  → spawncount (folosit la COM_Munge)
svc_resourcelist     → lista resurse + biți consistență
clc_fileconsistency  → răspuns client (OBLIGATORIU dacă mp_consistency=1)
clc_stringcmd spawn  → intrare în joc
```

Când `mp_consistency 0` sau `num_consistency=0`, serverul scrie `MSG_WriteBits(0,1)` la lista de consistență și **nu** așteaptă răspuns obligatoriu.

---

## Format wire `svc_resourcelist` (ReHLDS `SV_SendResources_internal`)

Sursă: [rehlds/engine/sv_main.cpp](https://github.com/rehlds/ReHLDS/blob/master/rehlds/engine/sv_main.cpp)

Pentru fiecare resursă (× `num_resources`):

| Câmp | Biți | Note |
|------|------|------|
| `type` | 4 | `t_sound=0`, `t_skin=1`, `t_model=2`, `t_decal=3`, … |
| `szFileName` | 8×N | **BitString null-terminated** (`MSG_WriteBitString`) — NU prefix de lungime |
| `nIndex` | 12 | |
| `nDownloadSize` | 24 | |
| `ucFlags` | 3 | Doar `RES_WASMISSING \| RES_FATALIFMISSING` pe wire |
| MD5 | 128 | **Doar dacă** `ucFlags & RES_CUSTOM` pe server (nu e în cei 3 biți) |
| has_reserved | 1 | |
| `rguc_reserved` | 256 | Dacă has_reserved=1; poate fi munged (modele cu bounds) |

După resurse:

| Câmp | Biți |
|------|------|
| consistency_list_present | 1 |
| Pentru fiecare fișier de verificat: | |
| → has_entry | 1 (=1) |
| → delta_or_abs | 1 (1=delta 5bit, 0=index absolut 10bit) |
| → index/delta | 5 sau 10 |
| terminator | 1 (=0) |

---

## Format răspuns `clc_fileconsistency`

Sursă referință: [7244/goldsrc-netclient](https://github.com/7244/goldsrc-netclient) + ReHLDS `SV_ParseConsistencyResponse`

```
[byte 0x07][uint16 payload_len][bitstream munged cu COM_Munge(payload, spawncount)]
```

Bitstream (înainte de munge), pentru fiecare resursă cu `NeedConsistency`:

```
1                    // has entry
index                // 12 biți — index în lista resurselor
dacă rguc_reserved == 0 (toți 0):
  hash               // primii 32 biți din MD5 (uint32 LE)
altfel:
  cmins              // 12 octeți (3 float) — bounds model
  cmaxs              // 12 octeți
0                    // terminator
```

Server:

1. `COM_UnMunge(payload, len, g_psvs.spawncount)` — **spawncount întreg**, nu doar byte low
2. Verifică `length == g_psv.num_consistency`
3. Verifică `r->ucFlags & RES_CHECKFILE` pentru fiecare index
4. Compară hash sau bounds

---

## Bug-uri fixate în GoldSrcProbe

### 1. `ReadBitString` greșit (CRITIC)

**Înainte:** citea 8 biți ca „lungime”, apoi N octeți.  
**Corect (GoldSrc):** octeți 8-bit până la terminator `\0`.

Fără fix, tot parserul de resurse era desincronizat → `wire≠marked` sau `marked=0` pe servere cu `mp_consistency 1` → **Bad file data**.

### 2. MD5 pe wire pentru `RES_CUSTOM`

Serverul trimite 128 biți MD5 după flagii de 3 biți când resursa e custom (spray/logo), dar flagul `RES_CUSTOM` (bit 2) **nu** e în cei 3 biți.

Parserul folosește euristică (`t_decal` + path/`!`/`tempdecal.wad`) + brute-force pe decal-uri ambigue (≤10, 2^N încercări).

### 3. MD5 local la marcare `NeedConsistency`

Ca în goldsrc-netclient: `FileToMD5` din `CsGamePath` (`cstrike/`, `valve/`, `sound/` pentru wav).

### 4. `COM_Munge` cu `spawncount` complet

Din `svc_resourcerequest`, nu doar low byte.

### 5. Bounds

`rguc_reserved` de pe wire e munged; clientul trimite **unmunged** mins/maxs (ReHLDS compară după `COM_UnMunge` pe server).

---

## Config necesar

`config.json`:

```json
{
  "CsGamePath": "C:\\cale\\la\\CS 1.6 instalat",
  "BindPort": 27005,
  "AuthEmulator": "auto"
}
```

`CsGamePath` trebuie să fie instalarea CS (cu `cstrike/`, `valve/`) pentru hash-uri corecte. Hash greșit → de obicei **inconsistent file**, nu bad file data.

---

## Testare

```powershell
cd C:\Users\StefaK\Desktop\goldsrc-probe
TEST.bat IP:PORT debug
```

sau:

```powershell
dotnet run --project GoldSrcProbe -- --no-pause --join --host IP:PORT --hold 15 --debug-net
```

### Ce să verifici în log

```
[net] resource list: N entries, consistency=True, wire=X, marked=X
```

**`wire` trebuie să fie egal cu `marked`.** Dacă `consistency=True` dar `marked=0` → parser încă desincronizat (raportează).

```
[net] file consistency: required=True, wire/marked=X, mungeKey=Y
[net] consistency packet Zb
```

`output/consistency_debug.txt` — lista fișierelor trimise.

### Servere referință

| Server | mp_consistency | Așteptat |
|--------|----------------|----------|
| `179.61.132.147:27015` | off | `consistency=False`, JOIN OK |
| Serverul tău | on | `consistency=True`, `wire=marked=N`, fără kick |

---

## Proiecte de referință

| Proiect | Rol |
|---------|-----|
| [7244/goldsrc-netclient](https://github.com/7244/goldsrc-netclient) | Client C minimal: parse resourcelist, `FileToMD5`, build `clc_fileconsistency`, fragmente |
| [rehlds/ReHLDS](https://github.com/rehlds/ReHLDS) | `SV_SendResources_internal`, `SV_ParseConsistencyResponse`, `SV_SendConsistencyList` |
| GoldSrcProbe | Bot C# ReUnion + consistency + status probe |

---

## ReUnion vs consistency

Vezi și `REUNION.md`. ReUnion = ticket la connect. Consistency = engine HLDS după resource list. Ambele trebuie să treacă pentru JOIN complet pe servere cu `mp_consistency 1`.
