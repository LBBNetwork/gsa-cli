# Welcome to GSA-CLI

Welcome to GSA-CLI, your Windows companion to the IBM General Sales Application's item record file. Before starting, let's make sure you have the following:

- Microsoft Windows XP with Service Pack 2 or greater
- Intel Core 2 Duo or greater (recommended)
- 512 MB of RAM (1 GB or greater preferred)
- Microsoft .NET Framework 2.0
- A copy of your EALITEMR.DAT item file, and FTP access to your IBM 4690 Store Controller if you intend to upload the result

Run `gsa-cli.exe` from its directory with no arguments to start in config mode. The program prints a banner, announces that no file is loaded, and drops you at the `config>` prompt:

```
gsa-cli command line utility for EALITEMR.DAT
Copyleft 2026 - The Crusty Computer Club, https://crusty.computer
With thanks to the Dongvile Point-of-Sale Community

No file has been loaded

config> 
```

On first run gsa-cli copies its `Settings.xml` defaults file out next to the executable; if that file is later found corrupt it is renamed aside and a fresh one created, with a notice printed. If another gsa-cli instance is already running, settings editing is disabled for your session (reads still work) and a note is printed at startup.

## Command Line Options

The full option table, exactly as `gsa-cli` prints it:

```
Usage:
  gsa-cli [DB.DAT] -import F   import a CSV (Phase 5)
  gsa-cli [DB.DAT] -play S      run a play script (Phase 5)
  gsa-cli -verify <DB.DAT>     verify database integrity (one-shot)
  gsa-cli                    - interactive config mode
  gsa-cli <DB.DAT>             - interactive, database preloaded
```

- `gsa-cli` — starts the interactive session in config mode. This is where you manage settings, list databases, and load a file for editing.
- `gsa-cli <DB.DAT>` — starts the interactive session with that database already loaded (plain LOAD semantics: an existing lock refuses the start). The session goes straight to database mode; no "No file has been loaded" notice is printed.
- `gsa-cli [DB.DAT] -import F` — one-shot CSV import of file `F` into database `DB.DAT`, then exit. Exits 0 on a completed run, 1 on any hard failure (including an unloadable database, in which case no rows are accepted).
- `gsa-cli [DB.DAT] -play S` — one-shot playback of play script `S` against database `DB.DAT`, then exit. Exits 0 only if every line completed cleanly; on any failure the staged changes are rolled back and it exits 1.
- `gsa-cli -verify <DB.DAT>` — one-shot integrity check that opens the file read-only, never claims a lock, and never mutates anything. Exits 0 when the file verifies clean, 1 otherwise.

Example - Preloading a database:

```
C:\> gsa-cli test.dat
gsa-cli command line utility for EALITEMR.DAT
Copyleft 2026 - The Crusty Computer Club, https://crusty.computer
With thanks to the Dongvile Point-of-Sale Community

Loaded test.dat OK - 8 records in use, 10042 slots free
gsa [(test.dat)]> 
```

Example - One-shot integrity check (clean file):

```
C:\> gsa-cli -verify test.dat
File: test.dat
Header OK
Slots: 10050 (header) == 10050 (calculated from file size)
Records: 8 in use, 10042 free
VERIFY OK
```

Example - One-shot integrity check (damaged file):

```
C:\> gsa-cli -verify corrupt.dat
File: corrupt.dat
Header OK
Slots: 10050 (header) == 10050 (calculated from file size)
Records: 8 in use, 10042 free
VERIFY FAILED (6 orphaned record(s)).
```

A damaged file exits with code 1; a clean file exits with code 0. Any other unrecognized combination of arguments prints the usage table and exits with code 2.

# Theory of Operation

GSA-CLI is a line-oriented, SQL-flavored editor for EALITEMR.DAT item files. One line is one command: whitespace separates tokens, keywords are case-insensitive, values may be wrapped in single or double quotes (mixed quoting inside one value is a syntax error), and a single trailing semicolon is optional after any command. The tool runs in two modes announced only by the prompt: `config>` when no database is loaded, and `gsa [(<file>)]>` — showing the loaded file's name exactly as you gave it — once a database is open.

Every change to a loaded database is *staged* in memory first; nothing touches disk until you run `COMMIT`. `ROLLBACK` discards staged work and reloads from disk, and ending the session without committing (EOF, or `BYE FORCE`) also discards it. The CSV import and play-script commands are all-or-nothing: a successful IMPORT commits itself, a failed one rolls back; a PLAY script must end in COMMIT and any failing line aborts playback with a rollback.

