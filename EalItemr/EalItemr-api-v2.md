# EALITEMR .NET Library — Public API Reference (v2)

A managed library for reading, writing, importing and browsing IBM 4680/4690 GSA
`EALITEMR.DAT` keyed files. This is the **current** public API of `EalItemr.cs`
(successor to `EalItemr-api.md`, which described an earlier revision).

Every code example in this document was taken verbatim from a runnable project
under `ApiExamples/`, compiled with a **C# 2.0 language gate** (`<LangVersion>2</LangVersion>`),
and executed successfully against the library source before being committed.
The library itself is C# 2 / .NET 2.0-BCL compatible (no TPL, no LINQ, no P/Invoke;
concurrency uses only `Thread`, `Monitor`, `Interlocked`, `ManualResetEvent`).

- **Namespace:** `C3.EalItemr`
- **Source:** single file `EalItemr.cs`
- **Format guarantee:** byte-exact GSA keyed-file layout (512-byte blocks, 4-byte
  chain header + 10 × 50-byte slots, packed BCD keys, KFCB control block). Files
  written by this library open in the original C tooling and vice versa.

## Building

```bash
# Mono / .NET Framework (the intended target):
mcs -sdk:2.0 -target:library EalItemr.cs -out:EalItemr.dll

# Any modern SDK host (verification setup used for this document):
dotnet build <project>        # project links ..\EalItemr.cs with <LangVersion>2</LangVersion>
```

The `ApiExamples/` projects show the exact host-side pattern: an SDK-style exe that
compiles `..\..\EalItemr.cs` into itself under a C# 2 language gate.

## Conventions

### Async model

Every operation that does I/O or real work is exposed as an async call returning
`AsyncResult<T>`. Work runs on a background worker thread; the handle lets you block,
poll, or subscribe:

| Member | Meaning |
|---|---|
| `T Result { get; }` | Blocks until complete, then returns the result **or rethrows the worker exception**. |
| `void Wait()` | Blocks until complete. Does **not** rethrow — inspect `Error` afterwards. |
| `bool IsCompleted { get; }` | True once the worker finished (success or failure). |
| `Exception Error { get; }` | Worker exception, or null on success. |
| `int WorkerThreadId { get; }` | OS thread id of the worker that ran the operation. |
| `event AsyncCompletedEventHandler<T> Completed` | Fires on the worker thread with `AsyncCompletedEventArgs<T>` (`Error`, `Result`). |

```csharp
// from ApiExamples/ex16-async-patterns (verified output below)
AsyncResult<VerifyReport> op = file.VerifyAsync();
Console.WriteLine("before wait: IsCompleted=" + op.IsCompleted);   // False
VerifyReport v = op.Result;      // blocks here if still running
Console.WriteLine("after  : IsCompleted=" + op.IsCompleted          // True
    + " Error=" + (op.Error == null ? "null" : op.Error.Message)     // null
    + " workerThreadId=" + op.WorkerThreadId);                       // e.g. 4

// event style: Completed fires on the worker thread
AsyncResult<VerifyReport> op2 = file.VerifyAsync();
op2.Completed += delegate(object s, AsyncCompletedEventArgs<VerifyReport> e)
{
    Console.WriteLine("event: error=" + (e.Error == null ? "null" : e.Error.Message)
        + " total=" + e.Result.TotalRecords);
};
op2.Wait();   // keep Main alive until the worker finishes
```

Verified output (ex16):

```text
before wait: IsCompleted=False
after  : IsCompleted=True Error=null workerThreadId=4
result: total=0
event: error=null total=0
wait-only: completed=True error=null
```

### Persistence semantics

- **Default mode:** every mutating call persists immediately via an atomic
  same-directory temp-file + rename (`Flush(true)` before the rename). A crash at any
  point leaves either the old or the new complete file — never a torn one.
- **Session mode:** `BeginSession()` defers all writes to the in-memory image;
  `CommitSessionAsync()` persists everything in one atomic rewrite, and
  `RollbackSessionAsync()` discards it (re-reads disk). `Close()` auto-commits an
  open session so nothing is silently lost.

### Item codes

A code is either **1–22 decimal digits** (stored as zero-padded packed BCD) or an
**even-length hex byte string of at most 11 bytes** (right-justified). Codes are
compared by their stored key bytes, so `"7"`, `"000000000007"` and the hex form all
address the same record. Displayed codes (`ItemrRow.Code`, `EalItemrRecord.Code` on
read-back) are the canonical decimal digits with leading zeros stripped — e.g. code
`"000000000007"` reads back as `"7"`.

### Exceptions

All library exceptions derive from `EalItemrException : System.Exception`:

| Exception | Meaning | Thrown by (examples) |
|---|---|---|
| `FileFormatException` | Missing, too small, structurally invalid, or over-ceiling file. | `Open`, `OpenAsync`, `ExtendDatabase`, batch/record ops on a bad file |
| `FieldFormatException` | A record field (code, price, INDICAT2, dept, qty, MPG…) is out of the GSA rules. | `ParseKey`, `ParsePrice`, all write paths, batch validation |
| `RecordNotFoundException` | Declared for contract completeness; plain misses return null/false instead. | — |
| `RecordAlreadyExistsException` (`.Code`) | `AddRecordAsync` on an existing code; duplicate codes inside one `ImportDatabase` set. | `AddRecordAsync`, `ImportDatabase` |
| `FileFullException` | No free slot available for placement. | all write paths, batch tiers |
| `CapacityException` (`.BlocksNeeded`) | Requested size exceeds the u16 chain-pointer ceiling (65,535 data blocks). | `CreateNewDatabase`, `ExtendDatabase`, `ImportDatabase` |
| `BatchValidationException` (`.Errors[]`) | One or more batch rows are invalid; **all** bad rows reported, nothing placed. | all three batch tiers |

