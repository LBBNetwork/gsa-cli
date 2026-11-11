# CSVToolkit — Design Decisions

Decision log for the CSV library (`CSVToolkit/`, namespace `C3.CSVToolkit`).
Companion to the root `DECISIONS.md` (gsa-cli decisions, D1–D58); entries here
are numbered **CSV-n** so cross-references are unambiguous. Convention follows
the root log: Question → Decision, owner rulings marked as such.

Context anchors:
- D39 — sample.csv is the reference schema; the external CSV library is final
  authority on the file format; EXPORT/IMPORT must be strict inverses.
- D20 — EXPORT syntax + overwrite protection (gsa-cli side).
- Phase 5f / D55–D57 — gsa-cli's memory discipline: ~400 MB working set for a
  600k-record database on .NET 2.0; the library must not undo it.
- Target runtime: C# 2 / .NET 2.0 (Mono-compatible), Windows XP → 11, POSIX;
  32-bit hosts with ~2 GB user-mode ceiling are in scope.

## Reference facts (measured 2026-11-10)

`sample.csv` (the reference schema):
- UTF-8 **with BOM**, **CRLF** line terminators, **no trailing newline** after the last row.
- Header + one data row; 19 columns:
  `ITEM_CODE, ITEM_NAME, DEPT, SALEPRICECENTS, SALEPRICE2, QTY, TRACK_MOVEMENT, REQUIRE_QTY, REQUIRE_PRICE, SELL_HOURS, SELL_RESTRICTED, NO_RETURN, GIVEAWAY, TAX1, TAX2, TAX3, TAX4, PRICE_METHOD, DCS_METHOD`
- Flags are `Y`/`N`; prices raw integer cents; no quoted fields.

`sample_massive_alt.csv` (older schema, parallelization-testing era):
- UTF-8 with BOM, **LF-only** terminators, no trailing newline; 3505 data rows (codes 1300–4804).
- 18 columns — **no SALEPRICE2**; three headers carry stray-zero typos:
  `SALEPRICECE0TS`, `TRACK_MOVEME0T`, `REQUIRE_QT0`.
- Every flag is `0`; QTY / PRICE_METHOD / DCS_METHOD all `0`; names are `CSV <n>`.

---

## Decision log (status per entry)

### CSV-1. Scope of the library: general-purpose CSV engine or GSA-schema-aware?

**Question:** Is `C3.CSVToolkit` a generic CSV parse/serialize library (rows of
strings, header as data), with all GSA schema knowledge (column names → record
fields, Y/N ↔ flag bits, cents parsing) staying in gsa-cli's bridge — or does the
library itself know the sample.csv schema and hand back typed item records?

**Recommendation:** General-purpose. D39's swap-in promise ("swap the
implementation behind the bridge without touching command logic") only holds if
the library speaks plain CSV; a GSA-aware library would couple it to one
consumer and re-couple every future schema delta (D4-style field additions) into
a second codebase. The existing `ICsvEngine` seam (`ReadRows`/`WriteRows`,
positional string rows) is already schema-agnostic, which supports this shape.

**Decision (owner, 2026-11-10):** **General-purpose.** Ruled in favour of the recommendation; the owner also wants the library reusable for projects outside gsa-cli — no GSA schema knowledge may leak into `C3.CSVToolkit`.

### CSV-2. API shape and memory model: eager lists or streaming reader/writer?

**Question:** The current seam returns `List<CsvRow>` for the whole file. A
600k-row EXPORT/IMPORT file would materialize ~600k rows × 19 strings on top of
gsa-cli's already-bounded ~400 MB working set (D55–D57) — a real OOM risk on
32-bit hosts. Should the library expose streaming primitives instead?

**Recommendation:** Streaming core: `CsvReader` (open → header → pull rows one
at a time, line-numbered, close) and `CsvWriter` (open → write header → push
rows → close), both over buffered streams so only one row is live at a time.
Keep small convenience wrappers (`ReadAll`/`WriteAll`) for tests and small
files. The gsa-cli bridge adapts: EXPORT streams SELECT rows straight into the
file (also removes a latent Phase 5d memory spike); IMPORT pulls row-by-row
into the upsert path. .NET 2.0 has `IEnumerable<T>` if pull-based iteration is
preferred over an explicit reader object — either shape satisfies "one row live".