The record engine (`EalItemr.cs`) speaks the native IBM keyed-file format of the 4690: the file is a sequence of 512-byte blocks where block 0 is the Keyed File Control Block (slot count at +42, record size 50 at +46, randomizing divisor 999 at +48, key length 11 at +54) and every data block holds a 4-byte chain header plus ten 50-byte records. Each item's 11-digit code is stored as packed BCD; its home block is the folded key modulo 999 (remainder 0 maps to block 999), with overflow slots linked by u16 chain pointers. The library opens a file in session mode, keeps an in-memory index of used slots, and applies staged changes atomically on commit — which is what makes `COMMIT`/`ROLLBACK` safe.

The CSV layer (`CSVToolkit`) is a small streaming reader/writer that handles quoted fields and produces the UTF-8 (BOM-prefixed) files used by `EXPORT`, `IMPORT`, and `CREATE CSV`. It never loads whole files into memory, so large item lists are fine even on 512 MB machines.

# VALUES / Columns

These are the columns gsa-cli understands in EALITEMR files. SELECT displays them under short names; INSERT, UPDATE, EXPORT, and IMPORT use the long names (the CSV header row is authoritative for file work).

| Column | Display name | Meaning |
|---|---|---|
| `ITEM_CODE` | `ITEM_CODE` | The item's code — the record key. Must be numeric digits only when supplied to INSERT or IMPORT. Code 0 is invalid: supplying it to INSERT, RECODE or IMPORT substitutes a generated free code per the CODE_METHOD setting (the record is stored under that code, never under zero). Shown in canonical form with leading zeros stripped; select by it using a target (`SELECT 100`, `UPDATE 100 SET ...`) or a range, not a WHERE clause (a WHERE predicate on ITEM_CODE is rejected with a validation error pointing you to the target form). |
| `ITEM_NAME` | `ITEM_NAME` | Item description. Stored in uppercase — names supplied to INSERT, UPDATE or IMPORT are converted to uppercase with no warning (a round trip through EXPORT/IMPORT rewrites 'nice' as 'NICE'). Alphanumeric and spaces only — any other character is rejected on INSERT/UPDATE/IMPORT; names longer than 18 characters are truncated per the `NAME_VALIDATION` setting. |
| `DEPT` | `DEPT` | Department number; displayed as the number plus its Settings.xml name, e.g. `1 (BOOKS)`. |
| `SALEPRICECENTS` | `SALEPRICECENTS` | Sale price in whole cents — always the unit/single-item price. Displayed raw (`399`, not `$3.99`). Must be a non-negative integer. |
| `SALEPRICE2` | `SALEPRICE2` | Optional second price in cents, meaningful only with Pricing Method 2 or 3 (package price for method 2, reduced price for method 3). Supplying a non-zero value under methods 0/1 warns and drops the value; a zero is silently ignored since it carries no information. SELECT shows this column only when the record's PRICE_METHOD is 2 or 3. |
| `QTY` | `QTY` | Quantity on hand, a whole number from 0 to 99 (the GSA file format stores it as a single packed-decimal byte — two digits — so 99 is the maximum). |
| `TRACK_MOVEMENT` | `MVMT` | Y/N — track item movement at the register. |
| `REQUIRE_QTY` | `REQ_QTY` | Y/N — require a quantity entry when selling. |
| `REQUIRE_PRICE` | `REQ_PRICE` | Y/N — require a price entry when selling. |
| `SELL_HOURS` | `ALL_HOURS` | Y/N — sell during normal store hours (stored inverted in the file; gsa-cli keeps the friendly name). |
| `SELL_RESTRICTED` | `RESTRICTED_HOURS` | Y/N — restricted-hours item. |
| `NO_RETURN` | `NO_RETURN` | Y/N — no returns allowed on this item. |
| `GIVEAWAY` | `GIVEAWAY` | Y/N — giveaway item. |
| `TAX1` – `TAX4` | `TAX1` – `TAX4` | Y/N flags selecting which of the four tax tables apply to this item. |
| `PRICE_METHOD` | `PRICE_METHOD` | Single-digit pricing method; methods 2 and 3 are the ones that carry a second price (SALEPRICE2). |
| `DCS_METHOD` | `DCS_METHOD` | Single-digit DCS method for the item. |

All Y/N flags and both method digits fall back to their Settings.xml defaults when omitted from an INSERT or UPDATE, so the four required fields are really just `ITEM_CODE`, `ITEM_NAME`, `DEPT`, and `SALEPRICECENTS`.

# CONFIG Commands