Plus standard .NET exceptions for argument problems: `ArgumentException`,
`ArgumentNullException`, `ArgumentOutOfRangeException`, `ObjectDisposedException`.

---

## Record model — `EalItemrRecord`

One 50-byte item record. Fields mirror the EALITEMR layout; write paths validate
them against the BCD/BCD-range rules (violations throw `FieldFormatException`).

| Field | Type | Constraint / meaning |
|---|---|---|
| `Code` | `string` | 1–22 digits, or even-length hex ≤ 11 bytes. Stored as packed BCD key. |
| `Indicat0` | `byte` | Flag byte 0 — see `ItemFlags`. Default `0x60` (qty + price required). |
| `Indicat1` | `byte` | Flag byte 1 — tax tables / stock number, see `ItemFlags`. Default `0`. |
| `Indicat2Validation` | `byte` | D/C/S validation class (INDICAT2 tens digit): 0–8. |
| `Indicat2Pricing` | `byte` | Pricing method (INDICAT2 units digit): 0–3 on write (4 = read-only). |
| `Department` | `string` | ≤ 6 decimal digits. |
| `SaleQuan` | `int` | Sale quantity in the deal, 0–99. |
| `SalePriceCents` | `long` | Primary sale price in cents (range depends on pricing method). |
| `SalePrice2Cents` | `long` | Secondary price: package (method 2) / reduced (method 3); must be 0 for methods 0/1. |
| `MpGroup` | `int` | Multi-price group number, 0–99. |
| `ItemName` | `string` | ≤ 18 chars; uppercased and padded on write. |
| `User` | `string` | ≤ 8 chars (user area). |

### `EalItemrRecord.CreateDefault(string code)` — static factory

New record with GSA-sensible defaults (`Indicat0 = QuantityRequired | PriceRequired`).

```csharp
// from ApiExamples/ex04-record-model
EalItemrRecord rec = EalItemrRecord.CreateDefault("030861007010");

rec.Indicat0 |= ItemFlags.NotReturnable;      // flag byte 0 (GSA Table 101)
rec.Indicat1 |= ItemFlags.TaxTable2;          // flag byte 1
rec.Indicat2Validation = 4;                   // D/C required (legal: 0-8)
rec.Indicat2Pricing    = 2;                   // method 2: unit + package price
rec.Department         = "101";               // <= 6 digits
rec.SaleQuan           = 2;                   // 0-99 (buy 2)
rec.SalePriceCents     = EalItemrFile.ParsePrice("1.00");   // unit price
rec.SalePrice2Cents    = EalItemrFile.ParsePrice("5.00");   // package price (m2)
rec.MpGroup            = 7;                   // 0-99
rec.ItemName           = "coke can 355ml";    // uppercased + padded on write
rec.User               = "ACME";              // <= 8 chars

Console.WriteLine("code   : " + rec.Code);          // 030861007010
Console.WriteLine("name   : " + rec.ItemName);      // coke can 355ml
Console.WriteLine("dept   : " + rec.Department);    // 101
Console.WriteLine("v/m    : " + rec.Indicat2Validation + "/" + rec.Indicat2Pricing);  // 4/2
Console.WriteLine("unit $ : " + EalItemrFile.FormatPrice(rec.SalePriceCents));        // 1.00
Console.WriteLine("pkg  $ : " + EalItemrFile.FormatPrice(rec.SalePrice2Cents));       // 5.00
```

### `ItemFlags` — static flag constants

| Constant | Value | Byte | Meaning |
|---|---|---|---|
| `MovementKept` | `0x80` | INDICAT0 | movement kept |
| `QuantityRequired` | `0x40` | INDICAT0 | quantity required |
| `PriceRequired` | `0x20` | INDICAT0 | price required |
| `NotAuthorizedSale` | `0x10` | INDICAT0 | not authorized for sale |
| `RestrictedHours` | `0x08` | INDICAT0 | restricted hours |
| `NotReturnable` | `0x04` | INDICAT0 | not returnable |
| `NotPricedGiveaway` | `0x02` | INDICAT0 | not priced / giveaway |
| `WeightPriceReq` | `0x01` | INDICAT0 | weight/price required |
| `TaxTable1` … `TaxTable4` | `0x80…0x10` | INDICAT1 | tax table selection |
| `StockNumberReq` | `0x08` | INDICAT1 | stock number required |

---

## Result value types

### `WriteResult` — outcome of `AddRecordAsync` / `WriteRecordAsync`

| Field | Meaning |
|---|---|
| `Record` | The record as stored (packed/normalized fields). |
| `Block` | Block number (1-based) where the record was placed. |
| `Slot` | Slot (0–9) within the block. |
| `Created` | True when a new slot was created, false when an existing record was replaced. |

### `ItemrRow` — one row from `ListRecordsAsync`

| Field | Meaning |
|---|---|
| `Code` | Canonical decimal code (leading zeros stripped). |
| `Name` | Item description. |
| `Department` | Department number string. |
| `PriceCents` | Primary sale price in cents. |
| `Price2Cents` | Secondary price: package (m2) / reduced (m3); 0 for methods 0/1. |
| `Validation` | D/C/S validation class (INDICAT2 tens digit). |
| `PricingMethod` | Pricing method 0–3, or null when the stored value is outside 0–3. |

### `RecordListResult` — result of `ListRecordsAsync`

| Field | Meaning |
|---|---|
| `Rows` | Rows returned (at most `limit`; limit 0 = all records). Block/slot scan order. |
| `Count` | Number of rows in `Rows`. |

### `ImportReport` — result of `ImportDatabase`