**Decision (owner, 2026-11-10):** **Streaming is fine.** Ruled in favour of the recommendation. Implementation details are further constrained by CSV-8 (operating model): streaming bounds must hold *under* parallelism, not just in a single-threaded reader.

### CSV-3. Format authority: line endings, BOM, trailing newline

**Question:** What exactly does the library accept on read and emit on write?

**Recommendation (byte-exact to sample.csv):**
- **Read:** tolerate UTF-8 BOM; accept CRLF *and* LF terminators (both workspace
  files exist in the wild); no phantom row after a final terminator.
- **Write:** UTF-8 with BOM, CRLF between rows, **no trailing newline** — so an
  EXPORT of one record reproduces sample.csv's byte layout and round-trips are
  byte-identical (D20/D39 inverse requirement).

**Decision (owner, 2026-11-10):** Approved as recommended — "keep exact to sample.csv": read tolerates BOM + CRLF/LF; write emits BOM + CRLF, no trailing newline.

### CSV-4. Quoting, strictness, and the error model

**Question:** How much RFC 4180 do we implement, and what is a hard error?

**Recommendation:**
- Parse quoted fields (double-quote, `""` escape) so well-formed third-party
  files import; writer quotes **only when necessary** (value contains `,`, `"`,
  CR or LF) — GSA data never needs quoting, so output stays byte-minimal.
- **Reject** CR/LF inside quoted fields with a line-numbered error: D39's
  contract is one record per row; multi-line rows would break line numbering in
  IMPORT reports and the strict-inverse guarantee.
- Header required (first row); field-count mismatch on any data row → hard
  error citing the line number (no silent pad/truncate).
- Errors surface as a typed exception (`CsvException` carrying the offending
  line) — .NET 2.0 idiom; gsa-cli maps it to its `<Origin> <message>:` layer
  (D31) and, for IMPORT, to per-row rejection with row numbers (D44).

**Decision (owner, 2026-11-10):** Approved as recommended — parse quoted fields; writer quotes only when necessary; reject CR/LF inside quotes; header required; field-count mismatch is a hard line-numbered error; typed `CsvException` carries the offending line.

### CSV-5. Value fidelity: encoding, whitespace, empty fields

**Question:** Does the library normalize anything?

**Recommendation:** No. UTF-8 only (decode failure → line-numbered error);
values are preserved byte-for-byte between read and write (no trimming, no
case-folding, no `0`/`N` conversion) — normalization is gsa-cli's job per D7/D12
and the strict-inverse requirement. Empty fields are legal at the library level;
whether a GSA record tolerates them is an importer-validation question.

**Decision (owner, 2026-11-10):** Approved as recommended — zero normalization in the library; UTF-8 only with line-numbered decode errors; byte-for-byte value fidelity.

### CSV-6. Migrating sample_massive_alt.csv to the reference schema

**Question:** The old 3505-row file must be brought onto the sample.csv schema
to stay useful as a multi-row test fixture. What are the mapping rules and where
does the result go?

**Recommendation:**
- Header: fix the three stray-zero typos (`SALEPRICECE0TS` → `SALEPRICECENTS`,
  `TRACK_MOVEME0T` → `TRACK_MOVEMENT`, `REQUIRE_QT0` → `REQUIRE_QTY`).