CONFIG;
The CONFIG command changes values in gsa-cli's Settings.xml file. It takes one or more `KEY=VALUE` pairs on a single line, each pair validated against the known settings keys before anything is written — if any pair is invalid, nothing is changed. The valid keys are exactly those shown by SHOW CONFIG: `CODE_METHOD`, `NAME_VALIDATION`, `QTY`, `TRACK_MOVEMENT`, `REQUIRE_QTY`, `REQUIRE_PRICE`, `SELL_HOURS`, `SELL_RESTRICTED`, `NO_RETURN`, `GIVEAWAY`, `TAX1` through `TAX4`, `PRICE_METHOD`, and `DCS_METHOD`. CONFIG is only available in config mode; with a database loaded it refuses, pointing you at SHOW CONFIG instead.

Example 1 - Setting one value:
```
config> CONFIG QTY=7
OK, Settings.xml updated (QTY=7)
```

Example 2 - Unknown key:
```
config> CONFIG BOGUS=1
Command Error: unknown settings parameter 'BOGUS'. SHOW CONFIG lists valid parameters.
```

Example 3 - Out-of-range value:
```
config> CONFIG QTY=-3
Validation Error: QTY must be a whole number 0-99.
```

SHOW CONFIG;
The SHOW CONFIG command prints the current settings as `KEY=value` lines, one per line. It takes no arguments and works in both config mode and database mode (in database mode it is the only SHOW command that touches settings).

Example:
```
config> SHOW CONFIG
CODE_METHOD=upc-a
NAME_VALIDATION=truncate
QTY=0
TRACK_MOVEMENT=N
REQUIRE_QTY=N
REQUIRE_PRICE=N
SELL_HOURS=Y
SELL_RESTRICTED=Y
NO_RETURN=N
GIVEAWAY=N
TAX1=N
TAX2=N
TAX3=N
TAX4=N
PRICE_METHOD=0
DCS_METHOD=0
```

SHOW DEPARTMENTS;
The SHOW DEPARTMENTS command lists the department numbers and names from Settings.xml, one per line. It takes no arguments and is config-mode only — in database mode gsa-cli answers that only SHOW DATABASE is available there. Department names are what SELECT prints next to each record's DEPT value.

Example:
```
config> SHOW DEPARTMENTS
 1 - BOOKS
 2 - MOVIES
 3 - MUSIC
```

Departments themselves are edited with the special CONFIG form `CONFIG DEPARTMENT <n> '<name>'`, which upserts that number's name (there is no delete form — rename it instead).

Example:
```
config> CONFIG DEPARTMENT 9 "TESTING"
OK, Settings.xml updated (DEPARTMENT 9=TESTING)
config> SHOW DEPARTMENTS
 1 - BOOKS
 2 - MOVIES
 3 - MUSIC
 9 - TESTING
```

SHOW DATABASES;
The SHOW DATABASES command lists all of the available .DAT files that gsa-cli can work with, numbered and sorted by most recently modified file on top. This command takes one optional argument: if no argument is specified it scans the current working directory, and if an argument is given it must be a directory path to scan instead. The listing numbers are what `LOAD <n>` refers to within the same session.

Example 1 - No arguments (working directory):
```
config> SHOW DATABASES
FILENAME                                        MODIFIED
1. H:\llm\gsa-cli\docs-example.dat              2026-11-10 20:48
2. H:\llm\gsa-cli\p1-600k.DAT                   2026-10-29 21:16
3. H:\llm\gsa-cli\GSA EALITEMR EXTRA TESTS.DAT  2026-10-23 22:02
4. H:\llm\gsa-cli\GSA EALITEMR.DAT              2026-10-09 10:39
```

Example 2 - Directory argument:
```
config> SHOW DATABASES h:\llm\gsa-cli
FILENAME                                        MODIFIED
1. h:\llm\gsa-cli\docs-example.dat              2026-11-10 20:48
2. h:\llm\gsa-cli\p1-600k.DAT                   2026-10-29 21:16
3. h:\llm\gsa-cli\GSA EALITEMR EXTRA TESTS.DAT  2026-10-23 22:02
4. h:\llm\gsa-cli\GSA EALITEMR.DAT              2026-10-09 10:39
```

LOAD;
The LOAD command loads the specified database into gsa-cli and enables database mode. This command requires one argument, which is either a direct file path (one token — filenames containing spaces must be loaded by their SHOW DATABASES number) or a numeric entry from a SHOW DATABASES listing in this session. When a database is loaded, gsa-cli writes a lock file (`<file>.lock`, same directory) so that no other copy of gsa-cli can open the file while the lock is active; UNLOAD removes it again. An optional OVERRIDE keyword may be placed before the argument to force past an existing lock file — but if the second gsa-cli process holding the lock is still alive, the override fails and the load is refused.

Example 1 - Direct file path:
```
config> LOAD test.dat
Loaded test.dat OK - 8 records in use, 10042 slots free
gsa [(test.dat)]> 
```