| Field | Meaning |
|---|---|
| `RecordsImported` | Records written to the new file. |
| `DataBlocks` | Data blocks in the new file (excluding the control block). |
| `MaxBucketCount` | Largest per-bucket record count (hotspot indicator). |

### `VerifyReport` — result of `VerifyAsync`

| Field | Meaning |
|---|---|
| `BlockCount`, `RecordSize`, `Divisor`, `KeyLen`, `ChainThreshold` | KFCB parameters as read from the file. |
| `TotalRecords` | All non-empty slots. |
| `HomeRecords` | Records sitting in their own home block. |
| `ChainedRecords` | Records reachable from their home's overflow chain. |
| `OrphanRecords` | Records with no link from their home block (should be 0). |
| `BcdRecords` / `RawKeyRecords` | Records with packed-BCD vs non-BCD keys. |
| `EmptySlots` | Free slots remaining. |

### `BatchError` — one invalid row of a rejected batch

| Field | Meaning |
|---|---|
| `Row` | **1-based** ordinal position in the input sequence. |
| `Code` | The row's item code (may be empty if the code itself was bad). |
| `Message` | Validation diagnostic for this row. |

Also has a human-readable `ToString()` override (`row N: [code] message`).

### `BatchReport` — outcome of a batch import

| Field | Meaning |
|---|---|
| `Rows` | Rows in the input sequence. |
| `Created` / `Updated` | New records created vs existing records overwritten (incl. intra-batch duplicates). |
| `ValidateMs`, `PlaceMs`, `CommitMs` | Per-phase durations in milliseconds (`CommitMs` 0 when deferred to a session). |
| `BytesWritten` | Bytes written by the batch's single commit (0 when deferred). |
| `Workers` | Worker threads used for validate/pack (1 = serial path). |
| `Placement` | `"serial"` or `"sharded"` — see the batch tier docs. |

---

## Browse support — `BrowseColumn`, `BrowseTable`

### `BrowseColumn` enum — sortable columns

`Code`, `ItemName`, `Department`, `SalePriceCents`, `SalePrice2Cents`, `SaleQuan`,
`MpGroup`, `Indicat0`, `Indicat1`, `Indicat2Validation`, `Indicat2Pricing`, `User`.
Comparison is ordinal (deterministic across machines and cultures).

### `BrowseTable` — in-memory browse table

Every record in the file decoded into RAM (block/slot scan order), with sort + filter
+ paging over a separate view state. The row store is immutable after construction;
`Sort`/`SetFilter` only rearrange the view, so re-sorting or re-filtering never
re-decodes and never touches disk. Intended for browse/edit UIs: load once, serve all
view operations from RAM. Not thread-safe against concurrent `Sort`/`SetFilter`; reads
are safe while no mutation is in flight.

| Member | Throws | Meaning |
|---|---|---|
| `int TotalRows { get; }` | — | Rows in the store (unfiltered). |
| `int Count { get; }` | — | Visible rows after the current filter. |
| `bool HasFilter { get; }` | — | True when a filter predicate is active. |
| `int Workers { get; }` | — | Worker threads used for extraction (1 = serial). |
| `double ExtractMs { get; }` | — | Extraction duration in milliseconds. |
| `EalItemrRecord Get(int index)` | `ArgumentOutOfRangeException` | Visible row at the given visible-index. |
| `int CopyToRange(int start, int count, EalItemrRecord[] buffer)` | `ArgumentNullException`, `ArgumentException`, `ArgumentOutOfRangeException` | Copies up to `count` visible rows starting at `start`; returns how many were copied (0 when past the end). |
| `void Sort(BrowseColumn column, bool ascending)` | — | Reorders the view by the given column. |
| `void SetFilter(Predicate<EalItemrRecord> predicate)` | — | Filters the visible rows; pass null to clear. |

```csharp
// from ApiExamples/ex07-browse (seed omitted: 25 records ITEM-01..ITEM-25, $1.00..$25.00)
BrowseTable t = file.BrowseAsync().Result;
Console.WriteLine("rows=" + t.TotalRows + " workers=" + t.Workers
    + " extractMs=" + t.ExtractMs.ToString("F0"));

t.Sort(BrowseColumn.SalePriceCents, false);   // price descending
Console.WriteLine("top by price: " + t.Get(0).ItemName
    + " $" + EalItemrFile.FormatPrice(t.Get(0).SalePriceCents));

t.SetFilter(delegate(EalItemrRecord r) { return r.SalePriceCents >= 200; });
Console.WriteLine("filtered: " + t.Count + " of " + t.TotalRows + " hasFilter=" + t.HasFilter);

// page through the visible rows (rendering stays host-side)
int page = 0, shown = 0;
while (shown < t.Count)
{
    EalItemrRecord[] buf = new EalItemrRecord[10];
    int n = t.CopyToRange(shown, 10, buf);
    for (int i = 0; i < n; i++)
        Console.WriteLine("p" + page + " " + buf[i].Code + " " + buf[i].ItemName);
    shown += n; page++;
}

t.SetFilter(null);   // clear the filter: all rows visible again
Console.WriteLine("after clear: count=" + t.Count + " hasFilter=" + t.HasFilter);
```

Verified output (ex07, trimmed):

```text
rows=25 workers=24 extractMs=3
top by price: ITEM-25 $25.00
filtered: 24 of 25 hasFilter=True
p0 75 ITEM-25
...
after clear: count=25 hasFilter=False
```

---

## `EalItemrFile` — the file API

### Constants

