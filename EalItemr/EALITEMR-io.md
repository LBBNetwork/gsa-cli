# Reading From and Writing To EALITEMR.DAT

> **Scope.** How the GSA Item Record File (`EALITEMR`, deployed to disk as
> `EALITEMR.DAT`) is structured on disk, and how it is read and written by
> (a) the GSA itself (4680 BASIC source under `gsasrc/`), and (b) the local C
> tools `ealitemr_edit` / `ealitemr_report`.
>
> **Sources.** IBM 4690 Store System OS V6R2 Programming Guide
> (G362-0574-00: Ch. 2 "Keyed files" p. 11, "Internal Processes of Keyed
> Files" and "IBM Folding Algorithm" p. 187–188, "Keyed File Control Record"
> p. 196, Ch. 6 "Keyed File Utility" p. 179); GSA Programming Guide
> (SC30-3631-01: Ch. 6 "Accessing Delayed Data Maintenance" p. 87,
> Appendix A "Item Record File – EALITEMR" p. 201, Tables 101–103); IBM 4680
> BASIC V1R2 Language Reference (SC30-3356-09, `READ FORM #` with `KEY` /
> `AUTOLOCK`, `WRITE … AUTOUNLOCK`); GSA source (376 `.BAS` + 444 `.J86`
> modules); and the verified tools in this workspace (real production
> `GSA EALITEMR.DAT`, 512,512 bytes).

---

## 1. What the file is

| Attribute | Value | Source |
|---|---|---|
| Logical filename | `EALITEMR` | GSA App A, p. 201 |
| Directory on store controller | `ADX_IDT1` (IDT = item master) | GSA App A |
| Organization | **Keyed** (hashed), no index | GSA App A; 4690 Ch. 2 |
| Key | Item number (item code), `ITEMCODE`, packed decimal, 11 bytes | Tables 101 |
| Record length | 50 bytes default; 63 (reserved area); 71 (Price Management) | Tables 101–103 |
| LAN distribution | **C&T — Compound** (controller + terminal copies), updated at every record update | GSA file summary, Table 29 |
| I/O session (channel) used by GSA | 59 | `EALDO1CC.BAS:908` |
| Who reads it | Sales (every item key at the keyboard), item-lookup utilities | GSA App A p. 201 |
| Who writes it | Data maintenance, DDM (host-driven), host processor, operator price-change | GSA App A p. 201 |
| Alternate logical name | `$ALITEMR` — prefix `$` selects the distributed (terminal-local) item record file | GSA App A p. 201 note |

GSA treats the file as a keyed logical-record file: application code never
computes block addresses itself for reads; it asks the OS for a record **by
key** and the OS hashes the key to a block. Writes are keyed upserts by the
same mechanism. Only the DDM "swap" path and the rebuild utility touch the
file at the physical level (whole-file copy / rename).

---

## 2. Physical layout (what `EALITEMR.DAT` actually contains)

### 2.1 Block structure