Example 2 - Load following SHOW DATABASES:
```
config> SHOW DATABASES
FILENAME                                        MODIFIED
1. H:\llm\gsa-cli\docs-example.dat              2026-11-10 20:48
2. H:\llm\gsa-cli\p1-600k.DAT                   2026-10-29 21:16
3. H:\llm\gsa-cli\GSA EALITEMR EXTRA TESTS.DAT  2026-10-23 22:02
4. H:\llm\gsa-cli\GSA EALITEMR.DAT              2026-10-09 10:39
config> LOAD 4
Loaded H:\llm\gsa-cli\GSA EALITEMR.DAT OK - 151 records in use, 9949 slots free
gsa [(GSA EALITEMR.DAT)]> 
```

Example 3 - Load with OVERRIDE (stale lock):
```
config> LOAD test.dat
test.dat is already in use (lock file present, owner unknown - use LOAD OVERRIDE if you are sure it is stale).
config> LOAD OVERRIDE test.dat
Loaded test.dat OK - 8 records in use, 10042 slots free
gsa [(test.dat)]> 
```

Failure conditions: a missing file answers `Database Exception: cannot open <file>`; loading while another database is already loaded answers `Command Error: A database is already loaded (<file>). UNLOAD first (UNLOAD FORCE discards staged changes).`; and a numeric load before any SHOW DATABASES in this session answers `Command Error: no numbered databases yet. Run SHOW DATABASES first.`

UNLOAD;
The UNLOAD command releases the loaded database, deletes its lock file, and returns gsa-cli to config mode. It takes one optional argument, FORCE, which discards staged changes before releasing — plain UNLOAD with pending changes is refused until you COMMIT or ROLLBACK them. With no database loaded it simply reports that there is nothing to unload.

Example 1 - Clean unload:
```
gsa [(test.dat)]> UNLOAD
Database successfully unloaded
config> 
```

Example 2 - Forced unload with staged changes:
```
gsa [(test.dat)]> INSERT VALUES (961, "STAGE", 1, 100)
OK, inserted item 961
gsa [(test.dat)]> UNLOAD FORCE
Staged changes discarded.
Database successfully unloaded
config> 
```

CREATE NEW;
The CREATE NEW command creates a brand-new empty EALITEMR database file with the standard keyed-file layout (block 0 control header plus data blocks of ten 50-byte slots). It requires two arguments, the target filename and a `SLOTS n` modifier requesting at least that many record slots; without a slot count the command is rejected. The request is rounded up to whole blocks of ten, and every new file gets a floor of 10,000 slots — the same layout as a real GSA EALITEMR.DAT. The command refuses to overwrite an existing file: delete it first if you want to start from scratch.

Example 1 - Requested slot count:
```
config> CREATE NEW big.dat SLOTS 20050
OK, created big.dat with 20050 record slots (1027072 bytes)
```

Example 2 - Missing slot count:
```
config> CREATE NEW test.dat
Command Error: CREATE NEW requires a slot count. Use CREATE NEW <file> SLOTS n.
```

Example 3 - File already exists:
```
config> CREATE NEW big.dat SLOTS 10000
Command Error: big.dat already exists. Delete it first.
```

# DATABASE Commands - Read Only

SELECT;
The SELECT command shows the operator a list of records as an aligned table. It requires one argument, which can be `*` for all records, a single item code, or a range of item codes (`400-500`, inclusive). It can also take a WHERE modifier so the operator can select matching patterns — comparisons are case-insensitive and support `?` (one character) and `*` (any run) wildcards, with AND, OR, and parentheses for compound conditions. It can also take a VALUES modifier listing which columns to display; if no VALUES modifier is provided, SELECT shows all available columns in the database file, and records whose PRICE_METHOD is 2 or 3 additionally show SALEPRICE2.

Example 1 - All records, all columns:
```
gsa [(test.dat)]> SELECT *
ITEM_CODE  ITEM_NAME  DEPT       SALEPRICECENTS  QTY  MVMT  REQ_QTY  REQ_PRICE  ALL_HOURS  RESTRICTED_HOURS  NO_RETURN  GIVEAWAY  TAX1  TAX2  TAX3  TAX4  PRICE_METHOD  DCS_METHOD
100        MILK       1 (BOOKS)   399             0    N     N        N          Y          Y                 N          N         N     N     N     N     0             0
```

Example 2 - Single code with a VALUES modifier:
```
gsa [(test.dat)]> SELECT 100 VALUES (ITEM_CODE, ITEM_NAME, SALEPRICECENTS)
ITEM_CODE  ITEM_NAME  SALEPRICECENTS
100        MILK       449
```