| Constant | Value | Meaning |
|---|---|---|
| `BlockSize` | 512 | Block size in bytes. |
| `SlotsPerBlock` | 10 | Record slots per data block. |
| `KeyLen` | 11 | Key length in bytes (packed BCD). |
| `RecordSize` | 50 | On-disk record size in bytes. |
| `NameLen`, `UserLen` | 18, 8 | Item name / user area lengths. |
| `CodeMaxDigits` | 12 | Max significant digits of a typical UPC-style code (the parser itself accepts up to `2 × KeyLen = 22` digits). |
| `PriceMaxCents` | 9,999,999,999 | Absolute maximum for `SalePriceCents`; per-pricing-method limits are stricter. |
| `Divisor` | 999 | Home-block hash divisor (remainder 0 maps to 999). |
| `MaxDataBlocks` | 65,535 | u16 chain-pointer ceiling: max data blocks per file (= 655,350 slots). |
| `MinRowsForParallelism` | 1,000 | Batches smaller than this fall back to the serial path in `ImportBatchParallelAsync`. |
| `DefaultIndicat0` | `0x60` | qty + price required. |

### Static methods

#### `EalItemrFile.Open(string path)` / `OpenAsync(string path)`

Opens an existing EALITEMR.DAT for read/write. `Open` blocks; `OpenAsync` runs on a
worker thread and returns `AsyncResult<EalItemrFile>`.

**Throws:** `ArgumentException` (null/empty path), `FileFormatException`
(missing, too small, structurally invalid, or over the 65,535-data-block ceiling).

```csharp
// from ApiExamples/ex01-open
string p = Path.Combine(Path.GetTempPath(), "ex01-open.DAT");
if (File.Exists(p)) File.Delete(p);
EalItemrFile.CreateNewDatabase(100, p);

using (EalItemrFile file = EalItemrFile.Open(p))          // using runs Close()/Dispose()
{
    Console.WriteLine("block size   : " + EalItemrFile.BlockSize);   // 512
    Console.WriteLine("slots/block  : " + EalItemrFile.SlotsPerBlock); // 10
    Console.WriteLine("key length   : " + EalItemrFile.KeyLen);      // 11
    Console.WriteLine("record size  : " + EalItemrFile.RecordSize);  // 50
    Console.WriteLine("hash divisor : " + EalItemrFile.Divisor);     // 999
}

AsyncResult<EalItemrFile> op = EalItemrFile.OpenAsync(p);
EalItemrFile file2 = op.Result;   // the opened file (blocks until done)
file2.Close();
```

Verified output (ex01): `opened … / block size: 512 / slots/block: 10 / key length: 11 /
record size: 50 / hash divisor: 999 / async open ok`.

#### `EalItemrFile.CreateNewDatabase(int maxRecords, string path)` / `CreateNewDatabaseAsync(...)`

Creates a new empty database sized for at least `maxRecords` records (floored at
1,000 data blocks so the home area is always complete). Replaces any existing file.

**Throws:** `ArgumentException`, `CapacityException` when the size would exceed
65,535 data blocks, `IOException`.

```csharp
// from ApiExamples/ex02-create
string p1 = Path.Combine(Path.GetTempPath(), "ex02-sync.DAT");
if (File.Exists(p1)) File.Delete(p1);
EalItemrFile.CreateNewDatabase(5000, p1);
Console.WriteLine("sync  : " + new FileInfo(p1).Length + " bytes, "
    + EalItemrFile.CountRecordSlots(p1) + " slots");

AsyncResult<int> op = EalItemrFile.CreateNewDatabaseAsync(5000, p2);
int slots = op.Result;   // number of record slots in the new file
Console.WriteLine("async : " + slots + " record slots");
```

Verified output (ex02): `sync: 512512 bytes, 10000 slots / async: 10000 record slots`.

#### `EalItemrFile.ExtendDatabase(int additionalSlots, string path)` — static

Appends capacity to an existing file. **Result = the NEW TOTAL slot count.**
**Throws:** `ArgumentException`, `FileFormatException` (bad/missing file),
`CapacityException` (would exceed 65,535 data blocks).

#### `ExtendDatabaseAsync(int additionalSlots)` — instance → `AsyncResult<int>`

Same operation on an open file; also refreshes the in-memory image so new capacity is
immediately usable. **Throws:** as above plus `ObjectDisposedException`.

```csharp
// from ApiExamples/ex14-extend (file created with CreateNewDatabase(100, p) first)
int before = EalItemrFile.CountRecordSlots(p);   // 10,000 (floor)

// Static form: background thread; Result = NEW TOTAL slot count.
AsyncResult<int> op = EalItemrFile.ExtendDatabase(500, p);
int afterStatic = op.Result;
Console.WriteLine("static : " + before + " -> " + afterStatic + " slots");   // 10000 -> 10500

// Instance form: also refreshes the in-memory image of an open file.
using (EalItemrFile file = EalItemrFile.Open(p))
{
    int before2 = file.CountRecordSlotsAsync().Result;
    int after2  = file.ExtendDatabaseAsync(500).Result;
    Console.WriteLine("instance: " + before2 + " -> " + after2 + " slots");   // 10500 -> 11000

    EalItemrRecord rec = EalItemrRecord.CreateDefault("000000000123");
    WriteResult w = file.AddRecordAsync(rec).Result;   // new capacity immediately usable
    Console.WriteLine("add into extended file: ok, used=" + file.CountUsedSlotsAsync().Result);  // used=1
}
```

Verified output (ex14): `static : 10000 -> 10500 slots / instance: 10500 -> 11000 slots /
add into extended file: ok, used=1`.

#### `EalItemrFile.ImportDatabase(IEnumerable<EalItemrRecord> records, string path)` — static

Planned-layout bulk import into a **new** file: one streaming write pass with exact
chains (no overflow churn), then a self-check. Replaces any existing file at `path`.

**Throws:** `ArgumentNullException`, `ArgumentException` (empty set),
`FieldFormatException` (invalid record), `RecordAlreadyExistsException` (duplicate code
in the set), `CapacityException` (planned layout exceeds 65,535 data blocks),
`EalItemrException` (self-check failure).