- Insert `SALEPRICE2` after `SALEPRICECENTS`; value `0` for every row (matches
  sample.csv's convention when PRICE_METHOD is 0).
- Flag columns (`TRACK_MOVEMENT`, `REQUIRE_QTY`, `REQUIRE_PRICE`,
  `SELL_HOURS`, `SELL_RESTRICTED`, `NO_RETURN`, `GIVEAWAY`, `TAX1..TAX4`
  — the Y/N columns in sample.csv's data row): map `0` → `N`, `1` → `Y`
  (all current values are `0`, so they become all-`N`).
- Numeric columns (`QTY`, `PRICE_METHOD`, `DCS_METHOD` — plain digits in
  sample.csv's data row) pass through unchanged, staying `0`.
- Emit per CSV-3 (BOM + CRLF, no trailing newline) as a **new** file
  (`sample_massive.csv`) — leave the alt file in place as the historical
  artifact. Do it with the new library itself: first real dogfood of reader +
  writer at 3505 rows.

**Decision (owner, 2026-11-10):** Approved as recommended — typo fixes, `SALEPRICE2` = `0`, flags `0`→`N` / `1`→`Y`, output to new file `sample_massive.csv` per CSV-3 byte layout, alt file preserved, migration performed with the new library.

### CSV-7. Build, packaging, and test strategy

**Question:** How does the library join the build, and how is it verified before
the Phase 6 harness exists?

**Recommendation:** Follow the EalItemr/Xml2 pattern exactly: `CSVToolkit/*.cs`
compiles to `bin/CSVToolkit.dll` in both build.bat (v2.0 csc) and build.sh
(mcs -sdk:2.0), namespace `C3.CSVToolkit`, zero dependencies, .NET 2.0 API
surface only. Verification now: a small standalone round-trip harness under
Tools/ (parse sample.csv → re-emit → byte-compare; parse the migrated 3505-row
file → re-emit → byte-compare), feeding the Phase 6 suite later. The gsa-cli
bridge swap-in itself stays scheduled for Phase 5/6b per PROJECT.md.

**Decision (owner, 2026-11-10):** Approved as recommended — `bin/CSVToolkit.dll` in both build scripts, namespace `C3.CSVToolkit`, zero dependencies, .NET 2.0 surface; Tools/ round-trip harness now, Phase 6 suite later.

### CSV-8. Operating model: complementary to EalItemr.cs — multithreaded and parallel by design

**Directive (owner, 2026-11-10):** "CSVToolkit's operating model should be complementary to EalItemr.cs's operating model - big, heavy emphasis on multithreading and parallelism because I want this thing to run as fast as possible."

Recorded ahead of CSV-3…CSV-7 per owner direction; it constrains how all of them are implemented.

Grounding facts (measured from `EalItemr/EalItemr.cs` header + API, 2026-11-10):
- Every operation runs **asynchronously on its own background thread** and returns an `AsyncResult<T>` handle: `Wait()` then read `Result`, or hook `Completed`; errors surface via the handle.
- Worker orchestration uses a **whitelist of primitives: `Thread` + `Join`** (plus locks for shared state) — no ThreadPool, no TPL, no async/await; consistent with the .NET 2.0 / Mono surface.
- Parallel tiers split work over **disjoint contiguous ranges** (`BrowseAsync([workerCount])` over block ranges; `ImportBatchShardedAsync` over home-bucket ownership), while ordering-sensitive phases (placement, commit) stay serial.
- Explicit acceptance bar: the serial and parallel tiers produce **byte-identical files**, with run-to-run determinism verified.

**Consequence:** CSVToolkit adopts the same idiom — background-thread operations with async handles, Thread+Join worker orchestration, disjoint-range parallelism over row chunks, and a byte-identity acceptance bar. The concrete decisions this raises are CSV-9…CSV-11 below.

### CSV-9. Threading whitelist and operation surface

**Question:** Which threading primitives may `C3.CSVToolkit` use, and how are its operations exposed to callers?

**Recommendation (mirror EalItemr exactly):**
- Worker orchestration via **`Thread` + `Join` only**; `lock`/`Monitor` for shared state as needed. No ThreadPool/TPL/async-await — none exist in the .NET 2.0 surface anyway, and the whitelist keeps Mono behaviour predictable.
- Public operations run on a background thread and return an **`AsyncResult<T>`-style handle** (`Wait()`, `Result`, error access, `Completed`) so gsa-cli drives both libraries with one mental model — that is what "complementary" buys at the call site.
- A **serial tier always available** (worker count 1 / dedicated serial path) for debugging and as the determinism reference.

**Decision (owner, 2026-11-10):** Approved as recommended — mirror EalItemr: Thread+Join whitelist, async-handle operation surface, serial tier retained.

### CSV-10. Parallel pipeline shape under the streaming bound

**Question:** What actually gets parallelized in read/write, and how do we keep the CSV-2 streaming memory bound (and D55–D57's ~400 MB discipline) intact *while* running workers?

**Recommendation — chunked ordered pipeline:**
- **Reader:** split the file into contiguous, line-boundary-aligned row chunks; each worker owns a disjoint chunk (the EalItemr idiom); results merge strictly in file order. Chunks are emitted as they complete, so only O(one chunk) of rows is live at any moment — parallelism inside a chunk, streaming across chunks.
- **Writer:** row preparation (quoting/formatting per CSV-4) pipelined ahead on workers; the actual file write stays a single ordered thread (byte order must be deterministic).
- `workerCount` parameter on public operations, like EalItemr's `[workerCount]`; default chosen at implementation time.
- **Interop note:** during gsa-cli IMPORT, CSVToolkit workers and EalItemr's session work run concurrently — both libraries cap their own worker counts; gsa-cli picks the numbers so total threads stay sane on 2–4 CPU XP-era boxes.

**Decision (owner, 2026-11-10):** Approved as recommended — chunked ordered pipeline with per-chunk emission, writer formatting pipelined ahead of a single ordered write thread, `workerCount` parameter.

### CSV-11. Determinism contract (the acceptance bar)

**Question:** What guarantees make parallel output trustworthy and testable?

**Decision (owner, 2026-11-10):** Approved as recommended — byte-identical output independent of worker count/scheduling; deterministic line-numbered errors (first-in-file-order wins); N-way + repeated-run diffs are the acceptance test.

---

## Implementation & verification (2026-11-10)

**Built:** `AssemblyInfo.cs`, `AsyncResult.cs` (`AsyncResult<T>` handle, EalItemr idiom),
`CsvException.cs` (typed error + offending line), `CsvRow.cs`, `CsvCore.cs`
(parse/format core), `CsvReader.cs` (chunked parallel pull pipeline),
`CsvWriter.cs` (batched parallel format → single ordered write thread, bounded
queue with backpressure), `Csv.cs` (facade: worker-count policy + ReadAll/WriteAll).
Compiles clean under the v2.0 gate in both build.bat and build.sh; zero warnings.
Note: no `[assembly: CLSCompliant(true)]` — this library follows EalItemr's
underscore-private-field idiom (EalItemr makes the same choice); Xml2 declares
CLS because it has no such fields.

**Harness:** `Tools/CsvTest.exe` (source `Tools/CsvTest.cs`, build wrapper
`Tools/csvtest.bat`) — commands: `all`, `stream <file> [wc] [passes]`,
`roundtrip <file> [wc]`, `determinism <file>`, `migrate`, `errors`. Result:
**31/31 checks pass, exit 0**, including byte-identical round-trips of
sample.csv at worker counts 1/2/4/8 and re-run determinism of the migration.

**Migration (CSV-6):** `sample_massive_alt.csv` → `sample_massive.csv`
(3505 rows, reference schema, flags mapped 0→N / 1→Y, SALEPRICE2 inserted as
0, header typos fixed). Original alt file preserved. Output verified: UTF-8
BOM, CRLF between rows, no trailing newline; first data row matches the
reference shape exactly.

**Streaming bound (CSV-2) measured:** `Tools/genbig.ps1` generates a 53 MB /
1M-row fixture (`big.csv`). Streaming round-trip (reader→writer pump, no bulk
collection) is byte-identical at wc=8 and wc=1. Peak working set:
~270–290 MB for the 53 MB file, **flat across two consecutive passes in one
process** (270.2 MB) — the D57 "flat across scans" criterion holds. The live
set is bounded by design (one reader group of ≤ workerCount × 512 KB chunks +
a few writer batches); the WS high-water mark above that is .NET 2.0 GC
behavior under heavy short-lived allocation, same order as the accepted
gsa-cli band from D55–D57.

**Bugs caught by verification (both fixed before sign-off):**
1. Writer deadlock: pipeline dequeued a batch without pulsing, so a producer
   blocked on the full queue was never woken (small files never filled the 4-
   unit queue, which is why round-trips passed until the 3505-row migration).
2. Writer leak: `_batch` was not cleared after hand-off, accumulating past one
   batch — violated CSV-2's streaming bound.

**gsa-cli regression check:** read-only functional pass on `GSA EALITEMR.DAT`
(`Tools/func.in`) still exits 0 after the rebuild; LARGEADDRESSAWARE patch
still applied. Bridge swap-in remains scheduled for Phase 5/6b per PROJECT.md.

---
*End of CSV decision log.*