Example 3 - Range of codes:
```
gsa [(test.dat)]> SELECT 400-500 VALUES (ITEM_CODE, ITEM_NAME)
ITEM_CODE  ITEM_NAME
400        BREAD
500        BUTTER
```

Example 4 - WHERE with wildcards and compound conditions:
```
gsa [(test.dat)]> SELECT * WHERE ITEM_NAME="*BREAD*" VALUES (ITEM_CODE, ITEM_NAME)
ITEM_CODE  ITEM_NAME
400        BREAD
gsa [(test.dat)]> SELECT * WHERE DEPT=1 AND SALEPRICECENTS=?99 VALUES (ITEM_CODE, ITEM_NAME)
ITEM_CODE  ITEM_NAME
100        MILK
500        BUTTER
```

Example 5 - WHERE on ITEM_CODE is rejected (use the target form):
```
gsa [(test.dat)]> SELECT * WHERE ITEM_CODE='100'
Validation Error: WHERE on ITEM_CODE is not supported - use the item code as the command target instead (e.g. SELECT 100 rather than SELECT * WHERE ITEM_CODE='100').
```

A selection that matches nothing is not an error — it prints `No matching records.` and returns you to the prompt. Note that WHERE predicates match record *values* (names, departments, prices, flags); to pick a specific code use the target form (`SELECT 100`) rather than a WHERE clause on ITEM_CODE — the tool rejects a WHERE predicate on ITEM_CODE with the validation error above, since the code is the record key and belongs in the target. This applies to every command that takes a target plus WHERE (SELECT, COUNT, UPDATE, DROP, EXPORT); IMPORT's optional WHERE filter still accepts ITEM_CODE, because there it filters CSV *rows* of a larger file rather than selecting records.

COUNT;
The COUNT command counts matching records and prints just the number. It takes the same target argument as SELECT — `*`, a single code, or a range — plus an optional WHERE modifier with the same pattern rules. A count of zero is printed as `0`; it is not an error.

Example 1 - Count everything:
```
gsa [(test.dat)]> COUNT *
8
```

Example 2 - Count a filtered set:
```
gsa [(test.dat)]> COUNT * WHERE DEPT=3
3
gsa [(test.dat)]> COUNT * WHERE ITEM_NAME="ZZZ"
0
```

SHOW DATABASE;
The SHOW DATABASE command prints the status card for the loaded database: its name, records in use, free slots, and how many staged changes are pending. It takes no arguments and is available only while a database is loaded (in config mode, SHOW DATABASES is what you want).

Example 1 - Nothing staged:
```
gsa [(test.dat)]> SHOW DATABASE
Database: test.dat
Records in use: 8
Free slots:     10042
Staged changes: 0 (none pending)
```

Example 2 - With a staged change:
```
gsa [(test.dat)]> INSERT VALUES (960, "STAGE", 1, 100)
OK, inserted item 960
gsa [(test.dat)]> SHOW DATABASE
Database: test.dat
Records in use: 8
Free slots:     10042
Staged changes: 1 (not yet committed - run COMMIT to persist)
```

# DATABASE Commands - Write Operations

INSERT;
The INSERT command adds a new item record to the database. It requires four values — ITEM_CODE, ITEM_NAME, DEPT, and SALEPRICECENTS — given either positionally as `VALUES (code, "name", dept, cents)` or by name with an INTO clause listing any subset of the columns you want to set; omitted columns take their Settings.xml defaults. If you specify optional parameters positionally, all parameters must be specified — a partial positional list is rejected in favor of building a custom query string. The item code may also be the keyword `rnd4` or `upc-a`, in which case gsa-cli generates a free code for you. Code 0 is not a usable item code: if you supply it, gsa-cli generates a free code using the CODE_METHOD setting and stores the record under that code instead — the success message reports the actual code used.

Example 1 - Positional, four values:
```
gsa [(test.dat)]> INSERT VALUES (100, "MILK", 1, 399)
OK, inserted item 100
```

Example 2 - Named fields:
```
gsa [(test.dat)]> INSERT INTO (ITEM_CODE, ITEM_NAME, DEPT, SALEPRICECENTS) VALUES (200, "SOUP", 2, 549)
OK, inserted item 200
```

Example 3 - Generated code:
```
gsa [(test.dat)]> INSERT VALUES (rnd4, "RAND", 1, 100)
OK, inserted item 1000
```