```csharp
// from ApiExamples/ex13-import-database
string p = Path.Combine(Path.GetTempPath(), "ex13-import.DAT");
if (File.Exists(p)) File.Delete(p);   // ImportDatabase replaces any existing file

EalItemrRecord[] recs = new EalItemrRecord[500];
for (int i = 0; i < recs.Length; i++)
{
    recs[i] = EalItemrRecord.CreateDefault((i + 1).ToString("D12"));
    recs[i].ItemName       = "IMPORTED " + (i + 1);
    recs[i].SalePriceCents = (i % 900) + 100;   // $1.00-$9.99
}

ImportReport rep = EalItemrFile.ImportDatabase(recs, p).Result;
Console.WriteLine("imported=" + rep.RecordsImported + " dataBlocks=" + rep.DataBlocks
    + " maxBucket=" + rep.MaxBucketCount);      // imported=500 dataBlocks=999 maxBucket=2

using (EalItemrFile file = EalItemrFile.Open(p))
{
    VerifyReport v = file.VerifyAsync().Result;
    Console.WriteLine("verify: total=" + v.TotalRecords + " home=" + v.HomeRecords
        + " chained=" + v.ChainedRecords + " orphan=" + v.OrphanRecords);  // 500/500/0/0

    EalItemrRecord extra = EalItemrRecord.CreateDefault("999999999901");
    extra.ItemName = "ADDED AFTER IMPORT";
    WriteResult w = file.AddRecordAsync(extra).Result;   // online adds work normally
}
```

#### `EalItemrFile.CountRecordSlots(string path)` / `CountRecordSlotsAsync(string path)` — static

Total record slots in the file (data blocks × 10). Sync and async forms.
**Throws:** `ArgumentException`, `FileFormatException`.

#### `EalItemrFile.CountUsedSlots(string path)` / `CountUsedSlotsAsync(string path)` — static

Non-empty slots in the file. Sync and async forms.
**Throws:** `ArgumentException`, `FileFormatException`.

#### `CountRecordSlotsAsync()` / `CountUsedSlotsAsync()` — instance → `AsyncResult<int>`

Same two queries on an open file (no path argument).
**Throws:** `ObjectDisposedException`.

```csharp
// from ApiExamples/ex15-counts (7 records seeded first)
int totalSync = EalItemrFile.CountRecordSlots(p);      // 10000
int usedSync  = EalItemrFile.CountUsedSlots(p);        // 7
Console.WriteLine("static sync : " + usedSync + "/" + totalSync + " slots in use");

int totalAsync = EalItemrFile.CountRecordSlotsAsync(p).Result;
int usedAsync  = EalItemrFile.CountUsedSlotsAsync(p).Result;
Console.WriteLine("static async: " + usedAsync + "/" + totalAsync);

using (EalItemrFile file = EalItemrFile.Open(p))       // instance forms: no path argument
{
    int itotal = file.CountRecordSlotsAsync().Result;
    int iused  = file.CountUsedSlotsAsync().Result;
    Console.WriteLine("instance    : " + iused + "/" + itotal);   // 7/10000
}
```

Verified output (ex15): `static sync: 7/10000 / static async: 7/10000 / instance: 7/10000`.

#### `EalItemrFile.ParseKey(string code)` — static

Parses a code into its 11-byte key (digits → packed BCD; even-length hex → bytes,
right-justified). **Throws:** `FieldFormatException` for empty/too long/invalid codes.

```csharp
// from ApiExamples/ex03-helpers
byte[] k1 = EalItemrFile.ParseKey("030861007010");   // UPC-12, BCD  -> Length 11
byte[] k2 = EalItemrFile.ParseKey("DEADBEEF");       // 4 hex bytes  -> Length 11
```

#### `EalItemrFile.ParsePrice(string s)` — static

Parses a price to cents: raw cents (`"1299"`) or dollars with 1–2 decimals (`"12.99"`).
**Throws:** `FieldFormatException`.

```csharp
// from ApiExamples/ex03-helpers
long c1 = EalItemrFile.ParsePrice("12.99");   // 1299
long c2 = EalItemrFile.ParsePrice("1299");    // 1299
```

#### `EalItemrFile.FormatPrice(long cents)` — static

Formats cents as `"d.dd"` (the inverse of the dollars form). No exceptions.

```csharp
// from ApiExamples/ex03-helpers
Console.WriteLine("FormatPrice(1299) = \"" + EalItemrFile.FormatPrice(1299) + "\"");  // "12.99"
```

### Instance methods

#### `GetRecordAsync(string code)` → `AsyncResult<EalItemrRecord?>`

Reads one record by code. **Result is null on a plain miss** (no exception).
**Throws:** `FieldFormatException` when the code string itself is unparseable,
`ObjectDisposedException` after close.

```csharp
// from ApiExamples/ex05-crud
EalItemrRecord? hit  = file.GetRecordAsync("030861007010").Result;   // found
EalItemrRecord? miss = file.GetRecordAsync("999999999999").Result;   // null
Console.WriteLine("get: " + (hit.HasValue ? hit.Value.ItemName : "null")
    + " / miss=" + (miss == null ? "null" : "value"));   // get: COKE CAN 355ML / miss=null
```

#### `ListRecordsAsync(int limit)` → `AsyncResult<RecordListResult>`

Lists records in block/slot scan order (physical, not sorted). Limit 0 = all.
**Throws:** `ObjectDisposedException`.