A keyed file is a sequence of 512-byte blocks (4690 Ch. 2: "A block is a
512-byte sector"). Layout, verified byte-for-byte on the production file:

```
+----+----+----+----+----+----+----+----+
| blk 0 | blk 1 | blk 2 | ...  | blk N |     N+1 = total blocks
+----+----+----+----+----+----+----+----+
  control   data blocks: each = 4-byte header + 10 × 50-byte records
```

- **Block 0 — keyed file control record** (4690 guide, "Keyed File Control
  Record" p. 196). Fields verified in the real file (little-endian):

| Offset (0-based) | Type | Real file value | Meaning |
|---|---|---|---|
| +42 | u32le | 1001 (`0F 07 00 00`) | total number of blocks, `NBLK` |
| +46 | u16le | 50 | record size in bytes, `RS` |
| +48 | u32le | 999 | randomizing divisor, `DIV` |
| +54 | u16le | 11 | key length in bytes, `KLEN` |
| +56 | u32le | 4 | chain length threshold, `THR` |

  Cross-check with the GSA BASIC side: `EALDO1CC.BAS:911` reads block 1 of
  the file opened `DIRECT RECL 512` and extracts (1-based string positions)
  char **47** → `IR.RECL` (record length = the value at byte 46), char
  **449** → `IR.USER.LEN` (added user area), char **451** → `IR.APPL.LEN`
  (application space). So the control block also carries the record-length
  and extension-area sizes that let GSA support the 50/63/71-byte variants.

- **Blocks 1..N — data blocks.** Each starts with a 4-byte chain header
  (fwd u16le @ +0, bwd u16le @ +2, `0` = end), followed by 10 slots of
  50-byte records each (10 × 50 = 496 + 4 header + 12 slack = 512).
  File size is exactly `(NBLK × 512)` bytes; the real file is
  1001 × 512 = 512,512 bytes.

### 2.2 Record slot rules

- A slot is **empty iff its key (first 11 bytes) is all zeros** (GSA keyed
  file convention; this is how `del` zeroes a record and how empty slots are
  detected).
- A record is its **key + 39-byte data area** (50-byte record); the key is
  physically the first 11 bytes of the stored record — GSA source confirms:
  `EALIRWR1.J86` builds the record as `IR.ITEMCODE$ + …` before writing, and
  `EALIRRD1.J86` reads a single record string `IR.REC$` and unpacks it.
- Records whose folded key does not fall in an occupied home slot are stored
  in **overflow blocks** reachable via the block chain headers.

### 2.3 Addressing: IBM folding / randomization

Per the 4690 guide ("IBM Folding Algorithm", p. 187–188):

1. **Fold** — XOR the key bytes, low-order byte → low accumulator,
   alternating, scanning the key right-to-left into two accumulator bytes.
2. **Randomize** — divide the two-byte folded key (MSB/LSB) by the
   randomizing divisor (999 here). The remainder is the **relative block
   number**; a remainder of **zero means block number = the divisor
   (999)**.

```
home_block(key) = F(key) % 999        (F = folded 16-bit key)
                 999 if F(key) % 999 == 0
```

This is exactly what the C tool implements (`hash_blk()`) and what was
verified: **all 150 records of the real file sit exactly in their computed
home block** — the file currently has zero overflow chains.

---

## 3. Key format (`ITEMCODE`, 11 bytes)

From GSA App A ("Item Record File – EALITEMR", p. 201) — the type nibble is
**part of the key**, in the first 4 bits; everything else is packed decimal,
right-justified, left-padded with hex `0`:

| Hex type nibble | Item type |
|---|---|
| `X'0'` | Other items (EAN, UPC, user-specified — key with ENTER) |
| `X'1'` | SKU items |
| `X'2'` | Department-class-stock (DCS) items |
| `X'3'` | Nonmerchandise items |

- Item numbers: max **21 digits** for all types. When transmitted **from the
  host**, the item number carries the type identifier as its first
  character, which can make 22 characters (GSA strips/holds the type in the
  nibble).
- DCS item keys embed separators inside the packed key: `X'B'` separates
  department from class, `X'D'` separates class from stock; department
  portion max 6 numeric digits.
- Note: non-BCD raw keys exist in the real production file (e.g. block 647,
  key text `SAMPLE DCS# 9/9/9`); the tools therefore also accept raw-hex
  11-byte keys, right-justified.

**Worked example.** Item `1234`, type "other" (`X'0'`):

```
nibbles:  0 0 0 0 0 0 1 2 3 4
bytes:    00 00 00 00 00 00 00 00 00 00 12 34   (11 bytes, PD)
```

## 4. Record layout (50-byte base record)

Table 101 of the GSA Programming Guide, p. 201–203 (offsets are 0-based from
the record start; the 11-byte key occupies offsets 0–10 and is not repeated
below):

| Offset | Field | Type | Len | Meaning / bits |
|---|---|---|---|---|
| 11 | `INDICAT0` | INT | 1 | bit7 `X'80'` movement kept · bit6 `X'40'` quantity required · bit5 `X'20'` price required · bit4 `X'10'` **not authorized for sale** · bit3 `X'08'` not authorized during store-restricted hours · bit2 `X'04'` item not returnable · bit1 `X'02'` item not priced (giveaway) · bit0 `X'01'` reserved: weight/price required (sold by weight) |
| 12 | `INDICAT1` | INT | 1 | bit7 `X'80'` taxtable 1 applies · bit6 `X'40'` taxtable 2 · bit5 `X'20'` taxtable 3 · bit4 `X'10'` taxtable 4 · bit3 `X'08'` stock number required · bits 2–0 reserved |
| 13 | `INDICAT2$` | PD | 1 | **digit 1** = validation class: 0 none · 1 dept · 2+ D/C desired · … · 8 D/C/S required (see source comments in `EALITEMR.J86`) **digit 2** = pricing method: 0 unit/split-package · 1 base-plus-1 · 2 group-threshold · 3 reduced |
| 14 | `DEPARTME$` | PD | 3 | Department number (drives department totals) |
| 17 | `SALEQUAN$` | PD | 1 | Item count in the deal, range 0–99 |
| 18 | `SALEPRIC$` | PD | 5 | Price, format depends on pricing method (see below) |
| 23 | `MPGROUP$` | PD | 1 | Multipricing group number, 0–99 |
| 24 | `ITEMNAME$` | ASCII | 18 | Item description |
| 42 | `USER$` | ASCII | 8 | Reserved for user-defined data |

**`SALEPRIC$` formats** (PD, 10 digits = `PPPPPPPPPP`):

| Method | Format | Meaning |
|---|---|---|
| 0 | `000 ppppppp` | unit price if `SALEQUAN$` is 0/1, else package price |
| 1 | `000 ppppppp` | package price |
| 2 | `ppppp sssss` | package price ‖ single-unit price |
| 3 | `uuuuu rrrrr` | unit price ‖ reduced price |

**Extended record variants** (record length comes from the control block,
not hard-coded):

| Len | Adds | Source |
|---|---|---|
| 50 | (base above) | Table 101 |
| 63 | `RESERVE$` ASCII 13 @ 50 — future IBM enhancements | Table 102 |
| 71 | `RESERVE$` ASCII 8 @ 63 — Price Management Feature | Table 103 |

`EALITEMR.J86` (the shared include) declares all of these as globals
(`IR.*`) plus work strings (`IR.REC$`, `IR.VAR.USER$`, `IR.WORK$`) and the
lengths `IR.RECL`, `IR.USER.LEN`, `IR.APPL.LEN`, read for the first time
from the control block (Section 5.1).

---

## 5. How GSA **reads** the file (from source)

### 5.1 Open sequence — `EALDO1CC.BAS` ("Change Item Price", Daily Ops menu)

The standard open (lines 908–915) has two phases:

```basic
! read file parameters from the keyed-file control block
OPEN "EALITEMR" DIRECT RECL 512 AS 59            ! block-level access
READ FORM "C512"; #59, 1; IR.REC$                ! block 1 (= control block)
CLOSE 59
CALL EXTRACT2(IR.REC$,47,IR.RECL)                ! record length (50/63/71)
CALL EXTRACT2(IR.REC$,449,IR.USER.LEN)           ! added user area
CALL EXTRACT2(IR.REC$,451,IR.APPL.LEN)           ! app space
IR.REC$ = ""
OPEN "EALITEMR" KEYED RECL IR.RECL AS 59         ! re-open for keyed access
```

So GSA first treats the file as a **direct 512-byte file** purely to learn
its parameters, then reopens it **KEYED** with the discovered record length
on I/O session (channel) **59**. Other modules (e.g. `EALDB5MC.BAS`) open it
`KEYED RECL` directly.

### 5.2 Keyed read — `EALIRRD1.J86` ("READ AN ITEM RECORD")

```basic
IR.RECL$ = "C" + STR$(IR.RECL)                    ! format "C50" | "C63" | "C71"
READ FORM IR.RECL$; #59 KEY IR.ITEMCODE$; IR.REC$ ! single record into one string
```

Then field extraction (`GETN1` = numeric 1-byte grab, `MID$` = character
grab, 1-based positions matching Section 4):

```basic
IR.INDICAT0  = GETN1(IR.REC$,11)   ! flag byte 0
IR.INDICAT1  = GETN1(IR.REC$,12)   ! flag byte 1
IR.INDICAT2$ = MID$(IR.REC$,14,1)  ! validation + pricing-method digit
IR.DEPARTME$ = MID$(IR.REC$,15,3)  ! department
IR.SALEQUAN$ = MID$(IR.REC$,18,1)  ! deal quantity
IR.SALEPRIC$ = MID$(IR.REC$,19,5)  ! sale price
IR.MPGROUP$  = MID$(IR.REC$,24,1)  ! multipricing group
IR.ITEMNAME$ = MID$(IR.REC$,25,18) ! description
IR.USER$     = MID$(IR.REC$,43,8)  ! user area
IF IR.APPL.LEN > 50 THEN IR.RESERVED$ = MID$(IR.REC$,51,IR.APPL.LEN-50)
IF IR.USER.LEN > 0 THEN IR.VAR.USER$ = RIGHT$(IR.REC$,IR.USER.LEN)
IR.REC$ = ""
```

Error semantics (4680 BASIC reference, `READ FORM #`): a not-found /
error that the `ON ERROR` routine handles leaves the destination variables
**nulled / zeroed after `RESUME`** — this is how "item not in file" is
detected by the calling routine.

Usage: hundreds of modules consume item data this way — the item-movement
modules (`EALTSIMC` "Obtain Item Movement", `EALTSIMS`), layaway routines
(`EALTSC1C`), DDM modules (`EALDM*`, `EALAD1*`), host-service modules
(`EALHS1*`)… all `%INCLUDE EALITEMR.J86` and then `EALIRRD1` (or the
`AUTOLOCK` variant below).

### 5.3 Read-for-update — `EALIRRD2.J86`

Identical, except:

```basic
READ FORM IR.RECL$; #59 AUTOLOCK KEY IR.ITEMCODE$; IR.REC$
```

**`AUTOLOCK`** (4680 BASIC reference): the record is **locked before the
read**, and the application waits for it to become available. Valid for
random, direct, and keyed files. The lock is released when the record is
written with `AUTOUNLOCK` (Section 6.1) or at file close/error — this is
GSA's read-modify-write guard for price/flag changes (`EALDO1CC`,
`EALDB5AC`).

---

## 6. How GSA **writes** the file (from source)

Keyed-file `WRITE` is an **upsert by key**: the OS folds the key, locates the
slot (or the overflow chain), and places the record there. Application code
never computes block addresses for writes.

### 6.1 Write — `EALIRWR1.J86` ("WRITE AN ITEM RECORD")

```basic
IR.REC$ = IR.ITEMCODE$ + "   "                     ! key + placeholder bytes 11-13
CALL SUBSTR(IR.REC$,11,CHR$(IR.INDICAT0),0,1)     ! flag byte 0
CALL SUBSTR(IR.REC$,12,CHR$(IR.INDICAT1),0,1)     ! flag byte 1
CALL SUBSTR(IR.REC$,13,IR.INDICAT2$,0,1)          ! validation/pricing digit
IR.REC$ = IR.REC$ + IR.DEPARTME$                   ! dept
IR.REC$ = IR.REC$ + IR.SALEQUAN$                   ! deal qty
IR.REC$ = IR.REC$ + IR.SALEPRIC$                   ! price
IR.REC$ = IR.REC$ + IR.MPGROUP$                    ! multipricing group
IR.REC$ = IR.REC$ + IR.ITEMNAME$                   ! name
IR.REC$ = IR.REC$ + IR.USER$                       ! user area
IF IR.APPL.LEN > 50 THEN IR.REC$ = IR.REC$ + IR.RESERVED$
IF IR.USER.LEN > 0 THEN IR.REC$ = IR.REC$ + IR.VAR.USER$
IR.RECL$ = "C" + STR$(IR.RECL)
WRITE FORM IR.RECL$; #59 AUTOUNLOCK; IR.REC$       ! write + release lock
IR.REC$ = ""
```

- The record is reassembled **in field order** from the same `IR.*` globals
  the read populated — a write is always "full record, by key".
- **`AUTOUNLOCK`** (4680 BASIC reference, `WRITE`): "a flag requesting the
  record be unlocked after it is written" — the pair with
  `EALIRRD2`'s `AUTOLOCK`.
- `EALIRWR2.J86` is the same build **without** `AUTOUNLOCK` ("write, no
  autounlock") for routines that keep the lock open across further work.
- `EALIRWR3.J86` is an equivalent variant using `INSERT1` helper calls
  instead of `SUBSTR`.

### 6.2 Who writes it in GSA

| Module | What it does |
|---|---|
| `EALDO1CC` — *Change Item Price* (Daily Operations menu) | authorized-operator price/dept/flag edits; opens, reads (`AUTOLOCK`), rewrites |
| `EALDB5AC` — *Add/Change an Item Record* (data maintenance) | new items and full-record updates — the manual's "data maintenance" writer |
| `EALTSPCC` — *Item Record Price Verify/Change* | price verification/change workflow |
| DDM timer task + `EALDMTVC` | host-driven batch application (Section 6.3) |
| `EALIRRBC` — *Item Record File Rebuild Utility* | rebuilds the keyed file (output staged as `EALITMNX`) |

Deletion of keyed records uses routines such as `MEMDLKEY`
("delete keyed records from a…") in `EALFST2C.BAS` — i.e. keyed-file
`DELETE` by key, which the OS performs by zeroing the record slot (the
all-zero-key empty convention of Section 2.2).

### 6.3 DDM — host-driven updates (the main production writer)

From GSA Ch. 6 "Accessing Delayed Data Maintenance" (p. 87) and source:

1. **Host** creates a **batch data file** of item-record change records plus
   a **control record (PRB)** naming the target file (`EALITEMR`), the
   activity, and the batch file. Transmitted in binary, **EBCDIC**.
2. **Store controller** holds the batch; the DDM **timer task**
   (`EALDMTTC` — "DDM Timer Task") applies it when due (immediately, at a
   timer, or on operator action).
3. **`EALDMTVC`** ("Rename and Delete Item File Routines") performs the swap
   (its own header comment):
   - started by the DDM timer task to **copy pending file `EALITMNX` to
     `EALITEMR`**;
   - first **backs up `EALITEMR` to `EALITMBK`**; if the backup is OK, the
     pending file is copied over the current one;
   - calls **`ADXFILES`** to restrict/despole the file (so it runs at low
     volume — "at night");
   - `RENAMES.FILE`/`RENAME.FILE` helper functions produce the
     **timestamped backup filename** before each rename (rollback safety).
4. Host change record forms (GSA Ch. 6, p. 91–92) — item number is
   22 chars (type char + up to 21 digits), no delimiters, first byte is a
   record-length byte; shown EBCDIC in the manual (`CCCC…` = digit fields):

   | Activity | In-store shape |
   |---|---|
   | **ADD** | full record (price, flags, name, dept, user area…) with no host message |
   | **REPLACE** | item # + price + name (price change) |
   | **DELETE** | item # only |

   ("Adding to the Item Record Data", "Replacing Item Record Prices",
   "Deleting an Item Record" — p. 91–92.)

### 6.4 Concurrency & safety model

- **Record-level**: `AUTOLOCK`/`AUTOUNLOCK` pair (Section 5.3/6.1) — only
  one writer holds a record; readers of the locked record wait.
- **File-level**: DDM swap runs with the application file **restricted**
  (despoled) via `ADXFILES`; a backup exists (`EALITMBK` + timestamped
  renames) so a bad batch can be rolled back.
- **Audit**: operator price changes open `EALAUDIC` in `APPEND` mode when
  the audit-log store option is on (`EALDO1CC.BAS`).
- The file is **C&T compound-distributed**: terminal and controller copies
  exist; `$ALITEMR` addresses the distributed variant (GSA App A, p. 201).

---


## 7. How the local C tools read/write it

Both tools open the `.DAT` in **raw binary, whole-file**, with no keyed-file
runtime — they implement Sections 2–3 directly. This is exactly how the file
must look on disk, and it is the reason the tools can verify GSA-produced
files.

### 7.1 Read path (both tools)

1. **Load** the entire file into memory (`load_file2`).
2. **Parse the control record** (block 0) for `NBLK`, `RS` (record size),
   `DIVISOR`, `KLEN`, `THR`, with sensible fallbacks if any field is
   implausible (Sections 2.1).
3. **Address a slot** (`rec_at`):
   `offset = block × 512 + 4 + slot × RS` — i.e. skip the 4-byte chain
   header, then slot index × record size.
4. **Empty-slot test**: first `KLEN` bytes all zero.
5. **Hash** (`hash_blk`, IBM folding, Section 2.3) to find the home block.

   - `ealitemr_report` doesn't even need the hash — it **scans every block
     and slot** and emits any well-formed occupied slot
     (valid BCD key + printable ASCII description), skipping past the record
     to avoid slide-duplicate matches. This is the most robust reader.
   - `ealitemr_edit` uses the hash to locate a single record
     (`find_rec`: home block first, then full-file scan covering overflow
     chains), which is how `get`/`set`/`del`/`raw` resolve a key.
6. **Decode** fields with the same offsets as the GSA read include
   (`INDICAT0`@11 … `ITEMNAME`@24, `USER`@42), BCD→decimal via
   `bcd_digits`, price via `bcd_val`/`price_str` (PPPPPPPPPP = cents).

### 7.2 Write path (`ealitemr_edit`)

Encoding is the inverse of the decode (`pack_digits`, right-justified,
high-nibble-first, same as GSA/PACK$), then:

1. **Locate or allocate** a slot — `find_free`, in this priority:
   - an empty slot in the **home block**;
   - an empty slot in an existing **overflow chain** off the home (`chain_find_free`
     walks the fwd/bwd pointers);
   - otherwise take a **pristine block** (no block points to it, no records)
     and **append it to the chain tail** (`append_chain`) with proper
     fwd/bwd pointers.
   This ordering reproduces GSA's keyed-file placement: home first, then
   chain — and avoids the earlier bug that left *orphan* records the GSA
   runtime could never find.
2. **`del`** zeroes the record's key (and slot) — the all-zero-key empty
   convention GSA itself relies on.
3. **Save** the full image back (`save_file`), byte-exact.

### 7.3 Verification & safety model

- `verify`/`stats` classify **every** record as *home / chained / orphan*
  — an orphan (reachable neither by hash nor by any chain pointer) is the
  signature of a corrupted or tool-mis-written keyed file.
- **Writes were validated on copies** (`/tmp`), never in-place on the
  production `GSA EALITEMR.DAT`; the real file is kept byte-identical to
  `/tmp/EALITEMR.DAT.bak`. `set` round-trips name/price/dept; `add` places
  at the home block; `del` zeroes in place. All 140 reportable codes (of
  150 total, incl. 2 raw-hex DCS-style keys) are found at their computed
  home block with **zero orphans / zero chains** — i.e. the file as shipped
  is exactly "one record per home block, no overflow".

**Rule of thumb:** treat `EALITEMR.DAT` as a *keyed hash file* at the
application layer (lookup by item code, upsert by item code), *never* as a
flat record array. The only legitimate physical-level writers are GSA's DDM
swap (backup → copy → restricted) and the rebuild utility; anything else must
go through the slot/chain/hash rules above.

---

## 8. Quick reference

| Question | Answer |
|---|---|
| File type | Keyed (hashed) file, 512-byte blocks, no index |
| Key | Item number, `ITEMCODE`, PD, 11 B, type nibble in top 4 bits |
| Record length | 50 (default) / 63 / 71 bytes, from control block |
| Control record | Block 0: `NBLK`@42, `RS`@46, `DIV`@48, `KLEN`@54, `THR`@56 (all LE) |
| Data block layout | 4 B chain (fwd/bwd) + 10 × 50 B slots; empty slot = all-zero key |
| Hash | IBM folding → `% 999`; remainder 0 ⇒ block 999 |
| GSA read | `READ FORM "C.."; #59 KEY IR.ITEMCODE$; IR.REC$` (AUTOLOCK for update) |
| GSA write | `WRITE FORM "C.."; #59 [AUTOUNLOCK]; IR.REC$` (record = key + fields in order) |
| Production writer | DDM: host batch → `EALITMNX`, backup `EALITEMR`→`EALITMBK`, swap |
| Local tools | Raw whole-file read; slot/chain/hash placement on write; `verify` checks orphans |