Failure conditions: inserting a code that already exists answers `Validation Error: ITEM_CODE <code> already exists. Use UPDATE <code> SET ... to modify it, or choose a different code.`; a name containing characters other than letters, digits and spaces answers `Validation Error: ITEM_NAME contains invalid characters (alphanumeric and spaces only).`; a code containing non-numeric characters answers `Validation Error: The required parameter ITEM_CODE contains invalid characters (numeric digits only)`; a partial positional list (e.g. five values) answers `Validation Error: When specifying optional parameters, all parameters must be specified. Please specify all parameters or build a custom query string.`; and supplying a non-zero SALEPRICE2 while PRICE_METHOD is 0 or 1 warns that the value is dropped (a zero is silently ignored) — the rest of the insert proceeds.

UPDATE;
The UPDATE command modifies existing records in place. It requires a target (`*`, a single code, or a range) followed by an optional WHERE modifier narrowing which matching records are touched, then one or more `FIELD=VALUE` assignments after SET. Assignments may set any column from the VALUES table; flags take Y/N and prices take whole cents. A zero-match update is not an error — it reports how many items were updated (zero).

Example 1 - Single record:
```
gsa [(test.dat)]> UPDATE 100 SET SALEPRICECENTS=449
OK, updated item 100
```

Example 2 - WHERE-scoped update:
```
gsa [(test.dat)]> UPDATE * WHERE ITEM_NAME="MILK" SET SALEPRICECENTS=449
OK, updated 1 item
```

Example 3 - Range update with several assignments:
```
gsa [(test.dat)]> UPDATE 400-500 SET QTY=9, TAX1='Y'
OK, updated 2 records (range 400-500)
```

Failure conditions: an assignment to a price column that is not a non-negative whole number of cents answers `Validation Error: SALEPRICECENTS must be a non-negative integer number of cents. (item <code>)`; setting ITEM_NAME to a value with characters other than letters, digits and spaces answers `Validation Error: ITEM_NAME contains invalid characters (alphanumeric and spaces only). (item <code>)`; and updating a code that does not exist simply reports `OK, 0 items updated.` — codes are never created by UPDATE.

DROP;
The DROP command deletes records from the database. It requires a target plus a WHERE modifier naming which records to delete — bare `DROP *` is forbidden, because wiping an entire item file is almost always a mistake (use CREATE NEW if you truly want to start over). A zero-match drop is not an error; it reports that zero records were deleted.

Example 1 - Delete by name:
```
gsa [(test.dat)]> DROP * WHERE ITEM_NAME="SOUP"
OK, deleted 1 record.
```

Example 2 - Bare wildcard refused:
```
gsa [(test.dat)]> DROP *
Validation Error: DROP * is forbidden. If you want to start from scratch, use CREATE NEW <file> SLOTS n.
```

RECODE;
The RECODE command changes an item's code while keeping its record intact — the old key is removed and the same data re-keyed under the new one. It requires exactly two arguments: the existing code and the new code. The new code must be free (not already in use), and the old code must exist; otherwise the operation fails with a validation error and nothing changes. A new code of 0 is invalid — gsa-cli substitutes a generated free code per the CODE_METHOD setting and reports the actual new code.

Example 1 - Recode an item:
```
gsa [(test.dat)]> RECODE 200 TO 300
OK, recoded 200 to 300
```

Example 2 - Failure conditions:
```
gsa [(test.dat)]> RECODE 999 TO 100
Validation Error: ITEM_CODE 999 does not exist.
gsa [(test.dat)]> RECODE 100 TO 400
Validation Error: target ITEM_CODE 400 already exists.
```

EXTEND;
The EXTEND command grows the loaded database by adding record slots at the end of its block chain. It requires one argument — the number of additional slots requested — and reports how many were actually added along with the new total capacity. Because extending rewrites the file's layout, it refuses to run while changes are staged: COMMIT or ROLLBACK first.

Example 1 - Extend a clean database:
```
gsa [(test.dat)]> EXTEND 50
OK, extended database (requested 50 slots, got 50; new total: 10050)
```

Example 2 - Refused with staged changes:
```
gsa [(test.dat)]> INSERT VALUES (970, "EXT", 1, 100)
OK, inserted item 970
gsa [(test.dat)]> EXTEND 10
Command Error: EXTEND would discard 1 staged change(s). COMMIT or ROLLBACK first.
```

COMMIT;
The COMMIT command writes all staged changes to disk and flushes the in-memory state, making them permanent. It takes no arguments. If there is nothing staged it still succeeds — committing an already-clean database is a harmless no-op that confirms the file on disk matches memory.

Example:
```
gsa [(test.dat)]> COMMIT
OK, changes committed to disk!
```

ROLLBACK;
The ROLLBACK command discards all staged changes and reloads the database from disk, restoring it to exactly what was last committed. It takes no arguments and is always safe — it is the "undo everything since the last COMMIT" button.