```csharp
// from ApiExamples/ex06-list-verify (5 records seeded first)
RecordListResult lr = file.ListRecordsAsync(3).Result;   // first 3 rows
for (int i = 0; i < lr.Count; i++)
{
    ItemrRow row = lr.Rows[i];
    Console.WriteLine("row: " + row.Code + " " + row.Name
        + " dept=" + row.Department
        + " v=" + row.Validation
        + " m=" + (row.PricingMethod.HasValue ? row.PricingMethod.ToString() : "-")
        + " $" + EalItemrFile.FormatPrice(row.PriceCents));
}

VerifyReport v = file.VerifyAsync().Result;
Console.WriteLine("verify: blocks=" + v.BlockCount + " total=" + v.TotalRecords
    + " home=" + v.HomeRecords + " chained=" + v.ChainedRecords
    + " orphan=" + v.OrphanRecords + " empty=" + v.EmptySlots);
```

Verified output (ex06): `row: 7 ITEM 1 dept=0 v=0 m=0 $1.99 / … / verify: blocks=1001
total=5 home=5 chained=0 orphan=0 empty=9995`. Note the code displays as `"7"` —
leading zeros are stripped on read-back (see *Item codes* above).

#### `BrowseAsync()` / `BrowseAsync(int workerCount)` → `AsyncResult<BrowseTable>`

Decodes every record into an in-memory browse table. Extraction is parallel over
contiguous block ranges; K ≤ 1 or a small file falls back to the serial path. The
result is a RAM snapshot: later mutations do not change it. See *Browse support* for
the full `BrowseTable` reference and example.
**Throws:** `ArgumentException` (workerCount < 1), `ObjectDisposedException`.

#### `AddRecordAsync(EalItemrRecord rec)` → `AsyncResult<WriteResult>`

Adds a new record. **Fails if the code already exists.** Persists immediately unless
in session mode.
**Throws:** `FieldFormatException`, `RecordAlreadyExistsException` (`.Code`),
`FileFullException`, `ObjectDisposedException`.

```csharp
// from ApiExamples/ex05-crud
EalItemrRecord rec = EalItemrRecord.CreateDefault("030861007010");
rec.ItemName       = "COKE CAN 355ML";
rec.Department     = "101";
rec.SalePriceCents = EalItemrFile.ParsePrice("1.29");
WriteResult w = file.AddRecordAsync(rec).Result;
Console.WriteLine("add: block=" + w.Block + " slot=" + w.Slot + " created=" + w.Created);
// add: block=636 slot=0 created=True
```

#### `WriteRecordAsync(EalItemrRecord rec)` → `AsyncResult<WriteResult>`

Create-or-replace (upsert): writes the record whether or not the code exists.
`Created` is false when replacing.
**Throws:** `FieldFormatException`, `FileFullException`, `ObjectDisposedException`.

```csharp
// from ApiExamples/ex05-crud
EalItemrRecord r = hit.Value;                       // fetched via GetRecordAsync
r.SalePriceCents = EalItemrFile.ParsePrice("1.19");
WriteResult w2 = file.WriteRecordAsync(r).Result;
Console.WriteLine("write: created=" + w2.Created);  // write: created=False
```

#### `DeleteRecordAsync(string code)` → `AsyncResult<bool>`

Deletes the record with this code. **Result is false when the code was not in the
file** (no exception). Persists immediately unless in session mode.
**Throws:** `FieldFormatException` (unparseable code), `ObjectDisposedException`.

```csharp
// from ApiExamples/ex05-crud
bool d1 = file.DeleteRecordAsync("030861007010").Result;   // True
bool d2 = file.DeleteRecordAsync("999999999999").Result;   // False (missing)
Console.WriteLine("delete: " + d1 + " / missing=" + d2);    // delete: True / missing=False
```

#### `ImportBatchAsync(IEnumerable<EalItemrRecord> rows)` → `AsyncResult<BatchReport>`

Serial batch import — the reference tier. Validates and packs every row first
(**collect-all-then-refuse**: any invalid row rejects the whole batch with a complete,
row-sorted error list; nothing is placed or committed), then places all rows in input
order and commits once. Duplicates (intra-batch or against existing records) upsert:
the later bytes win.

**Throws:** `ArgumentNullException`, `ArgumentException` (empty set),
`BatchValidationException` (`.Errors[]` — every bad row, 1-based `Row`),
`FileFullException` (capacity; file left byte-identical).

```csharp
// from ApiExamples/ex08-batch-serial
EalItemrRecord[] rows = new EalItemrRecord[6];
for (int i = 0; i < 5; i++)
{
    rows[i] = EalItemrRecord.CreateDefault((i + 1).ToString("D12"));
    rows[i].ItemName       = "BATCH ITEM " + (i + 1);
    rows[i].SalePriceCents = EalItemrFile.ParsePrice(i.ToString() + ".50");
}
rows[5] = EalItemrRecord.CreateDefault("000000000002");   // same code as row 2 (upsert)
rows[5].SalePriceCents = EalItemrFile.ParsePrice("9.99");

BatchReport rep = file.ImportBatchAsync(rows).Result;
Console.WriteLine("rows=" + rep.Rows + " created=" + rep.Created + " updated=" + rep.Updated);
// rows=6 created=5 updated=1
Console.WriteLine("placement=" + rep.Placement);           // serial
```

Failure path (from `ApiExamples/ex09-batch-fail`):

```csharp
rows[1].Indicat2Pricing = 7;              // illegal: must be 0-3 on write
rows[2].Code = "12G45";                   // non-digit code
try { BatchReport rep = file.ImportBatchAsync(rows).Result; }
catch (BatchValidationException ex)
{
    Console.WriteLine("BatchValidationException: " + ex.Errors.Length + " error(s)");
    for (int i = 0; i < ex.Errors.Length; i++)
        Console.WriteLine("  row " + ex.Errors[i].Row + ": [" + ex.Errors[i].Code + "] " + ex.Errors[i].Message);
}
```

