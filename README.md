# gsa-cli

A command-line utility for maintaining EALITEMR.DAT item databases — the kind of
file a IBM 4680-era point-of-sale register reads. Load it, browse items, insert,
update, drop, import from CSV, export to CSV, and play back scripts — with
transactional COMMIT/ROLLBACK semantics throughout.

- Single executable: `gsa-cli.exe` (Windows) / `gsa-cli` (POSIX), plus its
  companion DLLs (`EalItemr.dll`, `Xml2.dll`, `CSVToolkit.dll`).
- Targets the .NET Framework 2.0 API surface; builds with the in-box v2.0
  compiler on Windows and Mono's `mcs` on POSIX. Runs from Windows XP through
  11, and on Linux/macOS under Mono.

This program is licensed under The Pirate License. Please see LICENSE.txt for a
copy of The Pirate License.

Cash register icon courtesy Aha-Soft, https://www.aha-soft.com

## Building

```bat
:: Windows (strict v2.0 csc gate)
build.bat
```

```sh
# POSIX / Mono
./build.sh
```

Both produce `bin/gsa-cli.exe` (+ the three DLLs). The settings template and
the CSV template are embedded in the executable at build time; no external
files are required beyond the companion DLLs.

On Windows, MSBuild works too (`gsa-cli.csproj`, verified with the in-box
MSBuild 4.x): it emits one self-contained `bin\Release\gsa-cli.exe` with the
libraries compiled in, so that copy needs no companion DLLs.

## Quickstart

Start it with no arguments for config mode:

```text
C:\> gsa-cli.exe
gsa-cli command line utility for EALITEMR.DAT
Copyleft 2026 - The Crusty Computer Club, https://crusty.computer
With thanks to the Dongvile Point-of-Sale Community

No file has been loaded

config> LOAD 'GSA EALITEMR.DAT'
Loaded GSA EALITEMR.DAT OK - 151 records in use, 9949 slots free
gsa [(GSA EALITEMR.DAT)]> INSERT VALUES ('9777','PLAY ITEM',1,'499')
OK, inserted item 9777
gsa [(GSA EALITEMR.DAT)]> SELECT 9777
ITEM_CODE  ITEM_NAME   DEPT       SALEPRICECENTS  QTY  MVMT  REQ_QTY  ...
9777       PLAY ITEM   1 (BOOKS)  499             0    N     N        ...
gsa [(GSA EALITEMR.DAT)]> COMMIT
OK, changes committed to disk!
```

Changes are staged until `COMMIT`; `ROLLBACK` discards them. Exiting with
uncommitted work prompts you to commit or use `BYE FORCE`.

Import the sample CSV (upserts by ITEM_CODE — existing records update, new
records insert):

```text
gsa [(GSA EALITEMR.DAT)]> IMPORT 'sample.csv'
Progress: 100%
OK, imported 1 records
Warning: 1 rows had SALEPRICE2 dropped (Pricing Method 0 or 1).
OK, changes committed to disk!
```

Export back out (existing target files require `OVERWRITE`):

```text
gsa [(GSA EALITEMR.DAT)]> EXPORT * TO 'out.csv'
Progress: 100%
OK, exported 153 records to 'out.csv'
```

## One-shot modes

For scripting and batch use — no interactive session:

```bat
gsa-cli.exe -verify mydb.DAT          :: integrity check; exit 0 = OK
gsa-cli.exe mydb.DAT -import data.csv :: import a whole CSV file
gsa-cli.exe mydb.DAT -play script.scr :: play back a COMMIT-terminated script
```

Each exits 0 on success and non-zero on failure, printing the exact error.

## WHERE clauses

`SELECT`, `COUNT`, `UPDATE`, `DROP`, `IMPORT`, and `EXPORT` all accept WHERE
predicates with AND/OR, parentheses, inclusive item-code ranges, and wildcards:

```text
gsa> SELECT * WHERE ITEM_NAME='*RACER' AND DEPT='1'
gsa> UPDATE * WHERE SALEPRICECENTS='?00' SET SALEPRICECENTS='599';
```

`*` matches any run of characters, `?` exactly one; matching is
case-insensitive. Quoted values may contain spaces (`ITEM_NAME='MOTO RACER'`).

## Documentation

- [DOCUMENTATION.md](DOCUMENTATION.md) — manual for configuration and command syntax

## Testing

```powershell
# fast suite (13 tests) + slow D58 full-scan pin (t14)
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/testsuite.ps1 -IncludeSlow
```

Byte-exact expected outputs live next to each test under `Tools/tests/`.