Example:
```
gsa [(test.dat)]> DROP * WHERE ITEM_NAME="SOUP"
OK, deleted 1 record.
gsa [(test.dat)]> ROLLBACK
OK, flushed RAM and reloaded!
gsa [(test.dat)]> COUNT *
2
```

# CSV commands

CREATE CSV;
The CREATE CSV command generates a ready-to-fill import template: the full 19-column header row (UTF-8 with BOM) and no data rows. It takes one optional argument, the output filename — without an argument it writes `template.csv` in the current directory. If the target file already exists the command refuses to overwrite it; delete or rename it first.

Example 1 - Default name:
```
config> CREATE CSV
OK, created template.csv (190 bytes)
```

Example 2 - Named file:
```
config> CREATE CSV my.csv
OK, created my.csv (190 bytes)
```

Example 3 - File already exists:
```
config> CREATE CSV template.csv
Command Error: template.csv already exists. Delete it first.
```

EXPORT;
The EXPORT command writes records from the loaded database to a CSV file using the standard 19-column layout. It requires a target (`*`, a code, or a range), an optional WHERE modifier, and a `TO '<file>.csv'` clause naming the destination. If the destination already exists the export is refused unless you include the OVERWRITE keyword between the selection and TO — this protects against silently clobbering an import file you still need. Progress is reported as rows are written, ending at 100%. Note that EXPORT writes records verbatim from the file without re-validating them: a database holding data created by other tools (for example names with special characters) will export fine, but those rows may be rejected on re-import because IMPORT enforces the character-set rules in the column table.

Example 1 - Export everything:
```
gsa [(test.dat)]> EXPORT * TO out.csv
Progress: 100%
OK, exported 2 records to 'out.csv'
```

Example 2 - Existing file without OVERWRITE:
```
gsa [(test.dat)]> EXPORT * TO out.csv
Command Error: out.csv already exists. Re-run with OVERWRITE to replace it.
```

Example 3 - Replace with OVERWRITE:
```
gsa [(test.dat)]> EXPORT * OVERWRITE TO out.csv
Progress: 100%
OK, exported 2 records to 'out.csv'
```

IMPORT;
The IMPORT command reads a CSV file and upserts its rows into the loaded database — each row is matched by ITEM_CODE, updating an existing record or inserting a new one. The file must carry the standard header (ITEM_CODE is required) and every data row must contain all 19 fields; short or long rows abort the import. An optional WHERE modifier filters which CSV *rows* are applied (e.g. `WHERE DEPT=3`), so you can import a slice of a larger file. A successful import commits itself automatically; any failure rolls back every staged change from that import, leaving the database exactly as it was.

Example 1 - Importing new and existing rows:
```
gsa [(test.dat)]> IMPORT template.csv
Progress: 100%
OK, imported 2 records
Warning: 1 rows had SALEPRICE2 dropped (Pricing Method 0 or 1).
OK, changes committed to disk!
```

Example 2 - Re-importing the same file updates in place:
```
gsa [(test.dat)]> IMPORT template.csv
Progress: 100%
OK, imported 0 records, updated 2 records
Warning: 1 rows had SALEPRICE2 dropped (Pricing Method 0 or 1).
OK, changes committed to disk!
```

Example 3 - Row with the wrong field count aborts everything:
```
gsa [(test.dat)]> IMPORT short.csv
Command Error: expected 19 fields, found 4
Aborted.
Staged import changes rolled back.
```

Rows that violate the character-set rules (an ITEM_NAME with characters other than letters, digits and spaces, or a non-numeric ITEM_CODE) are rejected individually and listed after the summary with their reason — valid rows in the same file still import:

```
gsa [(test.dat)]> IMPORT mixed.csv
Progress: 100%
OK, imported 1 records, rejected the following ITEM_CODEs:
- 500 - ITEM_NAME contains invalid characters (alphanumeric and spaces only).
OK, changes committed to disk!
```

Failure conditions: a missing file answers `Command Error: could not open '<file>' (file not found).`; a header without ITEM_CODE answers `Command Error: '<path>' is missing required column 'ITEM_CODE'.`; rows whose names exceed 18 characters are truncated, with an aggregate warning printed; and short or long rows abort the whole import. The one-shot form (`gsa-cli [DB.DAT] -import F`) runs the same import over the whole file with no slice or filter, committing on success and exiting 0/1 accordingly.

# PLAY Scripts