Verified output (ex09): `BatchValidationException: 2 error(s) / row 2: [000000000002]
indic2 pricing must be 0-3 / row 3: [12G45] code is neither all digits nor a hex byte
string: 12G45` — and `used slots before/after: 0/0`, i.e. nothing was placed.

#### `ImportBatchParallelAsync(rows)` / `ImportBatchParallelAsync(rows, int workerCount)` → `AsyncResult<BatchReport>`

Same semantics as the serial tier with the **validate/pack front half parallelized**
across K workers (default K = `Environment.ProcessorCount`). Placement and commit stay
serial in input order on the exact same code path — so output is byte-comparable to
the serial tier. Batches smaller than `MinRowsForParallelism` (1,000) or K ≤ 1 fall
back to the serial path (`Workers == 1`).

**Throws:** as `ImportBatchAsync`, plus `ArgumentException` (workerCount < 1).

```csharp
// from ApiExamples/ex10-batch-parallel (5000 rows prepared first)
BatchReport r1 = file.ImportBatchParallelAsync(rows).Result;      // default K
Console.WriteLine("default K: workers=" + r1.Workers + " created=" + r1.Created
    + " placement=" + r1.Placement);   // workers=24 created=5000 placement=serial

for (int i = 0; i < rows.Length; i++) rows[i].SalePriceCents = 999;
BatchReport r2 = file.ImportBatchParallelAsync(rows, 4).Result;   // explicit K=4
Console.WriteLine("K=4      : workers=" + r2.Workers + " updated=" + r2.Updated
    + " placement=" + r2.Placement);   // workers=4 updated=5000 placement=serial

VerifyReport v = file.VerifyAsync().Result;
Console.WriteLine("verify: total=" + v.TotalRecords + " orphan=" + v.OrphanRecords);  // 5000/0
```

Note `placement` is `"serial"` by design for this tier (the placement phase runs
serially; parallelism is in validate/pack, reflected in `Workers`).

#### `ImportBatchShardedAsync(rows)` / `ImportBatchShardedAsync(rows, int workerCount)` → `AsyncResult<BatchReport>`

The fastest tier: **placement itself is sharded by home bucket** — each shard
exclusively owns a contiguous range of homes (home block + full overflow chain) plus
its own slice of the pristine pool sized exactly by a prior demand pass, so the
parallel phase has zero shared mutable state and capacity is checked before any
mutation. Same collect-all-then-refuse validation; same upsert semantics.

Output is **deterministic for a fixed (input, K)** but NOT byte-identical to the
serial tier's file (overflow blocks come from different pool slices) — compare tiers
semantically (`VerifyAsync`: same key set and values, zero orphans), not bytewise.

**Throws:** as `ImportBatchAsync`, plus `ArgumentException` (workerCount < 1).

```csharp
// from ApiExamples/ex11-batch-sharded (5000 rows prepared first)
BatchReport r1 = file.ImportBatchShardedAsync(rows, 4).Result;    // explicit K=4
Console.WriteLine("K=4      : workers=" + r1.Workers + " created=" + r1.Created
    + " placement=" + r1.Placement);   // workers=4 created=5000 placement=sharded

for (int i = 0; i < rows.Length; i++) rows[i].SalePriceCents = 999;
BatchReport r2 = file.ImportBatchShardedAsync(rows).Result;       // default K
Console.WriteLine("default K: workers=" + r2.Workers + " updated=" + r2.Updated
    + " placement=" + r2.Placement);   // workers=24 updated=5000 placement=sharded

VerifyReport v = file.VerifyAsync().Result;
Console.WriteLine("verify: total=" + v.TotalRecords + " orphan=" + v.OrphanRecords);  // 5000/0
```

#### `VerifyAsync()` → `AsyncResult<VerifyReport>`

Hash/layout consistency summary (see `VerifyReport`). A healthy file has
`OrphanRecords == 0`. **Throws:** `ObjectDisposedException`.

```csharp
// from ApiExamples/ex06-list-verify
VerifyReport v = file.VerifyAsync().Result;
Console.WriteLine("verify: blocks=" + v.BlockCount + " total=" + v.TotalRecords
    + " home=" + v.HomeRecords + " chained=" + v.ChainedRecords
    + " orphan=" + v.OrphanRecords + " empty=" + v.EmptySlots);
```

#### `BeginSession()` — synchronous

Defers all subsequent writes to the in-memory image (no disk I/O per record).
**Throws:** `ObjectDisposedException`.

#### `CommitSessionAsync()` → `AsyncResult<long>`

Persists everything since `BeginSession()` in one atomic temp+rename rewrite.
Returns bytes written (0 when nothing changed). Ends the session.
**Throws:** `EalItemrException` ("no active session").

#### `RollbackSessionAsync()` → `AsyncResult<bool>`

Discards all changes since `BeginSession()`: re-reads the on-disk file and rebuilds
the index. **Throws:** `EalItemrException` ("no active session").

```csharp
// from ApiExamples/ex12-session
file.BeginSession();
long lenBefore = new FileInfo(p).Length;
EalItemrRecord rec = EalItemrRecord.CreateDefault("000000000042");
rec.ItemName = "SESSION ITEM";
WriteResult w = file.AddRecordAsync(rec).Result;   // RAM only - disk untouched
Console.WriteLine("disk len unchanged during session: " + (new FileInfo(p).Length == lenBefore));  // True

long bytes = file.CommitSessionAsync().Result;     // one atomic rewrite
Console.WriteLine("commit wrote " + bytes + " bytes");                       // commit wrote 512512 bytes
Console.WriteLine("record now on disk: " + (file.GetRecordAsync("000000000042").Result != null));   // True

file.BeginSession();
EalItemrRecord rec2 = EalItemrRecord.CreateDefault("000000000099");
rec2.ItemName = "DOES NOT SURVIVE";
WriteResult w2 = file.AddRecordAsync(rec2).Result;
bool rolledBack = file.RollbackSessionAsync().Result;
Console.WriteLine("rollback ok: " + rolledBack);                                  // True
Console.WriteLine("rolled-back record present? " + (file.GetRecordAsync("000000000099").Result != null));  // False
```