A play script is a plain-text file of gsa-cli commands — one per line — that runs as a single all-or-nothing transaction against a loaded database. The rules for a valid script are strict: every line must lex cleanly and name a known command; `BYE`/`QUIT` are refused up front because they would end the session mid-script, and so are `CONFIG` and `UNLOAD`, which are session-level commands — CONFIG is config-mode-only (it silently no-ops against a loaded database) and UNLOAD would unload the very database the script runs on; and the last non-blank line must be COMMIT, which is what makes the whole run durable. If any line fails during playback, gsa-cli stops at that line, rolls back every staged change from this script, and reports the failing line number — nothing partial survives. A ROLLBACK inside a script is legal and simply discards the staged work up to that point.

PLAY;
The PLAY command executes a play script against the loaded database. It requires one argument, the script filename, and takes one optional modifier, LOUD: silent mode (the default) prints only the final result, while LOUD echoes each command and its response as it runs so you can watch the transaction unfold.

Example 1 - Silent playback (success):
```
gsa [(test.dat)]> PLAY good.script
OK, played 'good.script' successfully
```

Example 2 - Loud playback:
```
gsa [(test.dat)]> PLAY loud.script LOUD
OK, playing 'loud.script'...

INSERT VALUES (610, "JAM", 1, 725)
OK, inserted item 610

COMMIT
OK, changes committed to disk!


Played 'loud.script' successfully
```

Example 3 - A failing line aborts and rolls back:
```
gsa [(test.dat)]> PLAY bad.script
Script Error: 'bad.script' line 2: Validation Error: ITEM_CODE 800 already exists. Use UPDATE 800 SET ... to modify it, or choose a different code.
Staged changes rolled back.
```

Example 4 - Session-level commands are refused before playback starts:
```
gsa [(test.dat)]> PLAY bye.script
Script Error: 'bye.script' line 2: BYE/QUIT is not allowed in PLAY scripts.
gsa [(test.dat)]> PLAY c.script
Script Error: 'c.script' line 1: CONFIG is not allowed in PLAY scripts.
gsa [(test.dat)]> PLAY u.script
Script Error: 'u.script' line 1: UNLOAD is not allowed in PLAY scripts.
```

The refusal happens before any line executes, so nothing partial survives and the loaded database is left exactly as it was — make CONFIG changes from config mode (no database loaded) and keep UNLOAD out of scripts.

The one-shot form (`gsa-cli [DB.DAT] -play S`) loads the database, plays the script silently, and exits — 0 only if every line completed cleanly (the terminal COMMIT made it durable), 1 on a pre-parse failure or any failed execution line:

```
C:\> gsa-cli test.dat -play oneshot.script
OK, played 'oneshot.script' successfully
```

# Miscellaneous Commands

BYE / QUIT;
The BYE and QUIT commands end the session. With no staged changes they exit immediately; with pending work they refuse until you commit or force it — `BYE FORCE` (or `QUIT FORCE`) discards the staged changes and exits anyway. Reaching end-of-input at the prompt ends the session the same way: silently, and uncommitted staged changes are discarded either way, so COMMIT is what makes your work real.

Example 1 - Refused with pending commits:
```
gsa [(test.dat)]> BYE
The database has pending commits. COMMIT your changes first or specify QUIT / BYE FORCE; to exit anyway.
```

Example 2 - Forced exit:
```
gsa [(test.dat)]> BYE FORCE
Staged changes discarded.
Exiting!
```

HELP;
The HELP command prints the list of commands available in the current mode — config-mode help lists CONFIG, SHOW CONFIG, SHOW DEPARTMENTS, SHOW DATABASES, LOAD, UNLOAD, and the session commands; database-mode help lists SELECT, COUNT, SHOW DATABASE, UNLOAD, and the session commands. It takes no arguments.

Example - Database mode:
```
gsa [(test.dat)]> HELP
Commands in database mode:
  SELECT <code|range|*> [WHERE ...] [VALUES (...)] - show records
  COUNT <code|range|*> [WHERE ...]                 - count matching records
  SHOW DATABASE                                    - database status card
  UNLOAD [FORCE]                                   - release the database
  HELP                                             - this help
  BYE / QUIT                                       - exit gsa-cli
```

Interrupts;
Pressing Ctrl+C at any point interrupts the current operation and returns you to the prompt instead of terminating gsa-cli — staged work is left exactly as it was, so you can ROLLBACK or COMMIT afterwards. One-shot modes (`-import`, `-play`, `-verify`) keep the default terminate-on-Ctrl+C behavior, since nothing in them polls for an abort.

Exit codes;
gsa-cli exits 0 on a clean interactive termination (BYE/QUIT/EOF) and on successful one-shot runs; it exits 1 on fatal boot failures, failed preloads, failed imports or playbacks, and failed verifications; and it exits 2 when handed an unrecognized combination of command-line arguments.