#### `Close()` / `Dispose()` — synchronous

Waits for in-flight operations, **auto-commits an open session** (nothing is silently
lost), and releases the file. Idempotent; safe to call from a `using` block. No exceptions.

```csharp
// from ApiExamples/ex01-open
using (EalItemrFile file = EalItemrFile.Open(p)) { /* … */ }   // Close()/Dispose() runs here
```

---

## Exception reference — worked examples

From `ApiExamples/ex17-exceptions` (all verified):

| Exception | Trigger in the example | Verified message |
|---|---|---|
| `FileFormatException` | `Open` on a missing file | `cannot open C:\…\no-such-file.DAT` |
| `FieldFormatException` | `ParsePrice("12.345")` (3 decimals) | `bad price: 12.345` |
| `RecordAlreadyExistsException` | second `AddRecordAsync` of the same code | `code=000000000001` (via `.Code`) |
| `FieldFormatException` | record with `Indicat2Validation = 9` on write | `indic2 validation must be 0-8` |
| `FileFullException` | 12,000-row batch into a minimal 10,000-slot file | `batch import failed at row 6708 of 12000: file full: no free slot available` |
| `CapacityException` | `CreateNewDatabase(700000)` (70,000 data blocks > 65,535) | `database needs 70000 data blocks but the u16 chain pointer ceiling is 65535` (`BlocksNeeded=70000`) |

```csharp
// from ApiExamples/ex17-exceptions (abridged)
try { EalItemrFile.Open(Path.Combine(dir, "no-such-file.DAT")).Close(); }
catch (FileFormatException ex) { Console.WriteLine("FileFormatException: " + ex.Message); }

try { long c = EalItemrFile.ParsePrice("12.345"); }
catch (FieldFormatException ex) { Console.WriteLine("FieldFormatException: " + ex.Message); }

// RecordAlreadyExistsException / FieldFormatException on write — see the full project
// FileFullException: minimal file (999 homes + 1 pool block = 10,000 slots)
EalItemrFile.CreateNewDatabase(1, pf);   // minimal size
using (EalItemrFile file = EalItemrFile.Open(pf))
{
    EalItemrRecord[] rows = new EalItemrRecord[12000];
    for (int i = 0; i < rows.Length; i++)
        rows[i] = EalItemrRecord.CreateDefault((i + 1).ToString("D12"));
    try { BatchReport rep = file.ImportBatchAsync(rows).Result; }
    catch (FileFullException ex) { Console.WriteLine("FileFullException: " + ex.Message); }
}

// CapacityException: above the u16 chain-pointer ceiling
try { EalItemrFile.CreateNewDatabase(700000, pc); }
catch (CapacityException ex)
{ Console.WriteLine("CapacityException: " + ex.Message + " blocksNeeded=" + ex.BlocksNeeded); }
```

---

## Example index — `ApiExamples/`

Every project compiles the library source under a C# 2 language gate and was run to
completion successfully. Run any one with `dotnet run` inside its folder (or build
with `mcs -sdk:2.0` on Mono).

| Project | Covers | Verified result highlights |
|---|---|---|
| `ex01-open` | `Open`, `OpenAsync`, constants, `Close`/`Dispose` | 512-byte blocks, 999 divisor, async open ok |
| `ex02-create` | `CreateNewDatabase(Async)` | 512,512 bytes / 10,000 slots (floor) |
| `ex03-helpers` | `ParseKey`, `ParsePrice`, `FormatPrice` | `"12.99"` ↔ 1299; BCD + hex keys → 11 bytes |
| `ex04-record-model` | `EalItemrRecord` fields, `CreateDefault`, `ItemFlags` | all fields round-trip in memory |
| `ex05-crud` | `Add/Get/Write/DeleteRecordAsync`, `WriteResult` | add block=636 slot=0; upsert created=False; delete True/False |
| `ex06-list-verify` | `ListRecordsAsync`, `VerifyReport` | 5 rows, orphan=0, empty=9995 |
| `ex07-browse` | `BrowseAsync`, full `BrowseTable` tour | sort/filter/paging over 25 rows; workers=24 |
| `ex08-batch-serial` | `ImportBatchAsync`, `BatchReport` | created=5 updated=1, placement=serial |
| `ex09-batch-fail` | `BatchValidationException.Errors[]` | 2 row-sorted errors; file untouched (0/0 slots) |
| `ex10-batch-parallel` | `ImportBatchParallelAsync(×2)` | workers=24 / K=4, placement=serial, orphan=0 |
| `ex11-batch-sharded` | `ImportBatchShardedAsync(×2)` | placement=sharded, total=5000, orphan=0 |
| `ex12-session` | `BeginSession`, `Commit/RollbackSessionAsync` | disk untouched in session; commit 512,512 B; rollback discards |
| `ex13-import-database` | `ImportDatabase`, `ImportReport` | 500 records / 999 blocks / maxBucket=2; online add ok |
| `ex14-extend` | `ExtendDatabase` static + instance | 10,000 → 10,500 → 11,000 slots |
| `ex15-counts` | all six count members | 7/10,000 in use across sync/async/static/instance |
| `ex16-async-patterns` | `AsyncResult`: `Result`, `Wait`, `IsCompleted`, `Error`, `WorkerThreadId`, `Completed` | worker thread id 4; event + wait patterns |
| `ex17-exceptions` | all five domain exceptions with real triggers | messages as in the table above |
