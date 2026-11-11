/* EalItemr.cs - .NET library for IBM 4680/4690 GSA EALITEMR keyed item file.
 *
 * Library adaptation of ealitemr_edit.c (not a CLI port): every operation
 * runs asynchronously on its own background thread and returns an
 * AsyncResult<T> handle: Wait() then read Result, or hook Completed.
 *
 * File format (verified against GSA EALITEMR.DAT + 4690 OS V6R2 guide):
 *   - 512-byte blocks. Block 0 = control block:
 *       +42 u32le num blocks   +46 u16le rec size (50)   +48 u32le divisor (999)
 *       +54 u16le key length (11)   +56 u32le chain threshold (4)
 *   - Data blocks 1..N-1: 4-byte chain header (u16le fwd, u16le bwd),
 *     then 10 slots x 50-byte records. Empty slot = all-zero key.
 *   - Record = 11-byte packed-BCD key + fields:
 *       +11 INDICAT0  +12 INDICAT1  +13 INDICAT2$  +14 DEPARTME$(3B)
 *       +17 SALEQUAN$(1B)  +18 SALEPRIC$(5B, cents)  +23 MPGROUP$(1B)
 *       +24 ITEMNAME(18)  +42 USER(8)
 *   - SALEPRIC$ is price-METHOD dependent (Programming Guide Table 101,
 *     pricing method = units digit of INDICAT2$):
 *       method 0: 000 ppppppp  unit price (package price when qty > 1)
 *       method 1: 000 ppppppp  package price
 *       method 2: pppppsssss   ppppp = package price, sssss = single unit
 *       method 3: uuuuurrrrr   uuuuu = unit price, rrrrr = reduced price
 *     All prices in cents. SalePriceCents = method's primary (unit/price)
 *     value; SalePrice2Cents = package (m2) / reduced (m3) price.
 *   - Home block = folded_key % divisor (IBM folding, remainder 0 -> divisor).
 *
 * Phase 1 features:
 *   - In-memory key index built at open (BuildIndex) and maintained on every
 *     mutation under _imageLock: O(1) FindRec, no fallback scan.
 *   - Session mode: BeginSession() defers writes to the RAM image; Commit-
 *     SessionAsync() persists once via AtomicWrite(); RollbackSessionAsync()
 *     re-reads disk + rebuilds index (disk was never touched); Close()
 *     auto-commits a dirty open session.
 *   - AtomicWrite(): same-directory temp file + Flush(true) + File.Move, with
 *     an IOException delete-then-move fallback for runtimes where Move refuses
 *     to replace an existing file. Crash-safe: old or new complete file only.
 *   - Planned-layout bulk import: ImportDatabase() validates/packs all records,
 *     computes the exact block layout (999 sacred homes + per-bucket overflow
 *     chains), fails fast with CapacityException(BlocksNeeded) when the u16
 *     chain-pointer ceiling (MaxDataBlocks = 65,535 data blocks) would be
 *     exceeded - before writing anything - then streams one file and self-
 *     checks it.
 *
 * Phase 2 features:
 *   - Batch upsert pipeline: ImportBatchAsync(rows) validates/packs every row
 *     first (collect-all-then-refuse: any invalid row -> BatchValidationException
 *     with the full per-row report, nothing placed or committed), then places all
 *     rows in input order through the same Place() upsert path as single writes
 *     (existing key = 50-byte in-place overwrite; new key = free slot + index;
 *     intra-batch duplicates: last row wins), and commits exactly once - one
 *     AtomicWrite, or deferred when a session is open. One ~31 MB write for
 *     600k rows instead of one full-file rewrite per record (~9 TB).
 *   - Placement index completes the Phase 1 key index: per-block free-slot
 *     counts, chained flags, a pristine-block pool (blocks above the divisor
 *     only - home blocks are never stolen), and chain tails; all built in one
 *     O(n) pass at open and maintained on every mutation. FindFree/AppendChain
 *     are now O(1) amortized instead of scanning the whole file per allocation
 *     (the old BlockPristine check made overflow placement O(blocks^2) -
 *     unusable at 600k scale).
 *
 * Phase 3 features:
 *   - Parallel validate/pack front half of the batch pipeline:
 *     ImportBatchParallelAsync(rows[, workerCount]) splits the materialized row
 *     array into K contiguous ranges; each worker validates + packs its own
 *     range into a shared preallocated packed[] (every slot written by exactly
 *     one worker, per-worker error lists) - zero locks in the parallel phase.
 *     Whitelist-only primitives: Thread + Join. Placement and commit stay serial
 *     under _imageLock in input order and share the exact same code path as
 *     ImportBatchAsync (Phase 2), so both tiers produce byte-identical files
 *     (differential-tested). K == 1 or fewer than MinRowsForParallelism rows
 *     falls back to the serial path (report.Workers == 1).
 *
 * Phase 4 features:
 *   - Sharded fused placement: ImportBatchShardedAsync(rows[, workerCount])
 *     parallelizes the placement phase itself. Each shard gets exclusive
 *     ownership of a contiguous range of home buckets (home block + full
 *     overflow chain); all its decisions are local, so the parallel phase has
 *     zero shared mutable state - disjoint _image regions, disjoint
 *     _freeCount/_chainTail cells, per-shard local key maps merged after Join.
 *     A demand pass first computes exactly how many pristine blocks each shard
 *     will need (zero shared mutation); the op thread then either fails fast
 *     with FileFullException before any mutation or hands each shard its own
 *     slice of the pool. Whitelist-only primitives: Thread + Join.
 *     Output is deterministic for a fixed (input, K) but NOT byte-identical to
 *     the serial tier's file (overflow blocks come from different slices); the
 *     acceptance oracle is semantic equivalence (same key set and values,
 *     zero orphans, valid chains per VerifyAsync) plus run-to-run determinism.
 *
 * Workstream B features (browse-tool foundation):
 *   - BrowseAsync([workerCount]) decodes every record in the file into an
 *     in-memory BrowseTable (block/slot scan order). Parallel extraction over
 *     contiguous block ranges: each worker counts + decodes its own range into
 *     a private buffer; buffers merge in worker order after Join, so parallel
 *     output is identical to serial output (differential-tested). Zero locks
 *     in the parallel phase (the op thread holds _imageLock for the duration;
 *     workers only read disjoint regions). Whitelist-only primitives: Thread +
 *     Join. K == 1 or a small file falls back to the serial path.
 *   - BrowseTable = immutable row store + separate view state: int[] order
 *     permutation (Array.Sort over ints) for sorting, Predicate<EalItemrRecord>
 *     filter, and paging via Get/CopyToRange. Re-sorting/re-filtering a loaded
 *     table is an O(n log n) int pass - no re-decode, no disk.
 *
 * Target: .NET 2.0 (C# 2.0 source; build: mcs -sdk:2.0 -target:library).
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace C3.EalItemr
{
    /* ================= exceptions ================= */

    /// <summary>Base for all EalItemr library errors.</summary>
    public class EalItemrException : Exception
    {
        public EalItemrException() { }
        public EalItemrException(string message) : base(message) { }
        public EalItemrException(string message, Exception inner)
            : base(message, inner) { }
    }

    /// <summary>File is missing, too small, or has an invalid control block.</summary>
    public class FileFormatException : EalItemrException
    {
        public FileFormatException(string message) : base(message) { }
    }

    /// <summary>A record field failed validation (bad digits, too long, ...).</summary>
    public class FieldFormatException : EalItemrException
    {
        public FieldFormatException(string message) : base(message) { }
    }

    /// <summary>An operation needs an existing record and it was not found.</summary>
    public class RecordNotFoundException : EalItemrException
    {
        public string Code { get { return _code; } }
        private readonly string _code;
        public RecordNotFoundException(string code)
            : base("record not found: " + code) { _code = code; }
    }

    /// <summary>AddRecordAsync was called for a record that already exists.</summary>
    public class RecordAlreadyExistsException : EalItemrException
    {
        public string Code { get { return _code; } }
        private readonly string _code;
        public RecordAlreadyExistsException(string code)
            : base("record already exists: " + code) { _code = code; }
    }

    /// <summary>No free slot in the home block, its chain, or any pristine block.</summary>
    public class FileFullException : EalItemrException
    {
        public FileFullException() : base("file full: no free slot available") { }
        public FileFullException(string message) : base(message) { }
    }

    /// <summary>The requested operation would exceed the format's addressable
    /// capacity (u16 chain pointers limit a file to 65,535 data blocks =
    /// 655,350 record slots).</summary>
    public class CapacityException : EalItemrException
    {
        /// <summary>Number of data blocks the operation would need.</summary>
        public int BlocksNeeded { get { return _blocks; } }
        private readonly int _blocks;
        public CapacityException(string message, int blocksNeeded)
            : base(message) { _blocks = blocksNeeded; }
    }

    /// <summary>A batch import contained invalid row(s). Errors lists every bad
    /// row (1-based ordinal, code, diagnostic); nothing was placed or committed.</summary>
    public class BatchValidationException : EalItemrException
    {
        /// <summary>All invalid rows of the rejected batch, in input order.</summary>
        public BatchError[] Errors { get { return _errors; } }
        private readonly BatchError[] _errors;
        public BatchValidationException(BatchError[] errors)
            : base("batch import rejected: " + errors.Length + " invalid row(s)"
                + (errors.Length > 0 ? "; first: " + errors[0].ToString() : string.Empty))
        { _errors = errors; }
    }

    /* ================= async operation handles ================= */

    /// <summary>Worker body for a typed async operation.</summary>
    internal delegate T EalItemrWork<T>();

    /// <summary>Base operation handle. Each operation runs on its own
    /// background thread; the handle carries completion state.</summary>
    public abstract class AsyncResult
    {
        protected object _gate = new object();
        protected Exception _error;
        protected bool _completed;
        private Thread _thread;
        private EalItemrFile _owner;

        /// <summary>True once the worker thread has finished (success or failure).</summary>
        public bool IsCompleted
        {
            get { lock (_gate) { return _completed; } }
        }
        /// <summary>Worker exception, or null when the operation succeeded.</summary>
        public Exception Error
        {
            get { lock (_gate) { return _error; } }
        }
        /// <summary>Managed thread id of this operation's own worker thread.</summary>
        public long WorkerThreadId
        {
            get { return _thread != null ? _thread.ManagedThreadId : 0; }
        }
        /// <summary>Block the calling thread until the operation finishes.</summary>
        public void Wait()
        {
            if (_thread != null) _thread.Join();
        }
        internal void Start(string opName, EalItemrFile owner, ThreadStart work)
        {
            _owner = owner;
            _thread = new Thread(work);
            _thread.Name = "ealitemr-" + opName;
            _thread.IsBackground = true;
            _thread.Start();
        }
        internal void Finish(Exception error)
        {
            lock (_gate) { _error = error; _completed = true; }
            if (_owner != null) _owner.UnregisterOp(this);
        }
    }

    /// <summary>Typed operation handle: Wait() then read Result, or use Completed.</summary>
    public sealed class AsyncResult<T> : AsyncResult
    {
        private T _result;

        /// <summary>Raised on the worker thread when the operation completes.</summary>
        public event EventHandler<AsyncCompletedEventArgs<T>> Completed;

        /// <summary>Result value. Blocks until the operation completes, then
        /// returns the result or throws the worker exception on failure.
        /// For non-blocking observation use IsCompleted / Wait / Completed.</summary>
        public T Result
        {
            get
            {
                if (!IsCompleted) Wait();
                lock (_gate)
                {
                    if (_error != null) throw _error;
                    return _result;
                }
            }
        }
        internal void SetResult(T result, Exception error)
        {
            _result = result;
            Finish(error);
        }
        internal void RaiseCompleted(T result, Exception error)
        {
            EventHandler<AsyncCompletedEventArgs<T>> h = Completed;
            if (h != null)
                h(this, new AsyncCompletedEventArgs<T>(result, error));
        }
    }

    /// <summary>EventArgs for AsyncResult.Completed (WebClient-style, .NET 2.0).</summary>
    public sealed class AsyncCompletedEventArgs<T> : EventArgs
    {
        private readonly T _result;
        private readonly Exception _error;
        internal AsyncCompletedEventArgs(T result, Exception error)
        { _result = result; _error = error; }
        /// <summary>Operation result (default(T) when the operation failed).</summary>
        public T Result { get { return _result; } }
        /// <summary>Worker exception, or null on success.</summary>
        public Exception Error { get { return _error; } }
    }

    /* ================= record model ================= */

    /// <summary>INDICAT0 / INDICAT1 flag bit values (GSA Table 101, Item Record File).</summary>
    public static class ItemFlags
    {
        // INDICAT0 (byte offset 11)
        public const byte MovementKept       = 0x80;
        public const byte QuantityRequired   = 0x40;
        public const byte PriceRequired      = 0x20;
        public const byte NotAuthorizedSale  = 0x10;
        public const byte RestrictedHours    = 0x08;
        public const byte NotReturnable      = 0x04;
        public const byte NotPricedGiveaway  = 0x02;
        public const byte WeightPriceReq     = 0x01;
        // INDICAT1 (byte offset 12)
        public const byte TaxTable1          = 0x80;
        public const byte TaxTable2          = 0x40;
        public const byte TaxTable3          = 0x20;
        public const byte TaxTable4          = 0x10;
        public const byte StockNumberReq     = 0x08;
    }

    /// <summary>One 50-byte item record. Fields mirror the EALITEMR layout;
    /// PackRecordAsync/WriteRecordAsync validate them against BCD limits.</summary>
    public struct EalItemrRecord
    {
        /// <summary>Item code, 1-12 decimal digits (stored zero-padded BCD).</summary>
        public string Code;
        /// <summary>Flag byte 0; see ItemFlags (qty/price required, giveaway, ...).</summary>
        public byte Indicat0;
        /// <summary>Flag byte 1; see ItemFlags tax tables / stock number.</summary>
        public byte Indicat1;
        /// <summary>D/C/S validation class, INDICAT2 tens digit: 0 = none ..
        /// 8 = D/C/S validation required. Write: 0-8.</summary>
        public byte Indicat2Validation;
        /// <summary>Pricing method, INDICAT2 units digit: 0 = unit/split
        /// package, 1 = base-plus-1, 2 = group threshold, 3 = reduced.
        /// Write: 0-3 (4-9 undefined).</summary>
        public byte Indicat2Pricing;
        /// <summary>Department number, up to 6 decimal digits.</summary>
        public string Department;
        /// <summary>Sale quantity in the deal, 0-99.</summary>
        public int SaleQuan;
        /// <summary>Primary sale price in cents (see class docs for the
        /// per-method SALEPRIC$ splits): methods 0/1 = lower 7 digits
        /// (0-9999999 = $99,999.99); method 2 = single unit price,
        /// method 3 = unit price (both 0-99999 = $999.99).</summary>
        public long SalePriceCents;
        /// <summary>Secondary SALEPRIC$ price, cents: method 2 = package
        /// price, method 3 = reduced price (0-99999 = $999.99). Methods
        /// 0/1 use the 3 reserved top digits (normally 0).</summary>
        public long SalePrice2Cents;
        /// <summary>Multi-price group number, 0-99.</summary>
        public int MpGroup;
        /// <summary>Item description, up to 18 characters (uppercased on write).</summary>
        public string ItemName;
        /// <summary>User area, up to 8 characters.</summary>
        public string User;

        /// <summary>New record with the GSA-sensible defaults: INDICAT0 = qty + price
        /// required (0x60, item NOT priced-free), INDICAT1 = 0, INDICAT2 = 00 (no
        /// validation, unit pricing), zero qty/mpg/price. Set fields as needed.</summary>
        public static EalItemrRecord CreateDefault(string code)
        {
            EalItemrRecord r = new EalItemrRecord();
            r.Code = code;
            r.Indicat0 = EalItemrFile.DefaultIndicat0;
            r.Indicat1 = 0;
            r.Indicat2Validation = 0;
            r.Indicat2Pricing = 0;
            r.Department = string.Empty;
            r.SaleQuan = 0;
            r.SalePriceCents = 0;
            r.SalePrice2Cents = 0;
            r.MpGroup = 0;
            r.ItemName = string.Empty;
            r.User = string.Empty;
            return r;
        }
    }

    /* ================= operation results ================= */

    /// <summary>Outcome of AddRecordAsync / WriteRecordAsync.</summary>
    public struct WriteResult
    {
        /// <summary>The record as stored (packed/normalized fields).</summary>
        public EalItemrRecord Record;
        /// <summary>Block number (1-based) where the record was placed.</summary>
        public int Block;
        /// <summary>Slot (0-9) within the block.</summary>
        public int Slot;
        /// <summary>True when a new slot was created, false when an existing record was replaced.</summary>
        public bool Created;
    }

    /// <summary>One row from ListRecordsAsync.</summary>
    public struct ItemrRow
    {
        public string Code;
        public string Name;
        public string Department;
        /// <summary>Primary sale price (EalItemrRecord.SalePriceCents).</summary>
        public long PriceCents;
        /// <summary>Secondary price: package (method 2) / reduced (method 3); 0 for methods 0/1.</summary>
        public long Price2Cents;
        /// <summary>D/C/S validation class (INDICAT2 tens digit, 0-9).</summary>
        public int Validation;
        /// <summary>Pricing method (INDICAT2 units digit) 0-3, or null when
        /// the stored digit is 4-9 (undefined pricing method).</summary>
        public int? PricingMethod;
    }

    /// <summary>Result of ListRecordsAsync: rows in block/slot scan order.</summary>
    public struct RecordListResult
    {
        /// <summary>Rows returned (at most Limit entries; Limit 0 = all records).</summary>
        public ItemrRow[] Rows;
        /// <summary>Number of rows in Rows.</summary>
        public int Count;
    }

    /// <summary>Result of ImportDatabase: what the planned import produced.</summary>
    public struct ImportReport
    {
        /// <summary>Number of records written to the new file.</summary>
        public int RecordsImported;
        /// <summary>Data blocks in the new file (excluding the control block).</summary>
        public int DataBlocks;
        /// <summary>Largest per-bucket record count (hotspot indicator).</summary>
        public int MaxBucketCount;
    }

    /// <summary>Result of VerifyAsync: hash/layout consistency summary.</summary>
    public struct VerifyReport
    {
        public int BlockCount;
        public int RecordSize;
        public int Divisor;
        public int KeyLen;
        public int ChainThreshold;
        public int TotalRecords;
        /// <summary>Records sitting in their own home block.</summary>
        public int HomeRecords;
        /// <summary>Records in a block reachable from their home's overflow chain.</summary>
        public int ChainedRecords;
        /// <summary>Records in a block with no link from their home block.</summary>
        public int OrphanRecords;
        /// <summary>Records with a valid packed-BCD key.</summary>
        public int BcdRecords;
        /// <summary>Records with a non-BCD (raw/hex) key.</summary>
        public int RawKeyRecords;
        public int EmptySlots;
    }

    /// <summary>One invalid row of a rejected batch import.</summary>
    public struct BatchError
    {
        /// <summary>1-based ordinal position of the row in the input sequence.</summary>
        public int Row;
        /// <summary>The row's item code (may be empty if the code itself was bad).</summary>
        public string Code;
        /// <summary>Validation diagnostic for this row.</summary>
        public string Message;

        public override string ToString()
        {
            return "row " + Row + ": code=" + ((Code == null || Code.Length == 0) ? "(empty)" : Code)
                + ": " + Message;
        }
    }

    /// <summary>Outcome of a batch import: per-phase timings and row counts.</summary>
    public struct BatchReport
    {
        /// <summary>Rows in the input sequence (valid ones; invalid rows reject the batch).</summary>
        public int Rows;
        /// <summary>Rows that created new records.</summary>
        public int Created;
        /// <summary>Rows that overwrote existing records (incl. intra-batch duplicates).</summary>
        public int Updated;
        /// <summary>Validate/pack phase duration in milliseconds.</summary>
        public double ValidateMs;
        /// <summary>Placement phase duration in milliseconds.</summary>
        public double PlaceMs;
        /// <summary>Commit phase duration in milliseconds (0 when deferred to a session).</summary>
        public double CommitMs;
        /// <summary>Bytes written by the batch's single commit (0 when deferred).</summary>
        public long BytesWritten;
        /// <summary>Worker threads used for validate/pack (1 = serial path).</summary>
        public int Workers;

        /// <summary>Placement strategy that produced the file:
        /// "serial" (ImportBatchAsync / ImportBatchParallelAsync) or
        /// "sharded" (ImportBatchShardedAsync). Sharded output is
        /// deterministic for a fixed (input, K) but not byte-identical to
        /// serial output; compare tiers semantically, not bytewise.</summary>
        public string Placement;
    }

    /* ================= browse table (Workstream B) ================= */

    /// <summary>Columns sortable via BrowseTable.Sort. Comparison is ordinal
    /// (deterministic across machines and cultures).</summary>
    public enum BrowseColumn
    {
        Code,
        ItemName,
        Department,
        SalePriceCents,
        SalePrice2Cents,
        SaleQuan,
        MpGroup,
        Indicat0,
        Indicat1,
        Indicat2Validation,
        Indicat2Pricing,
        User
    }

    /// <summary>In-memory browse table: every record in the file decoded into RAM
    /// (block/slot scan order), with sort + filter + paging over a separate view
    /// state. The row store is immutable after construction; Sort/SetFilter only
    /// rearrange the view, so re-sorting or re-filtering a loaded table never
    /// re-decodes and never touches disk. Intended for browse/edit UIs: load once,
    /// serve all view operations from RAM (see PROJECT-SUMMARY section 8).
    /// Not thread-safe against concurrent Sort/SetFilter; reads are safe while no
    /// mutation is in flight.</summary>
    public sealed class BrowseTable
    {
        private readonly EalItemrRecord[] _rows;   /* immutable store, scan order */
        private int[] _order;                      /* permutation of _rows indices, display sort order */
        private int[] _view;                       /* visible rows (filter applied), in display order */
        private Predicate<EalItemrRecord> _filter;
        private readonly object _sync = new object();
        private int _workers;
        private double _extractMs;

        internal BrowseTable(EalItemrRecord[] rows, int workers, double extractMs)
        {
            _rows = rows;
            _order = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) _order[i] = i;
            _view = (int[])_order.Clone();
            _workers = workers;
            _extractMs = extractMs;
        }

        /// <summary>Visible row count (after filter), in display order.</summary>
        public int Count { get { lock (_sync) return _view.Length; } }
        /// <summary>Total decoded rows before filtering.</summary>
        public int TotalRows { get { return _rows.Length; } }
        /// <summary>Worker threads used for extraction (1 = serial path).</summary>
        public int Workers { get { return _workers; } }
        /// <summary>Extraction/decode phase duration in milliseconds.</summary>
        public double ExtractMs { get { return _extractMs; } }
        /// <summary>True when a filter is active (Count may be below TotalRows).</summary>
        public bool HasFilter { get { lock (_sync) return _filter != null; } }

        /// <summary>Visible row at display position index (0-based).
        /// Throws ArgumentOutOfRangeException outside the visible range.</summary>
        public EalItemrRecord Get(int index)
        {
            lock (_sync)
            {
                if (index < 0 || index >= _view.Length)
                    throw new ArgumentOutOfRangeException("index", index,
                        "outside visible range [0, " + _view.Length + ")");
                return _rows[_view[index]];
            }
        }

        /// <summary>Copy a page of visible rows into buffer for rendering (the host
        /// draws N rows at a time instead of materializing all of them). Returns the
        /// number copied, which is fewer than requested near the end.</summary>
        public int CopyToRange(int start, int count, EalItemrRecord[] buffer)
        {
            lock (_sync)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (start < 0 || count < 0)
                    throw new ArgumentException("start and count must be >= 0", "start");
                if (start > _view.Length)
                    throw new ArgumentOutOfRangeException("start", start,
                        "past end of visible rows");
                int n = Math.Min(count, _view.Length - start);
                for (int i = 0; i < n; i++) buffer[i] = _rows[_view[start + i]];
                return n;
            }
        }

        /// <summary>Sort the view by column. Ordinal comparison; ties come out in an
        /// unspecified but deterministic order (Array.Sort is not stable).</summary>
        public void Sort(BrowseColumn column, bool ascending)
        {
            lock (_sync)
            {
                Array.Sort(_order, new OrderComparer(this, column, ascending ? 1 : -1));
                RebuildView();
            }
        }

        /// <summary>Filter the view. null = show all rows again.</summary>
        public void SetFilter(Predicate<EalItemrRecord> predicate)
        {
            lock (_sync)
            {
                _filter = predicate;
                RebuildView();
            }
        }

        private void RebuildView()
        {
            if (_filter == null) { _view = (int[])_order.Clone(); return; }
            int n = 0;
            for (int i = 0; i < _order.Length; i++)
                if (_filter(_rows[_order[i]])) n++;
            int[] v = new int[n];
            int j = 0;
            for (int i = 0; i < _order.Length; i++)
                if (_filter(_rows[_order[i]])) v[j++] = _order[i];
            _view = v;
        }

        private sealed class OrderComparer : IComparer
        {
            private readonly BrowseTable _t;
            private readonly BrowseColumn _col;
            private readonly int _dir;   /* +1 ascending, -1 descending */

            public OrderComparer(BrowseTable t, BrowseColumn col, int dir)
            {
                _t = t; _col = col; _dir = dir;
            }

            public int Compare(object a, object b)
            {
                EalItemrRecord ra = _t._rows[(int)a];
                EalItemrRecord rb = _t._rows[(int)b];
                int c;
                switch (_col)
                {
                    case BrowseColumn.Code:               c = string.CompareOrdinal(ra.Code, rb.Code); break;
                    case BrowseColumn.ItemName:           c = string.CompareOrdinal(ra.ItemName, rb.ItemName); break;
                    case BrowseColumn.Department:         c = string.CompareOrdinal(ra.Department, rb.Department); break;
                    case BrowseColumn.SalePriceCents:     c = ra.SalePriceCents.CompareTo(rb.SalePriceCents); break;
                    case BrowseColumn.SalePrice2Cents:    c = ra.SalePrice2Cents.CompareTo(rb.SalePrice2Cents); break;
                    case BrowseColumn.SaleQuan:           c = ra.SaleQuan.CompareTo(rb.SaleQuan); break;
                    case BrowseColumn.MpGroup:            c = ra.MpGroup.CompareTo(rb.MpGroup); break;
                    case BrowseColumn.Indicat0:           c = ra.Indicat0.CompareTo(rb.Indicat0); break;
                    case BrowseColumn.Indicat1:           c = ra.Indicat1.CompareTo(rb.Indicat1); break;
                    case BrowseColumn.Indicat2Validation: c = ra.Indicat2Validation.CompareTo(rb.Indicat2Validation); break;
                    case BrowseColumn.Indicat2Pricing:    c = ra.Indicat2Pricing.CompareTo(rb.Indicat2Pricing); break;
                    default:                              c = string.CompareOrdinal(ra.User, rb.User); break;
                }
                return _dir < 0 ? -c : c;
            }
        }
    }

    /* ================= keyed file ================= */

    /// <summary>Access to one EALITEMR.DAT keyed file. Open (sync or async), then call
    /// operations; each call returns immediately with an AsyncResult handle that runs
    /// on its own background thread. Mutating operations persist the whole image.
    /// Call Close() when done (waits for outstanding operations).</summary>
    public sealed class EalItemrFile : IDisposable
    {
        /* format constants (GSA EALITEMR) */
        public const int BlockSize = 512;
        public const int SlotsPerBlock = 10;
        public const int KeyLen = 11;
        public const int RecordSize = 50;
        public const int NameLen = 18;
        public const int UserLen = 8;
        public const int CodeMaxDigits = 12;
        public const long PriceMaxCents = 9999999999L;
        public const int Divisor = 999;

        /// <summary>Maximum data blocks addressable by the u16 chain pointers:
        /// a hard format ceiling of 65,535 blocks = 655,350 record slots.</summary>
        public const int MaxDataBlocks = 65535;

        /// <summary>Default INDICAT0 for new records: quantity + price required
        /// (X'40' + X'20'); "item not priced (giveaway)" stays OFF.</summary>
        public const byte DefaultIndicat0 = 0x60;

        private readonly string _path;
        private byte[] _image;
        private int _nblk, _rs, _divisor, _klen, _thr;
        private readonly object _imageLock = new object();
        private readonly object _opLock = new object();
        private readonly ArrayList _activeOps = new ArrayList();
        private bool _closed;

        /* ---------- Phase 1: in-memory key index + deferred-commit session ----------
           _index maps every used slot's key (exactly _klen bytes) to a packed
           location (block << 4 | slot). Built by scanning the image once at
           open, and maintained on every mutation under _imageLock. Memory-only:
           never written to disk; the file format is unchanged.
           Session mode: BeginSession() defers SaveImage(); CommitSessionAsync()
           writes once (temp + rename); RollbackSessionAsync() re-reads the
           unmodified on-disk image. Close() auto-commits pending changes. */
        private Hashtable _index;
        private bool _session;
        private bool _dirty;

        /* Phase 2 placement index (built by BuildIndex, maintained under
           _imageLock): per-block free-slot counts, chained flags, pristine
           block pool (stack), and chain tails - O(1) FindFree/AppendChain. */
        private int[] _freeCount;
        private bool[] _chained;
        private int[] _pristine;
        private int _pristineCount;
        private int[] _chainTail;

        private static int _tmpSeq;

        /// <summary>Immutable key wrapper for the index: value-equality over a
        /// fixed-length byte array (byte[] itself has reference equality).</summary>
        private sealed class KeyRef
        {
            private readonly byte[] _k;
            private readonly int _hash;
            public KeyRef(byte[] k)
            {
                _k = k;
                int h = 17;
                for (int i = 0; i < k.Length; i++)
                    h = h * 31 + k[i];
                _hash = h;
            }
            public override bool Equals(object o)
            {
                KeyRef r = o as KeyRef;
                if (r == null || r._k.Length != _k.Length) return false;
                for (int i = 0; i < _k.Length; i++)
                    if (_k[i] != r._k[i]) return false;
                return true;
            }
            public override int GetHashCode() { return _hash; }
        }

        private EalItemrFile(string path) { _path = path; }

        /// <summary>Open an existing EALITEMR.DAT file (loads the image, parses the
        /// control block). Throws FileFormatException on a bad file.</summary>
        public static EalItemrFile Open(string path)
        {
            EalItemrFile f = new EalItemrFile(path);
            f.LoadImage();
            return f;
        }

        /// <summary>Open on a background thread; Result is the opened file.</summary>
        public static AsyncResult<EalItemrFile> OpenAsync(string path)
        {
            AsyncResult<EalItemrFile> r = new AsyncResult<EalItemrFile>();
            r.Start("open", null, new ThreadStart(delegate
            {
                Exception err = null; EalItemrFile res = null;
                try { res = Open(path); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /* ---------- create new database ---------- */

        /// <summary>Create a new, empty 4680/4690 keyed GSA item file
        /// ("database") at path that can hold up to maxRecords item records.
        ///
        /// The output follows the keyed-file layout in the 4690 OS V6R2
        /// Programming Guide ("Keyed File Control Record") and matches the
        /// shipped GSA EALITEMR.DAT: block 0 is the 512-byte KFCB (sector
        /// zero-initialized, then required fields set), followed by
        /// ceil(maxRecords / SlotsPerBlock) zeroed 512-byte data blocks, each
        /// holding 10 slots of 50-byte keyed records (an empty block has an
        /// all-zero 4-byte chain header and all-zero slots). KFCB fields set:
        ///   +12 file name (ASCII, zero padded)     +30..37 creation time stamp
        ///   +40 block size (512)                   +42 number of blocks
        ///   +46 record size (50)                   +48 divisor (999)
        ///   +52 key offset (0)                     +54 key length (11)
        ///   +56 chain threshold (4)                +160 valid string "FSFACADX"
        ///   +179 hashing algorithm (0)             +180 chain pointer type (0 =
        ///        absolute block numbers, as in GSA EALITEMR.DAT)
        ///   +508 user identification ("USER")
        /// All other bytes stay zero (reserved / statistics fields).
        ///
        /// If a file already exists at path it is replaced. Data blocks are
        /// written one 512-byte block at a time, so RAM use stays bounded no
        /// matter how large maxRecords is. Throws ArgumentException for
        /// invalid arguments; disk failures surface as IOException.
        /// </summary>
        public static void CreateNewDatabase(int maxRecords, string path)
        {
            if (path == null || path.Length == 0)
                throw new ArgumentException("path must not be null or empty", "path");
            if (maxRecords < 1)
                throw new ArgumentException("maxRecords must be >= 1", "maxRecords");

            int dataBlocks = (int)(((long)maxRecords + SlotsPerBlock - 1) / SlotsPerBlock);
            // IBM keyed files need every home block 1..divisor to exist and at
            // least one block for overflow chaining; floor at divisor+1 data
            // blocks (1001 total, the same layout as real GSA EALITEMR.DAT).
            if (dataBlocks < Divisor + 1) dataBlocks = Divisor + 1;
            /* chain pointers are u16: a file with more than MaxDataBlocks data
               blocks cannot be placed into safely (block numbers would truncate).
               Fail fast instead of creating an unusable file. */
            if (dataBlocks > MaxDataBlocks)
                throw new CapacityException(
                    "database needs " + dataBlocks + " data blocks but the u16 chain pointer ceiling is "
                        + MaxDataBlocks,
                    dataBlocks);
            int totalBlocks = dataBlocks + 1; // block 0 = KFCB, then data blocks

            byte[] ctl = BuildKfcB(path, totalBlocks);
            byte[] zeroBlock = new byte[BlockSize]; // empty block: zero header + slots
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(ctl, 0, ctl.Length);
                for (int b = 0; b < dataBlocks; b++)
                    fs.Write(zeroBlock, 0, zeroBlock.Length);
            }
        }

        /// <summary>Build the 512-byte keyed file control block (KFCB, block 0)
        /// for a newly created GSA EALITEMR file of totalBlocks 512-byte
        /// blocks. Offsets are from the 4690 OS V6R2 Programming Guide,
        /// "Keyed File Control Record"; values match real GSA EALITEMR.DAT.</summary>
        private static byte[] BuildKfcB(string path, int totalBlocks)
        {
            byte[] ctl = new byte[BlockSize]; // guide: initialize the whole sector to zero

            // +12 file name, 18 bytes, ASCII with binary zero padding
            string name = Path.GetFileName(path);
            if (name.Length > NameLen) name = name.Substring(name.Length - NameLen);
            for (int i = 0; i < name.Length; i++)
                ctl[12 + i] = (byte)name[i];

            // +30 creation time stamp (year/month/day/ms after midnight);
            // +38 time zone is left zero, which the guide allows
            DateTime now = DateTime.Now;
            SetLe16(ctl, 30, now.Year);
            ctl[32] = (byte)now.Month;
            ctl[33] = (byte)now.Day;
            SetLe32(ctl, 34, (int)now.TimeOfDay.TotalMilliseconds);

            // +40..+56 keyed file parameters: 512-byte blocks, 50-byte keyed
            // records, 999 randomizing divisor, key at offset 0, 11-byte key
            SetLe16(ctl, 40, BlockSize);
            SetLe32(ctl, 42, totalBlocks);
            SetLe16(ctl, 46, RecordSize);
            SetLe32(ctl, 48, Divisor);
            SetLe16(ctl, 52, 0); // key offset: must be zero
            SetLe16(ctl, 54, KeyLen);
            SetLe32(ctl, 56, 4); // chain threshold: 4, as in GSA EALITEMR.DAT

            // +160 file valid string; +179 hashing algorithm 0; +180 chain
            // pointer type 0 = absolute block numbers (both stay zero below)
            ctl[160] = (byte)'F'; ctl[161] = (byte)'S'; ctl[162] = (byte)'F';
            ctl[163] = (byte)'A'; ctl[164] = (byte)'C'; ctl[165] = (byte)'A';
            ctl[166] = (byte)'D'; ctl[167] = (byte)'X';

            // +508 user identification: how the file was created
            ctl[508] = (byte)'U'; ctl[509] = (byte)'S';
            ctl[510] = (byte)'E'; ctl[511] = (byte)'R';

            return ctl;
        }

        /// <summary>Background-thread version of CreateNewDatabase: runs the
        /// whole file creation on a worker thread for performance. Result is
        /// the number of record slots in the new file; Wait() then inspect
        /// Error on failure.</summary>
        public static AsyncResult<int> CreateNewDatabaseAsync(int maxRecords, string path)
        {
            AsyncResult<int> r = new AsyncResult<int>();
            r.Start("createdb", null, new ThreadStart(delegate
            {
                Exception err = null; int res = 0;
                try
                {
                    CreateNewDatabase(maxRecords, path);
                    res = CountRecordSlots(path);
                }
                catch (Exception ex) { err = ex; res = 0; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /* ---------- extend an existing database ---------- */

        /// <summary>Extend an existing EALITEMR.DAT keyed file by adding enough
        /// zeroed data blocks to hold additionalSlots more record slots
        /// (e.g. 100 slot db + 100 = 200 slot db). Runs on a background
        /// thread; Result is the new total number of record slots.
        ///
        /// Procedure (per the 4690 OS V6R2 Programming Guide "Keyed File
        /// Control Record"): existing blocks are never touched, so all
        /// records and every absolute forward/backward chain pointer stay
        /// valid. The new blocks are pure capacity: each is a 512-byte 4680
        /// block with an all-zero chain header and 10 empty slots, exactly
        /// like the zero blocks CreateNewDatabase writes. The KFCB's
        /// "Number of blocks in the file" field (+42) is then rewritten to
        /// include the new blocks, so 4680 OPEN/GSA see one larger file.
        /// New blocks enter service lazily when FindFree needs overflow
        /// capacity (GSA 4680 chains a pristine block via the chain
        /// threshold), so no record migration is needed.
        ///
        /// File format is validated first (512-byte blocks, whole blocks, a
        /// valid keyed-file KFCB, absolute chain pointers - a relative
        /// pointer file cannot be safely extended by appending). Only block
        /// 0 is rewritten; the append is streamed one 512-byte block at a
        /// time, so RAM use stays bounded no matter how large the
        /// extension is. Existing records are never modified.
        ///
        /// Throws ArgumentException for bad arguments; FileFormatException
        /// for an invalid file; disk failures surface as IOException.
        /// </summary>
        public static AsyncResult<int> ExtendDatabase(int additionalSlots, string path)
        {
            if (path == null || path.Length == 0)
                throw new ArgumentException("path must not be null or empty", "path");
            if (additionalSlots < 1)
                throw new ArgumentException("additionalSlots must be >= 1", "additionalSlots");

            AsyncResult<int> r = new AsyncResult<int>();
            r.Start("extenddb", null, new ThreadStart(delegate
            {
                Exception err = null; int res = 0;
                try { res = ExtendDatabaseCore(additionalSlots, path); }
                catch (Exception ex) { err = ex; res = 0; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /// <summary>Worker body of ExtendDatabase: append zero blocks for
        /// additionalSlots more slots, then rewrite the KFCB block count.
        /// Returns the new total slot count.</summary>
        private static int ExtendDatabaseCore(int additionalSlots, string path)
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists)
                throw new FileFormatException("cannot open " + path);
            long sz = fi.Length;
            if (sz < BlockSize)
                throw new FileFormatException(path + ": file too small (" + sz + " bytes)");
            if (sz % BlockSize != 0)
                throw new FileFormatException(path + ": not a whole number of blocks");
            int oldBlocks = (int)(sz / BlockSize);
            if (oldBlocks < 2)
                throw new FileFormatException(path + ": no data blocks");

            // 1. validate the KFCB (block 0) - only 512 bytes touched
            byte[] ctl = new byte[BlockSize];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                int got = 0;
                while (got < BlockSize)
                {
                    int n = fs.Read(ctl, got, BlockSize - got);
                    if (n <= 0)
                        throw new FileFormatException(path + ": short read of control block");
                    got += n;
                }
            }
            int ksize = Le16(ctl, 40); if (ksize != BlockSize)
                throw new FileFormatException(path + ": bad block size (" + ksize + ")");
            int rs = Le16(ctl, 46);
            if (rs <= 0 || rs > BlockSize - 4)
                throw new FileFormatException(path + ": bad record size (" + rs + ")");
            if (Le16(ctl, 52) != 0)
                throw new FileFormatException(path + ": key offset is not zero");
            int klen = Le16(ctl, 54);
            if (klen <= 0 || klen > rs)
                throw new FileFormatException(path + ": bad key length (" + klen + ")");
            if (ctl[160] != (byte)'F' || ctl[161] != (byte)'S' || ctl[162] != (byte)'F' ||
                ctl[163] != (byte)'A' || ctl[164] != (byte)'C' || ctl[165] != (byte)'A' ||
                ctl[166] != (byte)'D')
                throw new FileFormatException(path + ": not a valid keyed file (bad valid string)");
            if (ctl[179] != 0)
                throw new FileFormatException(path + ": unknown hashing algorithm (" + ctl[179] + ")");
            if (Le16(ctl, 180) != 0)
                throw new FileFormatException(path +
                    ": relative chain pointers - cannot extend by appending blocks");
            if (rs < 1 || rs > BlockSize - 4)
                throw new FileFormatException(path + ": record size out of range for a 4-byte-header block");
            /* GSA 4680/4690 keyed blocks are 512 B = 4 B chain header +
             * (BlockSize-4) record area; record areas may carry trailing
             * padding (GSA: 10 x 50 B slots = 500 B in a 508 B area), so
             * floor division, not an exact-division requirement. */
            int perBlock = (BlockSize - 4) / rs;
            if (perBlock < 1)
                throw new FileFormatException(path + ": no record slots per block");
            int newBlocks = (int)(oldBlocks + ((long)additionalSlots + perBlock - 1) / perBlock);
            if (newBlocks <= oldBlocks)
                throw new FileFormatException(path + ": extension produced no new blocks");
            /* u16 chain pointer ceiling: extending past it would make block
               numbers unrepresentable in fwd/bwd pointers. Fail before writing. */
            if (newBlocks - 1 > MaxDataBlocks)
                throw new CapacityException(
                    "extension would need " + (newBlocks - 1) + " data blocks but the u16 chain pointer ceiling is "
                        + MaxDataBlocks,
                    newBlocks - 1);

            // 2. append zero data blocks, streamed one 512 bytes at a time
            int newBlocksToAdd = newBlocks - oldBlocks;
            byte[] zeroBlock = new byte[BlockSize];
            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write))
            {
                for (int b = 0; b < newBlocksToAdd; b++)
                    fs.Write(zeroBlock, 0, zeroBlock.Length);
            }

            // 3. rewrite the KFCB with the new total block count
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                if (fs.Length != (long)newBlocks * BlockSize)
                    throw new FileFormatException(path + ": bad file size after append");
                SetLe32(ctl, 42, newBlocks);
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(ctl, 0, ctl.Length);
            }
            return (newBlocks - 1) * perBlock;
        }

        /// <summary>Instance form: extend this file's database by
        /// additionalSlots more record slots, then refresh the in-memory
        /// image and parameters so the newly created slots are usable by
        /// subsequent operations. Result is the new total slot count.
        /// Fails with ObjectDisposedException if the file is closed.
        /// </summary>
        public AsyncResult<int> ExtendDatabaseAsync(int additionalSlots)
        {
            return BeginOp("extend", new EalItemrWork<int>(delegate
            {
                int res = ExtendDatabaseCore(additionalSlots, _path);
                lock (_imageLock)
                {
                    LoadImage(); // pick up the rewritten KFCB + new blocks
                }
                return res;
            }));
        }

        /* ---------- planned-layout bulk import (Phase 1, workstream A) ---------- */
        /// <summary>Bulk import: build a new EALITEMR.DAT from an in-memory record
        /// set using the planned layout. All codes are known up front, so per-bucket
        /// counts are computed first and chains sized exactly - no online allocation,
        /// no stranded free slots (the Phase 0 F4 starvation cannot occur), and one
        /// streaming write pass instead of a full-file rewrite per record.
        /// Layout rules (byte-exact GSA/4690):
        ///  - all Divisor home blocks exist; bucket H's records start in block H;
        ///  - overflow blocks live strictly above block Divisor, linked into H's
        ///    chain via u16 fwd/bwd pointers. Homes are never stolen, so online
        ///    adds after import cannot collide with imported chains.
        /// Fails BEFORE writing anything if: the set is empty (ArgumentException),
        /// a code is duplicated (RecordAlreadyExistsException), any field is invalid
        /// (FieldFormatException), or the layout would exceed MaxDataBlocks data
        /// blocks (CapacityException). Replaces any existing file at path.
        /// RAM during import is O(records): packed copies + duplicate set.</summary>
        public static AsyncResult<ImportReport> ImportDatabase(IEnumerable<EalItemrRecord> records, string path)
        {
            if (path == null || path.Length == 0) throw new ArgumentException("empty path", "path");
            AsyncResult<ImportReport> r = new AsyncResult<ImportReport>();
            r.Start("import", null, new ThreadStart(delegate
            {
                Exception err = null; ImportReport res = default(ImportReport);
                try { res = RunImport(records, path); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        private static int CeilBlocks(int records)
        {
            return records / SlotsPerBlock + (records % SlotsPerBlock == 0 ? 0 : 1);
        }

        private static ImportReport RunImport(IEnumerable<EalItemrRecord> records, string path)
        {
            if (records == null) throw new ArgumentNullException("records");

            /* pass 1: validate + pack each record, group by home bucket in input
               order, reject duplicates. */
            ArrayList[] buckets = new ArrayList[Divisor + 1];
            Hashtable seen = new Hashtable(4096);
            int total = 0;
            foreach (EalItemrRecord rec in records)
            {
                if (rec.Code == null || rec.Code.Length == 0)
                    throw new FieldFormatException("record with empty code in import set");
                byte[] p = PackRecord(rec);          /* validates all fields */
                byte[] k = new byte[KeyLen];
                Array.Copy(p, 0, k, 0, KeyLen);      /* first KeyLen bytes are the key */
                if (seen[new KeyRef(k)] != null)
                    throw new RecordAlreadyExistsException(rec.Code);
                seen[new KeyRef(k)] = true;
                int hb = HashKey(k, Divisor);        /* 1..Divisor */
                if (buckets[hb] == null) buckets[hb] = new ArrayList();
                buckets[hb].Add(p);
                total++;
            }
            if (total == 0) throw new ArgumentException("import set is empty", "records");

            /* pass 2: planned layout. Homes are sacred (all Divisor home blocks
               exist); overflow lives strictly above block Divisor. */
            int overflow = 0, maxBucket = 0;
            for (int h = 1; h <= Divisor; h++)
            {
                if (buckets[h] == null) continue;
                int c = buckets[h].Count;
                if (c > maxBucket) maxBucket = c;
                overflow += CeilBlocks(c) - 1;
            }
            int nblk = Divisor + overflow;
            if (nblk > MaxDataBlocks)
                throw new CapacityException(
                    "import needs " + nblk + " data blocks but the u16 chain pointer ceiling is "
                        + MaxDataBlocks,
                    nblk);

            /* assign each bucket's overflow positions; precompute every block's
               record slice and its chain pointers. */
            int[] posOwner = new int[nblk + 1];   /* owner bucket of an overflow block; 0 otherwise */
            int[] posStart = new int[nblk + 1];   /* index into the owner list where this block's records start */
            int[] posFwd = new int[nblk + 1];     /* next in chain (0 = tail) */
            int[] posBwd = new int[nblk + 1];     /* previous in chain (0 at head) */
            int nextPos = Divisor + 1;
            for (int h = 1; h <= Divisor; h++)
            {
                if (buckets[h] == null || buckets[h].Count <= SlotsPerBlock) continue;
                int need = CeilBlocks(buckets[h].Count);   /* total blocks incl. home */
                int prev = h;
                for (int i = 1; i < need; i++)
                {
                    int pos = nextPos++;
                    posOwner[pos] = h;
                    posStart[pos] = i * SlotsPerBlock;
                    posFwd[prev] = pos;
                    posBwd[pos] = prev;
                    prev = pos;
                }
            }

            /* pass 3: single streaming write (bounded RAM). */
            byte[] ctl = BuildKfcB(path, nblk + 1);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(ctl, 0, ctl.Length);
                byte[] blk = new byte[BlockSize];
                for (int pos = 1; pos <= nblk; pos++)
                {
                    Array.Clear(blk, 0, BlockSize);
                    ArrayList list = pos <= Divisor ? buckets[pos] : buckets[posOwner[pos]];
                    if (list != null)
                        for (int s = 0; s < SlotsPerBlock && posStart[pos] + s < list.Count; s++)
                            Array.Copy((byte[])list[posStart[pos] + s], 0, blk, 4 + s * RecordSize, RecordSize);
                    SetLe16(blk, 0, posFwd[pos]);   /* fwd pointer (0 = none) */
                    SetLe16(blk, 2, posBwd[pos]);   /* bwd pointer (0 at head) */
                    fs.Write(blk, 0, BlockSize);
                }
            }

            /* self-check: stream the file back and confirm every record landed. */
            int check = CountUsedSlots(path);
            if (check != total)
                throw new EalItemrException("import self-check failed: " + check
                    + " of " + total + " records found after write");

            ImportReport rep = new ImportReport();
            rep.RecordsImported = total;
            rep.DataBlocks = nblk;
            rep.MaxBucketCount = maxBucket;
            return rep;
        }

        /* ---------- total slot count ---------- */
        /// <summary>Total number of record slots (in use + free) in an
        /// EALITEMR.DAT file = (data blocks - 1) x SlotsPerBlock. Reads only
        /// block 0 (the control block) from disk, in BlockSize (512-byte, i.e.
        /// 1-2KB) chunks, so RAM use stays bounded no matter how large the
        /// file is.</summary>
        public static int CountRecordSlots(string path)
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists)
                throw new FileFormatException("cannot open " + path);
            long sz = fi.Length;
            if (sz < BlockSize)
                throw new FileFormatException(path + ": file too small (" + sz + " bytes)");
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                byte[] ctl = new byte[BlockSize];
                int got = 0;
                while (got < BlockSize)
                {
                    int n = fs.Read(ctl, got, BlockSize - got);
                    if (n <= 0)
                        throw new FileFormatException(path + ": short read of control block");
                    got += n;
                }
                int nblk = Le32(ctl, 42);
                if (nblk < 1)
                    throw new FileFormatException(path + ": no data blocks");
                if ((long)nblk * BlockSize > sz) nblk = (int)(sz / BlockSize);
                if (nblk < 1)
                    throw new FileFormatException(path + ": no data blocks");
                return (nblk - 1) * SlotsPerBlock;
            }
        }

        /// <summary>Background-thread version of CountRecordSlots: the handle
        /// result is the total number of record slots in the file. Only block 0
        /// is read, in 512-byte chunks.</summary>
        public static AsyncResult<int> CountRecordSlotsAsync(string path)
        {
            AsyncResult<int> r = new AsyncResult<int>();
            r.Start("countslots", null, new ThreadStart(delegate
            {
                Exception err = null; int res = 0;
                try { res = CountRecordSlots(path); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /// <summary>Instance form: total number of record slots (in use + free)
        /// in this file. Reads only block 0 from disk in 512-byte chunks.</summary>
        public AsyncResult<int> CountRecordSlotsAsync()
        {
            return CountRecordSlotsAsync(_path);
        }

        /* ---------- used slot count ---------- */
        /// <summary>Number of record slots in use (non-empty key) in an
        /// EALITEMR.DAT file. Streams the data blocks from disk one 512-byte
        /// block at a time, so RAM use stays bounded no matter how large the
        /// file is (block 0 also supplies the key length / record size).</summary>
        public static int CountUsedSlots(string path)
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists)
                throw new FileFormatException("cannot open " + path);
            long sz = fi.Length;
            if (sz < BlockSize)
                throw new FileFormatException(path + ": file too small (" + sz + " bytes)");
            int klen = KeyLen;
            int rs = RecordSize;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                byte[] ctl = new byte[BlockSize];
                int got = 0;
                while (got < BlockSize)
                {
                    int n = fs.Read(ctl, got, BlockSize - got);
                    if (n <= 0)
                        throw new FileFormatException(path + ": short read of control block");
                    got += n;
                }
                if (sz % BlockSize != 0)
                    throw new FileFormatException(path + ": not a whole number of blocks");
                int nblk = (int)(sz / BlockSize);
                klen = Le16(ctl, 54); if (klen <= 0) klen = KeyLen;
                rs = Le16(ctl, 46); if (rs <= 0 || rs > BlockSize - 4) rs = RecordSize;
                int slots = (BlockSize - 4) / rs;
                fs.Seek(BlockSize, SeekOrigin.Begin); // skip control block
                int used = 0;
                byte[] blk = new byte[BlockSize];
                for (int b = 1; b < nblk; b++)
                {
                    got = 0;
                    while (got < BlockSize)
                    {
                        int n = fs.Read(blk, got, BlockSize - got);
                        if (n <= 0)
                            throw new FileFormatException(path + ": short read at block " + b);
                        got += n;
                    }
                    for (int s = 0; s < slots; s++)
                    {
                        if (!SlotEmptyAt(blk, 4 + s * rs, klen))
                        used++;
                    }
                }
                return used;
            }
        }

        /// <summary>Background-thread version of CountUsedSlots: the handle
        /// result is the number of record slots in use in the file. Data
        /// blocks are read one 512-byte block at a time.</summary>
        public static AsyncResult<int> CountUsedSlotsAsync(string path)
        {
            AsyncResult<int> r = new AsyncResult<int>();
            r.Start("countused", null, new ThreadStart(delegate
            {
                Exception err = null; int res = 0;
                try { res = CountUsedSlots(path); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /// <summary>Instance form: number of record slots in use in this
        /// file. Streams the data blocks from disk in 512-byte chunks.</summary>
        public AsyncResult<int> CountUsedSlotsAsync()
        {
            return CountUsedSlotsAsync(_path);
        }

        private void LoadImage()
        {
            FileInfo fi = new FileInfo(_path);
            if (!fi.Exists)
                throw new FileFormatException("cannot open " + _path);
            int sz = (int)fi.Length;
            if (sz < BlockSize)
                throw new FileFormatException(_path + ": file too small (" + sz + " bytes)");
            byte[] img = File.ReadAllBytes(_path);
            if (img.Length != sz)
                throw new FileFormatException(_path + ": short read");
            int nblk = Le32(img, 42);
            int rs = Le16(img, 46);
            int div = Le32(img, 48);
            int klen = Le16(img, 54);
            int thr = Le32(img, 56);
            if (rs <= 0 || rs > 508) rs = RecordSize;
            if (klen <= 0) klen = KeyLen;
            if (div <= 1) div = 999;
            if (nblk * BlockSize > sz) nblk = sz / BlockSize;
            if (nblk < 1)
                throw new FileFormatException(_path + ": no data blocks");
            /* defense in depth: a file above the u16 chain pointer ceiling
               cannot be placed into without silent pointer truncation, so
               refuse to open it rather than corrupt on first import. */
            if (nblk - 1 > MaxDataBlocks)
                throw new FileFormatException(_path + ": " + nblk + " blocks exceeds the u16 chain pointer ceiling ("
                    + MaxDataBlocks + " data blocks); cannot open safely");
            _image = img;
            _nblk = nblk; _rs = rs; _divisor = div; _klen = klen; _thr = thr;
            BuildIndex();
        }

        /* ---------- key index (Phase 1) + placement index (Phase 2) ---------- */
        /// <summary>Build the in-memory indexes by scanning every block once.
        /// Called from LoadImage before the object is visible to operations,
        /// and again after a failed batch restores the pre-batch image, so all
        /// structures are consistent by construction. Duplicate keys in a file
        /// are malformed; the last one wins.
        /// Phase 1 part: key -> location Hashtable (sized up front so a 600k
        /// file does not rehash).
        /// Phase 2 placement index (O(n) here, O(1) at use time):
        ///   _freeCount[b] = empty slots in block b (maintained on every store/delete);
        ///   _chained[b]    = true when some chain pointer references block b;
        ///   _pristine      = stack of pristine blocks ABOVE the divisor - home
        ///                    blocks are never stolen as overflow, matching the
        ///                    Phase 1 layout rule; only shrinks: a block that is
        ///                    ever allocated becomes chained and can never
        ///                    re-qualify;
        ///   _chainTail[h]  = last block of home h's overflow chain (h itself
        ///                    when it has no chain), stamped for every member so
        ///                    AppendChain is O(1).</summary>
        private void BuildIndex()
        {
            int n = _nblk;

            /* pass 1: which blocks are referenced by a chain pointer, and the
               per-block free-slot counts */
            bool[] chained = new bool[n];
            for (int blk = 1; blk < n; blk++)
            {
                int fwd = Le16(_image, blk * BlockSize);
                if (fwd >= 1 && fwd < n) chained[fwd] = true;
                int bwd = Le16(_image, blk * BlockSize + 2);
                if (bwd >= 1 && bwd < n) chained[bwd] = true;
            }
            _freeCount = new int[n];
            int used = 0;
            for (int blk = 1; blk < n; blk++)
            {
                int free = 0;
                for (int s = 0; s < SlotsPerBlock; s++)
                    if (SlotEmptyAt(_image, SlotOffset(blk, s), _klen)) free++;
                _freeCount[blk] = free;
                used += SlotsPerBlock - free;
            }

            /* pass 2: key -> location index (sized up front) */
            Hashtable ht = new Hashtable(used > 0 ? (int)(used / 0.72f) + 16 : 16);
            for (int blk = 1; blk < n; blk++)
                for (int s = 0; s < SlotsPerBlock; s++)
                {
                    int off = SlotOffset(blk, s);
                    if (SlotEmptyAt(_image, off, _klen)) continue;
                    byte[] kb = new byte[_klen];
                    Array.Copy(_image, off, kb, 0, _klen);
                    ht[new KeyRef(kb)] = (blk << 4) | s;
                }
            _index = ht;

            /* pass 3: pristine pool - blocks above the divisor that are fully
               empty and unchained. Pushed ascending; FindFree pops descending
               (highest-numbered block first). */
            _chained = chained;
            int cap = n > 0 ? n : 1;
            _pristine = new int[cap];
            _pristineCount = 0;
            for (int b = _divisor + 1; b < n; b++)
                if (_freeCount[b] == SlotsPerBlock && !chained[b])
                    _pristine[_pristineCount++] = b;

            /* pass 4: chain tails - walk each head's chain once and stamp the
               tail on every member (a block with no chain is its own tail). */
            _chainTail = new int[n];
            for (int b = 1; b < n; b++) _chainTail[b] = b;
            for (int h = 1; h < n; h++)
            {
                if (Le16(_image, h * BlockSize + 2) != 0) continue; /* not a head */
                int first = Le16(_image, h * BlockSize);
                if (first == 0) continue;                            /* no chain */
                int tail = first, guard = 0;
                while (true)
                {
                    int nxt = Le16(_image, tail * BlockSize);
                    if (nxt < 1 || nxt >= n || ++guard > n) break;   /* corrupt: stop */
                    tail = nxt;
                }
                guard = 0;
                for (int c = h; ; )
                {
                    _chainTail[c] = tail;
                    int nxt = Le16(_image, c * BlockSize);
                    if (nxt < 1 || nxt >= n || ++guard > n) break;
                    c = nxt;
                }
            }
        }

        /// <summary>Index probe/insert/remove key for an arbitrary parsed key:
        /// copies exactly _klen bytes so lengths always match stored entries.</summary>
        private KeyRef IndexKey(byte[] key)
        {
            int n = _klen < key.Length ? _klen : key.Length;
            byte[] kb = new byte[n];
            Array.Copy(key, 0, kb, 0, n);
            return new KeyRef(kb);
        }

        private void SaveImage()
        {
            /* caller holds _imageLock */
            if (_session) { _dirty = true; return; }
            AtomicWrite();
        }

        /// <summary>Atomically replace the on-disk file: write a temp file in
        /// the SAME directory, flush to disk, then rename over the target.
        /// A crash at any point leaves either the old or the new complete
        /// file - never a torn one. Caller holds _imageLock.</summary>
        private void AtomicWrite()
        {
            string tmp = _path + ".tmp" + (Interlocked.Increment(ref _tmpSeq) & 0xffff)
                + "-" + Thread.CurrentThread.ManagedThreadId;
            bool moved = false;
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(_image, 0, _image.Length);
                    /* WriteThrough forces stable storage on every write (.NET 2.0 has no
                       Flush(bool)); the extra Flush() is belt-and-suspenders before rename. */
                    fs.Flush();
                }
                try
                {
                    File.Move(tmp, _path);   /* same-directory rename: atomic replace */
                }
                catch (IOException)
                {
                    /* runtimes where Move refuses to replace an existing file:
                       delete-then-move (small non-atomic window; best effort). */
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmp, _path);
                }
                moved = true;
            }
            finally
            {
                if (!moved)
                {
                    try { File.Delete(tmp); } catch { /* best effort cleanup */ }
                }
            }
        }

        /* ---------- little-endian + BCD helpers ---------- */
        private static int Le16(byte[] b, int o)
        { return b[o] | (b[o + 1] << 8); }
        private static int Le32(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }
        private static void SetLe16(byte[] b, int o, int v)
        {
            b[o] = (byte)(v & 0xff); b[o + 1] = (byte)((v >> 8) & 0xff);
        }
        private static void SetLe32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v & 0xff); b[o + 1] = (byte)((v >> 8) & 0xff);
            b[o + 2] = (byte)((v >> 16) & 0xff); b[o + 3] = (byte)((v >> 24) & 0xff);
        }
        /// <summary>Expand n packed-BCD bytes into 2n ASCII digits.</summary>
        private static string BcdDigits(byte[] p, int o, int n)
        {
            char[] d = new char[2 * n];
            for (int i = 0; i < n; i++)
            {
                d[2 * i] = (char)('0' + ((p[o + i] >> 4) & 0x0f));
                d[2 * i + 1] = (char)('0' + (p[o + i] & 0x0f));
            }
            return new string(d);
        }
        private static bool IsAllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!char.IsDigit(s[i])) return false;
            return true;
        }
        /// <summary>Pack a decimal digit string (<= 2n digits, right-justified) into
        /// n BCD bytes; most significant digit in the high nibble of the leftmost
       /// byte used. Returns false when s is not all digits or too long.</summary>
        private static bool PackDigits(string s, byte[] outb, int o, int n)
        {
            if (s.Length > 2 * n || !IsAllDigits(s)) return false;
            Array.Clear(outb, o, n);
            for (int i = 0; i < s.Length; i++)
            {
                int fromRight = s.Length - 1 - i;
                int bytepos = n - 1 - fromRight / 2;
                if (fromRight % 2 == 1)
                    outb[o + bytepos] |= (byte)((s[i] - '0') << 4);
                else
                    outb[o + bytepos] |= (byte)(s[i] - '0');
            }
            return true;
        }
        /// <summary>Decimal value of n packed-BCD bytes (0 when any nibble is non-digit).</summary>
        private static long BcdValue(byte[] p, int o, int n)
        {
            long v = 0;
            for (int i = 0; i < 2 * n; i++)
            {
                int nib = (i % 2 == 0) ? (p[o + i / 2] >> 4) & 0x0f : p[o + i / 2] & 0x0f;
                if (nib > 9) return 0;
                v = v * 10 + nib;
            }
            return v;
        }
        private static bool IsBcdKey(byte[] p, int o, int n)
        {
            for (int i = 0; i < n; i++)
                if ((p[o + i] >> 4) > 9 || (p[o + i] & 0x0f) > 9) return false;
            return true;
        }

        /* ---------- hashing & locating (all image access under _imageLock) ---------- */
        /// <summary>Record byte offset of a slot within a block.</summary>
        private int SlotOffset(int blk, int slot)
        {
            return blk * BlockSize + 4 + slot * _rs;
        }
        private static bool SlotEmptyAt(byte[] img, int off, int klen)
        {
            for (int i = 0; i < klen; i++)
                if (img[off + i] != 0) return false;
            return true;
        }
        private static bool KeysEqual(byte[] a, int ao, byte[] b, int n)
        {
            for (int i = 0; i < n; i++)
                if (a[ao + i] != b[i]) return false;
            return true;
        }
        /// <summary>IBM folding hash: low key byte -> low acc, alternating;
        /// (H<<8|L) % divisor, remainder 0 -> divisor. Returns 0..divisor.</summary>
        private static int HashKey(byte[] key, int divisor)
        {
            int L = 0, H = 0, acc = 0;
            for (int i = key.Length - 1; i >= 0; i--)
            {
                if (acc == 0) L ^= key[i]; else H ^= key[i];
                acc ^= 1;
            }
            int F = (H << 8) | (L & 0xff);
            int r = F % divisor;
            return r == 0 ? divisor : r;
        }
        private int HashBlock(byte[] key) { return HashKey(key, _divisor); }
        /// <summary>Find a record: home block slots first, then full scan (chains).</summary>
        /// <summary>Find a record by key via the in-memory index: O(1) instead
        /// of the old O(file size) scan. Returns 0 if found, -1 if not present.
        /// The index is built at open and maintained on every mutation under
        /// _imageLock, so it is complete by construction (no fallback scan).
        /// Caller holds _imageLock.</summary>
        private int FindRec(byte[] key, int[] outLoc)
        {
            object v = _index[IndexKey(key)];
            if (v == null) return -1;
            int packed = (int)v;
            outLoc[0] = packed >> 4;
            outLoc[1] = packed & 15;
            return 0;
        }
        /// <summary>First empty slot for a new key, via the placement index:
        /// home block slots first, then home's overflow chain in order, then a
        /// pristine block (mode 2 = caller must link it into the chain). All
        /// O(1) amortized - no whole-file scans. Returns -1 when full.
        /// Note: the pool only holds blocks above the divisor and hb is always
        /// in [1, divisor], so a home block can never be stolen as overflow.</summary>
        private int FindFree(byte[] key, int[] outLoc)
        {
            int hb = HashBlock(key);
            if (hb < 1 || hb >= _nblk) return -1;   /* home block not in this file */
            if (_freeCount[hb] > 0)
                for (int s = 0; s < SlotsPerBlock; s++)
                    if (SlotEmptyAt(_image, SlotOffset(hb, s), _klen))
                    { outLoc[0] = hb; outLoc[1] = s; return 0; }
            int blk = Le16(_image, hb * BlockSize);   /* first overflow block */
            int guard = 0;
            while (blk >= 1 && blk < _nblk && guard++ < _nblk)
            {
                if (_freeCount[blk] > 0)
                    for (int s = 0; s < SlotsPerBlock; s++)
                        if (SlotEmptyAt(_image, SlotOffset(blk, s), _klen))
                        { outLoc[0] = blk; outLoc[1] = s; return 1; }
                blk = Le16(_image, blk * BlockSize);
            }
            while (_pristineCount > 0)
            {
                int b = _pristine[--_pristineCount];
                if (_freeCount[b] != SlotsPerBlock || _chained[b]) continue; /* stale entry */
                outLoc[0] = b; outLoc[1] = 0; return 2;
            }
            return -1;
        }
        /// <summary>Link a pristine block onto home's overflow chain at its tail.
        /// O(1) via _chainTail (the old version walked the whole chain).</summary>
        private void AppendChain(int home, int newblk)
        {
            int tail = _chainTail[home];
            SetLe16(_image, tail * BlockSize + 0, newblk);
            SetLe16(_image, newblk * BlockSize + 2, tail);
            _chained[newblk] = true;
            _chainTail[home] = newblk;
        }
        /// <summary>Is blk reachable in home's overflow chain (home counts)?</summary>
        private bool InChain(int home, int blk)
        {
            int cur = home, guard = 0;
            while (cur >= 1 && cur < _nblk && guard++ < _nblk)
            {
                if (cur == blk) return true;
                cur = Le16(_image, cur * BlockSize);
            }
            return false;
        }

        /* ---------- field parsing / normalization ---------- */
        /// <summary>Parse an item code into the 11-byte key: either 1-22 decimal
        /// digits (full BCD key space; store item codes are usually <= 12 / UPC-12,
        /// but real files may hold longer BCD keys) or an even-length hex byte
        /// string (<= 11 bytes, right-justified). Throws FieldFormatException.</summary>
        public static byte[] ParseKey(string code)
        {
            if (code == null || code.Length == 0)
                throw new FieldFormatException("empty item code");
            byte[] key = new byte[KeyLen];
            if (IsAllDigits(code))
            {
                if (code.Length > 2 * KeyLen)
                    throw new FieldFormatException(
                        "code has " + code.Length + " digits, max " + (2 * KeyLen));
                if (!PackDigits(code, key, 0, KeyLen))
                    throw new FieldFormatException("bad code: " + code);
                return key;
            }
            int len = code.Length;
            if (len % 2 != 0 || len / 2 > KeyLen)
                throw new FieldFormatException(
                    "code is neither all digits nor a hex byte string: " + code);
            for (int i = 0; i < len; i++)
                if (HexVal(code[i]) < 0)
                    throw new FieldFormatException("bad hex in code: " + code);
            int nb = len / 2;
            for (int i = 0; i < nb; i++)
                key[KeyLen - 1 - (nb - 1 - i)] =
                    (byte)((HexVal(code[2 * i]) << 4) | HexVal(code[2 * i + 1]));
            return key;
        }
        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
        /// <summary>Parse a price as dollars "12.99" (1-5 whole digits, 1-2
        /// fraction digits) or raw cents "1299" (1-10 digits); returns cents,
        /// 0-9999999 (<= $99,999.99). Throws FieldFormatException on malformed
        /// or out-of-range input - never clamps silently.</summary>
        public static long ParsePrice(string s)
        {
            if (s == null || s.Length == 0)
                throw new FieldFormatException("empty price");
            int dot = s.IndexOf('.');
            if (dot >= 0)
            {
                // dollars form: D+.D{1,2}, at most one dot
                if (s.IndexOf('.', dot + 1) >= 0)
                    throw new FieldFormatException("bad price: " + s);
                long whole = 0, c = 0;
                int wf = 0, ff = 0;
                for (int i = 0; i < dot; i++)
                {
                    char ch = s[i];
                    if (!char.IsDigit(ch))
                        throw new FieldFormatException("bad price: " + s);
                    wf++;
                    if (wf > 5)
                        throw new FieldFormatException("price too large: " + s);
                    whole = whole * 10 + (ch - '0');
                }
                for (int i = dot + 1; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (!char.IsDigit(ch))
                        throw new FieldFormatException("bad price: " + s);
                    ff++;
                    if (ff > 2)
                        throw new FieldFormatException("bad price: " + s);
                    c = c * 10 + (ch - '0');
                }
                if (wf == 0 || ff == 0)
                    throw new FieldFormatException("bad price: " + s);
                while (ff < 2) { c *= 10; ff++; }
                long total = whole * 100 + c;
                if (total > 9999999)
                    throw new FieldFormatException("price too large: " + s);
                return total;
            }
            // raw cents form: 1-10 digits
            if (s.Length > 10)
                throw new FieldFormatException("bad price: " + s);
            for (int i = 0; i < s.Length; i++)
                if (!char.IsDigit(s[i]))
                    throw new FieldFormatException("bad price: " + s);
            long cents = long.Parse(s);
            if (cents > 9999999)
                throw new FieldFormatException("price too large: " + s);
            return cents;
        }
        /// <summary>Format cents as dollars "12.99" (mirrors the C tool's display).</summary>
        public static string FormatPrice(long cents)
        {
            return (cents / 100).ToString() + "." + ((cents % 100) / 10).ToString()
                + (cents % 10).ToString();
        }
        /// <summary>ASCII-uppercase and truncate to 18 chars (GSA name field).</summary>
        private static string NormalizeName(string s)
        {
            if (s == null) s = string.Empty;
            int n = s.Length < NameLen ? s.Length : NameLen;
            char[] c = new char[n];
            for (int i = 0; i < n; i++)
            {
                char ch = s[i];
                if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
                c[i] = ch;
            }
            return new string(c);
        }
        /* ---------- SALEPRIC$ price-method handling (Programming Guide
           Table 101; pricing method = INDICAT2$ units digit) ---------- */
        /// <summary>Validate (primary, secondary) cents for the given pricing
        /// method and combine them into the 10-digit (0-9999999999) value stored
        /// in SALEPRIC$. Throws FieldFormatException on malformed input.</summary>
        private static long PackPrice(int method, long primary, long secondary)
        {
            if (primary < 0) throw new FieldFormatException("sale price negative");
            if (secondary < 0)
                throw new FieldFormatException("secondary sale price negative");
            if (method == 0 || method == 1)
            {
                // 000 ppppppp: lower 7 digits = unit (m0) or package (m1) price.
                if (primary > 9999999L)
                    throw new FieldFormatException(
                        "sale price must be 0-9999999 cents for pricing method " + method);
                if (secondary > 999)
                    throw new FieldFormatException(
                        "reserved SALEPRIC$ digits must be 000-999 for method " + method);
                return primary + secondary * 10000000L;
            }
            if (method == 2)
            {
                // pppppsssss: primary = single unit, secondary = package price.
                if (primary > 99999L || secondary > 99999L)
                    throw new FieldFormatException(
                        "sale prices must be 0-99999 cents for pricing method 2");
                return secondary * 100000L + primary;
            }
            if (method == 3)
            {
                // uuuuurrrrr: primary = unit price, secondary = reduced price.
                if (primary > 99999L || secondary > 99999L)
                    throw new FieldFormatException(
                        "sale prices must be 0-99999 cents for pricing method 3");
                return primary * 100000L + secondary;
            }
            if (primary > PriceMaxCents)
                throw new FieldFormatException("sale price must be 0-" + PriceMaxCents + " cents");
            throw new FieldFormatException("undefined pricing method " + method);
        }
        private static string NormalizeUser(string s)
        {
            if (s == null) s = string.Empty;
            return s.Length <= UserLen ? s : s.Substring(0, UserLen);
        }

        /* ---------- record (de)serialization: the one-shot path ---------- */
        /// <summary>Pack a full record into 50 bytes, validating every field.
        /// This is what WriteRecordAsync/AddRecordAsync store.</summary>
        private static byte[] PackRecord(EalItemrRecord rec)
        {
            byte[] key = ParseKey(rec.Code);
            if (rec.Indicat2Validation > 8)
                throw new FieldFormatException("indic2 validation must be 0-8");
            if (rec.Indicat2Pricing > 3)
                throw new FieldFormatException("indic2 pricing must be 0-3");
            if (rec.Department != null &&
                (rec.Department.Length > 6 || !IsAllDigits(rec.Department)))
                throw new FieldFormatException("dept must be <= 6 digits");
            if (rec.SaleQuan < 0 || rec.SaleQuan > 99)
                throw new FieldFormatException("sale quantity must be 0-99");
            long priceRaw = PackPrice(rec.Indicat2Pricing,
                rec.SalePriceCents, rec.SalePrice2Cents);
            if (rec.MpGroup < 0 || rec.MpGroup > 99)
                throw new FieldFormatException("mpg must be 0-99");

            byte[] r = new byte[RecordSize];
            Array.Copy(key, 0, r, 0, KeyLen);
            r[11] = rec.Indicat0;
            r[12] = rec.Indicat1;
            PackDigits(rec.Indicat2Validation.ToString() + rec.Indicat2Pricing.ToString(),
                r, 13, 1);
            if (!PackDigits(rec.Department ?? string.Empty, r, 14, 3))
                throw new FieldFormatException("dept must be <= 6 digits");
            PackDigits(rec.SaleQuan.ToString().PadLeft(2, '0'), r, 17, 1);
            if (!PackDigits(priceRaw.ToString(), r, 18, 5))
                throw new FieldFormatException("sale price is out of range for this pricing method");
            PackDigits(rec.MpGroup.ToString().PadLeft(2, '0'), r, 23, 1);
            byte[] name = AsciiBytes(NormalizeName(rec.ItemName));
            Array.Copy(name, 0, r, 24, name.Length);
            byte[] user = AsciiBytes(NormalizeUser(rec.User));
            Array.Copy(user, 0, r, 42, user.Length);
            // ASC fields are blank-padded: 4680 WRITE FORM pads fixed C fields
            // with blanks and GSA explicitly pads ITEMNAME$ to 18 (EALHS073.J86,
            // EALHS1VC.BAS). Every record in production EALITEMR files is
            // space-padded; NUL padding would leak into GSA displays/compares.
            for (int i = 24 + name.Length; i < 24 + NameLen; i++) r[i] = (byte)' ';
            for (int i = 42 + user.Length; i < 42 + UserLen; i++) r[i] = (byte)' ';
            return r;
        }
        private static byte[] AsciiBytes(string s)
        {
            byte[] b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }
        /// <summary>Decode 50 stored bytes into a record (values as stored).</summary>
        private static EalItemrRecord UnpackRecord(byte[] p, int off)
        {
            EalItemrRecord r = new EalItemrRecord();
            byte[] key = new byte[KeyLen];
            Array.Copy(p, off, key, 0, KeyLen);
            r.Code = IsBcdKey(key, 0, KeyLen) ? BcdKeyString(key) : HexKeyString(key);
            r.Indicat0 = p[off + 11];
            r.Indicat1 = p[off + 12];
            byte ind2 = p[off + 13];
            if (ind2 / 16 > 9 || ind2 % 16 > 9)
                throw new FieldFormatException("indic2 is not packed BCD: "
                    + ind2.ToString("x2"));
            r.Indicat2Validation = (byte)(ind2 / 16);
            r.Indicat2Pricing = (byte)(ind2 % 16);
            r.Department = BcdKeyString(Substring(p, off + 14, 3));
            r.SaleQuan = (int)BcdValue(p, off + 17, 1);
            long priceRaw = BcdValue(p, off + 18, 5);
            int pm = r.Indicat2Pricing;
            if (pm == 2)
            {
                // pppppsssss: unit = lower 5 digits, package = upper 5.
                r.SalePriceCents = priceRaw % 100000L;
                r.SalePrice2Cents = priceRaw / 100000L;
            }
            else if (pm == 3)
            {
                // uuuuurrrrr: unit = upper 5 digits, reduced = lower 5.
                r.SalePriceCents = priceRaw / 100000L;
                r.SalePrice2Cents = priceRaw % 100000L;
            }
            else if (pm == 0 || pm == 1)
            {
                // 000ppppppp: lower 7 digits = price; top 3 are reserved.
                r.SalePriceCents = priceRaw % 10000000L;
                r.SalePrice2Cents = priceRaw / 10000000L;
            }
            else
            {
                // Undefined method: return the stored value whole.
                r.SalePriceCents = priceRaw;
                r.SalePrice2Cents = 0;
            }
            r.MpGroup = (int)BcdValue(p, off + 23, 1);
            r.ItemName = TrimTrailing(ZeroTerminated(p, off + 24, NameLen));
            r.User = ZeroTerminated(p, off + 42, UserLen);
            return r;
        }
        private static string TrimTrailing(string s)
        {
            int end = s.Length;
            while (end > 0 && s[end - 1] == ' ') end--;
            return s.Substring(0, end);
        }
        private static byte[] Substring(byte[] p, int off, int n)
        {
            byte[] b = new byte[n];
            Array.Copy(p, off, b, 0, n);
            return b;
        }
        private static string BcdKeyString(byte[] key)
        {
            string d = BcdDigits(key, 0, key.Length);
            int cut = 0;
            while (cut < d.Length - 1 && d[cut] == '0') cut++;
            return d.Substring(cut);
        }
        private static string HexKeyString(byte[] key)
        {
            string s = string.Empty;
            for (int i = 0; i < key.Length; i++)
                s += ((int)key[i]).ToString("x2");
            return s;
        }
        private static string ZeroTerminated(byte[] p, int off, int max)
        {
            int i = 0;
            while (i < max && p[off + i] != 0) i++;
            string s = string.Empty;
            for (int j = 0; j < i; j++) s += (char)p[off + j];
            return s;
        }

        /* ---------- async operation launcher ---------- */
        private AsyncResult<T> BeginOp<T>(string opName, EalItemrWork<T> work)
        {
            AsyncResult<T> r = new AsyncResult<T>();
            lock (_opLock)
            {
                if (_closed)
                {
                    r.Finish(new ObjectDisposedException(
                        "EalItemrFile", "file is closed"));
                    return r;
                }
                _activeOps.Add(r);
            }
            r.Start(opName, this, new ThreadStart(delegate
            {
                Exception err = null; T res = default(T);
                try { res = work(); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        /* ---------- operations ---------- */
        /// <summary>Read one record by item code. Result is null when the code
        /// is not in the file (no exception for a plain miss).</summary>
        public AsyncResult<EalItemrRecord?> GetRecordAsync(string code)
        {
            return BeginOp("get", new EalItemrWork<EalItemrRecord?>(delegate
            {
                byte[] key = ParseKey(code);
                lock (_imageLock)
                {
                    int[] loc = new int[2];
                    if (FindRec(key, loc) < 0) return null;
                    return UnpackRecord(_image, SlotOffset(loc[0], loc[1]));
                }
            }));
        }

        /// <summary>List records in block/slot scan order. limit 0 = all records,
        /// otherwise at most the first limit rows.</summary>
        public AsyncResult<RecordListResult> ListRecordsAsync(int limit)
        {
            return BeginOp("list", new EalItemrWork<RecordListResult>(delegate
            {
                lock (_imageLock)
                {
                    ArrayList rows = new ArrayList();
                    for (int blk = 1; blk < _nblk; blk++)
                        for (int s = 0; s < SlotsPerBlock; s++)
                        {
                            if (limit != 0 && rows.Count >= limit) break;
                            int off = SlotOffset(blk, s);
                            if (SlotEmptyAt(_image, off, _klen)) continue;
                            EalItemrRecord r = UnpackRecord(_image, off);
                            ItemrRow row = new ItemrRow();
                            row.Code = r.Code;
                            row.Name = r.ItemName;
                            row.Department = r.Department;
                            row.PriceCents = r.SalePriceCents;
                            row.Price2Cents = r.SalePrice2Cents;
                            row.Validation = r.Indicat2Validation;
                            int? pmr = null;
                            if (r.Indicat2Pricing <= 3) pmr = (int)r.Indicat2Pricing;
                            row.PricingMethod = pmr;
                            rows.Add(row);
                        }
                    RecordListResult res = new RecordListResult();
                    res.Rows = (ItemrRow[])rows.ToArray(typeof(ItemrRow));
                    res.Count = res.Rows.Length;
                    return res;
                }
            }));
        }

        /// <summary>Decode every record in the file into an in-memory browse table
        /// (Workstream B). Rows come out in block/slot scan order - identical to
        /// ListRecordsAsync's order. Extraction is parallel over contiguous block
        /// ranges; K == 1 or a small file falls back to the serial path.
        /// The result is a RAM snapshot: later mutations do not change it.</summary>
        public AsyncResult<BrowseTable> BrowseAsync()
        {
            return BeginOp("browse", new EalItemrWork<BrowseTable>(delegate
            {
                return BuildBrowseTable(Environment.ProcessorCount);
            }));
        }

        /// <summary>BrowseAsync with an explicit worker count (workerCount &lt; 1 throws).</summary>
        public AsyncResult<BrowseTable> BrowseAsync(int workerCount)
        {
            if (workerCount < 1) throw new ArgumentException("workerCount must be >= 1", "workerCount");
            return BeginOp("browse", new EalItemrWork<BrowseTable>(delegate
            {
                return BuildBrowseTable(workerCount);
            }));
        }

        /* ---------- browse extraction (Workstream B) ---------- */
        private sealed class BrowseExtractJob
        {
            public int Worker;      /* index into Buffers */
            public int Lo;          /* first block number (1-based, inclusive) */
            public int Hi;          /* one past last block number */
            public EalItemrRecord[][] Buffers;
        }

        private BrowseTable BuildBrowseTable(int K)
        {
            lock (_imageLock)
            {
                int blocks = _nblk - 1;   /* data blocks, numbered 1..blocks */
                if (K <= 1 || blocks * SlotsPerBlock < MinRowsForParallelism) K = 1;

                Stopwatch sw = Stopwatch.StartNew();
                EalItemrRecord[] rows;
                if (K == 1)
                {
                    int total = CountNonEmpty(1, blocks);
                    rows = new EalItemrRecord[total];
                    FillRows(rows, 0, 1, blocks);
                }
                else
                {
                    /* Contiguous disjoint block ranges; worker i owns [starts[i], starts[i+1]). */
                    int[] starts = new int[K + 1];
                    for (int i = 0; i <= K; i++)
                        starts[i] = 1 + (int)((long)blocks * i / K);

                    EalItemrRecord[][] bufs = new EalItemrRecord[K][];
                    Thread[] ths = new Thread[K];
                    for (int i = 0; i < K; i++)
                    {
                        if (starts[i + 1] <= starts[i]) { bufs[i] = new EalItemrRecord[0]; continue; }
                        BrowseExtractJob job = new BrowseExtractJob();
                        job.Worker = i;
                        job.Lo = starts[i];
                        job.Hi = starts[i + 1];
                        job.Buffers = bufs;
                        ths[i] = new Thread(new ParameterizedThreadStart(ExtractRange));
                        ths[i].IsBackground = true;
                        ths[i].Start(job);
                    }
                    for (int i = 0; i < K; i++) ths[i].Join();

                    int total = 0;
                    for (int i = 0; i < K; i++) total += bufs[i].Length;
                    rows = new EalItemrRecord[total];
                    int pos = 0;
                    for (int i = 0; i < K; i++)
                    {
                        Array.Copy(bufs[i], 0, rows, pos, bufs[i].Length);
                        pos += bufs[i].Length;
                    }
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                return new BrowseTable(rows, K, ms);
            }
        }

        /* Worker body: count + decode its own block range into a private buffer.
         * Reads only (the op thread holds _imageLock for the whole extraction). */
        private void ExtractRange(object state)
        {
            BrowseExtractJob job = (BrowseExtractJob)state;
            int n = CountNonEmpty(job.Lo, job.Hi);
            EalItemrRecord[] buf = new EalItemrRecord[n];
            FillRows(buf, 0, job.Lo, job.Hi);
            job.Buffers[job.Worker] = buf;
        }

        /* Count non-empty slots in block range [lo, hi) (1-based block numbers). */
        private int CountNonEmpty(int lo, int hi)
        {
            byte[] img = _image;
            int n = 0;
            for (int blk = lo; blk < hi; blk++)
                for (int s = 0; s < SlotsPerBlock; s++)
                    if (!SlotEmptyAt(img, SlotOffset(blk, s), _klen)) n++;
            return n;
        }

        /* Decode non-empty slots of block range [lo, hi) into buf starting at pos. */
        private void FillRows(EalItemrRecord[] buf, int pos, int lo, int hi)
        {
            byte[] img = _image;
            for (int blk = lo; blk < hi; blk++)
                for (int s = 0; s < SlotsPerBlock; s++)
                    if (!SlotEmptyAt(img, SlotOffset(blk, s), _klen))
                        buf[pos++] = UnpackRecord(img, SlotOffset(blk, s));
        }

        /// <summary>Locate an existing slot for key, or allocate a free one.
        /// Caller holds _imageLock. Returns [block, slot, created(0/1)].</summary>
        private int[] Place(byte[] key, string code, bool failIfExists)
        {
            int[] loc = new int[2];
            if (FindRec(key, loc) >= 0)
            {
                if (failIfExists)
                    throw new RecordAlreadyExistsException(code);
                return new int[] { loc[0], loc[1], 0 };
            }
            int mode = FindFree(key, loc);
            if (mode < 0) throw new FileFullException();
            if (mode == 2) AppendChain(HashBlock(key), loc[0]);
            _index[IndexKey(key)] = (loc[0] << 4) | loc[1];   /* index maintenance */
            return new int[] { loc[0], loc[1], 1 };
        }

        /// <summary>Write a packed record into a slot and keep the placement index
        /// in sync. fresh=true means an empty->occupied transition (decrement the
        /// block's free-slot count); an update of an already-occupied slot must
        /// not touch it. Caller holds _imageLock.</summary>
        private void StoreRecord(int blk, int slot, byte[] packed, bool fresh)
        {
            int off = SlotOffset(blk, slot);
            if (fresh)
            {
                Array.Clear(_image, off, _rs);   /* belt-and-braces: slot was empty */
                if (_freeCount[blk] > 0) _freeCount[blk]--;   /* empty -> occupied */
            }
            Array.Copy(packed, 0, _image, off, RecordSize);
        }

        /// <summary>Add a new record. Fails with RecordAlreadyExistsException when
        /// the code is present. Persisted immediately.</summary>
        public AsyncResult<WriteResult> AddRecordAsync(EalItemrRecord rec)
        {
            return BeginOp("add", new EalItemrWork<WriteResult>(delegate
            {
                byte[] packed = PackRecord(rec);      /* validate before locking */
                lock (_imageLock)
                {
                    int[] loc = Place(ParseKey(rec.Code), rec.Code, true);
                    int off = SlotOffset(loc[0], loc[1]);
                    StoreRecord(loc[0], loc[1], packed, loc[2] == 1);
                    SaveImage();
                    return MakeWriteResult(rec, off, loc);
                }
            }));
        }

        /// <summary>Write an entire record in one shot: all 50 bytes are packed
        /// from <paramref name="rec"/> and the record is created or replaced
        /// atomically with respect to other operations. Persisted immediately.</summary>
        public AsyncResult<WriteResult> WriteRecordAsync(EalItemrRecord rec)
        {
            return BeginOp("write", new EalItemrWork<WriteResult>(delegate
            {
                byte[] packed = PackRecord(rec);
                lock (_imageLock)
                {
                    int[] loc = Place(ParseKey(rec.Code), rec.Code, false);
                    int off = SlotOffset(loc[0], loc[1]);
                    StoreRecord(loc[0], loc[1], packed, loc[2] == 1); /* fresh slot? */
                    SaveImage();
                    return MakeWriteResult(rec, off, loc);
                }
            }));
        }
        private WriteResult MakeWriteResult(EalItemrRecord rec, int off, int[] loc)
        {
            WriteResult w = new WriteResult();
            w.Record = UnpackRecord(_image, off);
            w.Block = loc[0];
            w.Slot = loc[1];
            w.Created = loc[2] == 1;
            return w;
        }

        /// <summary>Zero out the record (GSA delete). Result is false when the
        /// code was not in the file. Persisted immediately.</summary>
        public AsyncResult<bool> DeleteRecordAsync(string code)
        {
            return BeginOp("del", new EalItemrWork<bool>(delegate
            {
                byte[] key = ParseKey(code);
                lock (_imageLock)
                {
                    int[] loc = new int[2];
                    if (FindRec(key, loc) < 0) return false;
                    _index.Remove(IndexKey(key));   /* index maintenance */
                    Array.Clear(_image, SlotOffset(loc[0], loc[1]), _rs);
                    if (_freeCount[loc[0]] < SlotsPerBlock) _freeCount[loc[0]]++;
                    SaveImage();
                    return true;
                }
            }));
        }

        /* ---------- batch upsert pipeline (Phase 2) ---------- */

        /// <summary>Import a whole batch of rows into this file in one operation.
        /// Pipeline: (A) validate + pack every row - collect-all-then-refuse, so
        /// any invalid row aborts the batch with BatchValidationException listing
        /// EVERY bad row and nothing is placed or committed; (B) place all rows
        /// in input order through the same upsert path as single writes: existing
        /// key = 50-byte in-place overwrite, new key = free slot + index update,
        /// intra-batch duplicates resolve last-row-wins; (C) commit exactly once -
        /// one AtomicWrite, or deferred when a session is open. A batch that runs
        /// out of capacity mid-placement restores the pre-batch image and indexes;
        /// disk was never touched either way.
        /// rows may be any IEnumerable&lt;EalItemrRecord&gt; (e.g. a streaming CSV
        /// reader); it is enumerated once, on the worker thread.
        /// For the parallel validate/pack variant see ImportBatchParallelAsync().</summary>
        public AsyncResult<BatchReport> ImportBatchAsync(IEnumerable<EalItemrRecord> rows)
        {
            return BeginOp("batch", new EalItemrWork<BatchReport>(delegate
            {
                return RunBatch(rows);
            }));
        }

        private BatchReport RunBatch(IEnumerable<EalItemrRecord> rows)
        {
            if (rows == null) throw new ArgumentNullException("rows");

            /* Pass A - validate + pack every row (pure; no image access).
               Collect-all-then-refuse: report EVERY bad row, commit nothing. */
            Stopwatch sw = new Stopwatch();
            sw.Start();
            ArrayList packedL = new ArrayList();
            ArrayList errors = new ArrayList();
            int rowNo = 0;
            foreach (EalItemrRecord rec in rows)
            {
                rowNo++;
                try { packedL.Add(PackRecord(rec)); }
                catch (FieldFormatException ex)
                {
                    BatchError be = new BatchError();
                    be.Row = rowNo;
                    be.Code = rec.Code;
                    be.Message = ex.Message;
                    errors.Add(be);
                }
            }
            sw.Stop();
            double validateMs = sw.Elapsed.TotalMilliseconds;
            if (errors.Count > 0)
                throw new BatchValidationException((BatchError[])errors.ToArray(typeof(BatchError)));
            if (packedL.Count == 0)
                throw new ArgumentException("batch import set is empty", "rows");

            byte[][] packed = new byte[packedL.Count][];
            for (int i = 0; i < packedL.Count; i++) packed[i] = (byte[])packedL[i];

            BatchReport rep = PlaceAndCommit(packed);
            rep.ValidateMs = validateMs;
            rep.Workers = 1;
            return rep;
        }

        /* Pass B + C shared by the serial and parallel batch paths: upsert all
           packed rows in input order, then exactly one commit. Identical code
           for both tiers - that is what makes their output files byte-identical
           (differential-tested). */
        private BatchReport PlaceAndCommit(byte[][] packed)
        {
            int created = 0, updated = 0;
            long bytesWritten = 0;
            double placeMs = 0, commitMs = 0;
            Stopwatch sw = new Stopwatch();
            lock (_imageLock)
            {
                /* pre-batch snapshot: a failed batch must leave the file object
                   exactly as it was before the call (disk is untouched either
                   way - the single commit happens only after all rows placed).
                   Restoring also rebuilds every placement structure from the
                   image, so free counts / pool / tails stay consistent. */
                byte[] snapImg = (byte[])_image.Clone();

                sw.Start();
                try
                {
                    for (int i = 0; i < packed.Length; i++)
                    {
                        byte[] p = packed[i];
                        byte[] key = new byte[KeyLen];
                        Array.Copy(p, 0, key, 0, KeyLen);
                        int[] loc = Place(key, null, false);   /* upsert */
                        StoreRecord(loc[0], loc[1], p, loc[2] == 1); /* fresh? */
                        if (loc[2] == 1) created++; else updated++;
                    }
                }
                catch (FileFullException ex)
                {
                    _image = snapImg;
                    BuildIndex();
                    throw new FileFullException("batch import failed at row " + (created + updated + 1)
                        + " of " + packed.Length + ": " + ex.Message);
                }
                sw.Stop();
                placeMs = sw.Elapsed.TotalMilliseconds;

                /* the batch's single commit. SaveImage() defers to a session
                   when one is open (_dirty), else AtomicWrite(). */
                sw.Reset(); sw.Start();   /* .NET 2.0 has no Stopwatch.Restart() */
                SaveImage();
                sw.Stop();
                commitMs = sw.Elapsed.TotalMilliseconds;
                bytesWritten = _session ? 0 : (long)_image.Length;
            }

            BatchReport rep = new BatchReport();
            rep.Rows = packed.Length;
            rep.Created = created;
            rep.Updated = updated;
            rep.PlaceMs = placeMs;
            rep.CommitMs = commitMs;
            rep.BytesWritten = bytesWritten;
            rep.Placement = "serial";
            return rep;
        }

        /* ---------- parallel batch front half (Phase 3) ---------- */

        /// <summary>Minimum row count for the parallel validate/pack phase to be
        /// worth spawning worker threads. Below it, ImportBatchParallelAsync()
        /// runs the serial path and reports Workers == 1.</summary>
        public const int MinRowsForParallelism = 1000;

        /// <summary>One worker's slice of the parallel validate/pack phase: a
        /// contiguous row range. The job owns its error list and fault slot; it
        /// writes only its own packed[] slots, so no synchronization is needed.</summary>
        private sealed class PackJob
        {
            public EalItemrRecord[] arr;
            public int start;
            public int len;
            public byte[][] packed;
            public ArrayList errors;
            public Exception fault;
        }

        /// <summary>Validate + pack one worker's row range (pure work: PackRecord
        /// is static and touches no shared state). Runs on the worker thread.</summary>
        private void PackRange(PackJob job)
        {
            try
            {
                int end = job.start + job.len;
                for (int i = job.start; i < end; i++)
                {
                    EalItemrRecord rec = job.arr[i];
                    try { job.packed[i] = PackRecord(rec); }
                    catch (FieldFormatException ex)
                    {
                        BatchError be = new BatchError();
                        be.Row = i + 1;
                        be.Code = rec.Code;
                        be.Message = ex.Message;
                        job.errors.Add(be);   /* single owner: no lock */
                    }
                }
            }
            catch (Exception ex) { job.fault = ex; }   /* rethrown after Join, keeps the process alive */
        }

        /// <summary>Merge per-worker validation errors into one deterministic
        /// report (ascending row order - same as the serial path).</summary>
        private static BatchValidationException BuildBatchFailure(PackJob[] jobs)
        {
            ArrayList all = new ArrayList();
            for (int i = 0; i < jobs.Length; i++)
                for (int j = 0; j < jobs[i].errors.Count; j++)
                    all.Add(jobs[i].errors[j]);
            BatchError[] errs = (BatchError[])all.ToArray(typeof(BatchError));
            Array.Sort(errs, new BatchErrorByRow());
            return new BatchValidationException(errs);
        }

        private sealed class BatchErrorByRow : IComparer<BatchError>
        {
            public int Compare(BatchError a, BatchError b) { return a.Row.CompareTo(b.Row); }
        }

        /// <summary>Import a whole batch of rows with the validate/pack phase
        /// spread across worker threads (auto count = ProcessorCount). Placement
        /// and commit are identical to ImportBatchAsync(): same upsert order,
        /// one commit - so both entry points produce byte-identical files.</summary>
        public AsyncResult<BatchReport> ImportBatchParallelAsync(IEnumerable<EalItemrRecord> rows)
        {
            return BeginOp("batchpar", new EalItemrWork<BatchReport>(delegate
            {
                return RunBatchParallel(rows, 0);
            }));
        }

        /// <summary>Import a whole batch of rows with the validate/pack phase
        /// spread across <paramref name="workerCount"/> worker threads.
        /// Whitelist-only concurrency (Thread + Join; no TPL): static contiguous
        /// partitioning, each worker writes only its own packed[] slots and its
        /// own error list - zero locks in the parallel phase. Collect-all-then-
        /// refuse validation is preserved: any invalid row rejects the whole
        /// batch with BatchValidationException (errors sorted by row) and nothing
        /// is placed or committed. Placement then runs serially under _imageLock
        /// in input order through the exact same code as ImportBatchAsync(), so
        /// both tiers are byte-identical. workerCount &lt; 1 throws; K == 1 or a
        /// batch smaller than MinRowsForParallelism falls back to the serial
        /// path (report.Workers == 1).</summary>
        public AsyncResult<BatchReport> ImportBatchParallelAsync(IEnumerable<EalItemrRecord> rows, int workerCount)
        {
            if (workerCount < 1)
                throw new ArgumentException("workerCount must be >= 1", "workerCount");
            return BeginOp("batchpar", new EalItemrWork<BatchReport>(delegate
            {
                return RunBatchParallel(rows, workerCount);
            }));
        }

        private BatchReport RunBatchParallel(IEnumerable<EalItemrRecord> rows, int requestedK)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            EalItemrRecord[] arr = MaterializeRows(rows);

            int K = requestedK > 0 ? requestedK : Environment.ProcessorCount;
            if (arr.Length < MinRowsForParallelism || K <= 1)
                return RunBatch(arr);   /* serial path, identical behavior */
            if (K > arr.Length) K = arr.Length;

            byte[][] packed;
            double validateMs;
            ValidatePackParallel(arr, K, out packed, out validateMs);

            /* Pass B + C - serial placement in input order + one commit, the
               exact same code as the serial path. */
            BatchReport rep = PlaceAndCommit(packed);
            rep.ValidateMs = validateMs;
            rep.Workers = K;
            return rep;
        }

        /// <summary>Materialize a row sequence once (workers need random access)
        /// and reject an empty batch. Shared by the parallel tiers.</summary>
        private static EalItemrRecord[] MaterializeRows(IEnumerable<EalItemrRecord> rows)
        {
            ArrayList list = new ArrayList();
            foreach (EalItemrRecord rec in rows) list.Add(rec);
            if (list.Count == 0)
                throw new ArgumentException("batch import set is empty", "rows");
            return (EalItemrRecord[])list.ToArray(typeof(EalItemrRecord));
        }

        /// <summary>Parallel validate/pack front half (shared by the Phase 3 and
        /// Phase 4 tiers). Contiguous partitioning: each worker owns a disjoint
        /// row range and writes only its own packed[] slots / error list -> no
        /// locks at all in the parallel phase. Collect-all-then-refuse is
        /// preserved: any invalid row rejects the whole batch with
        /// BatchValidationException (errors sorted by ascending row - same as
        /// the serial path) and nothing is placed or committed.</summary>
        private void ValidatePackParallel(EalItemrRecord[] arr, int K,
                out byte[][] packed, out double validateMs)
        {
            PackJob[] jobs = new PackJob[K];
            Thread[] ths = new Thread[K];
            packed = new byte[arr.Length][];
            int baseLen = arr.Length / K, rem = arr.Length % K;
            for (int w = 0; w < K; w++)
            {
                PackJob job = new PackJob();
                job.arr = arr;
                job.start = w * baseLen + Math.Min(w, rem);
                job.len = baseLen + (w < rem ? 1 : 0);
                job.packed = packed;
                job.errors = new ArrayList();
                jobs[w] = job;
                ths[w] = new Thread(new ThreadStart(delegate { PackRange(job); }));
                ths[w].Name = "ealitemr-pack-" + w;
                ths[w].IsBackground = true;
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();
            for (int w = 0; w < K; w++) ths[w].Start();
            for (int w = 0; w < K; w++) ths[w].Join();
            sw.Stop();
            validateMs = sw.Elapsed.TotalMilliseconds;

            Exception fault = null;
            int errCount = 0;
            for (int w = 0; w < K; w++)
            {
                if (jobs[w].fault != null && fault == null) fault = jobs[w].fault;
                errCount += jobs[w].errors.Count;
            }
            if (fault != null) throw fault;
            if (errCount > 0) throw BuildBatchFailure(jobs);
        }

        /* ---------- sharded fused placement (Phase 4) ---------- */

        /// <summary>One shard's state for one pass of the sharded placement
        /// phase. A shard exclusively owns a contiguous range of home buckets;
        /// every per-home array below is touched only by this shard's worker
        /// thread, so no synchronization is needed in the parallel phase.</summary>
        private sealed class ShardJob
        {
            public int id;
            public bool dry;              /* true = demand pass (zero shared mutation) */
            public int loHome;            /* first owned home block */
            public int nHomes;            /* number of owned homes */
            public int[] rows;            /* row indices, input order preserved */
            public byte[][] packed;       /* shared read-only */
            public int[] homes;           /* per-row home block (shared read-only) */
            /* mutable simulation state, one entry per owned home: */
            public int[] stage;           /* 0 = home block, 1 = original chain, 2 = new allocations */
            public int[] pos;             /* cursor into chainBlks[hi] while stage == 1 */
            public int[] homeFr;          /* free slots left in the owned home block */
            public int[][] chainBlks;     /* [hi] -> original overflow blocks in walk order */
            public int[][] chainFr;       /* [hi] -> parallel mutable free-slot counts */
            public int[] allocBlk;        /* [hi] -> most recently allocated block (dry: dummy 1) */
            public int[] allocFree;       /* [hi] -> free slots left in that allocation */
            /* outputs / inputs: */
            public Hashtable localIndex;  /* KeyRef -> packed location (real pass) or marker (demand pass) */
            public int demand;            /* demand-pass output: pristine blocks needed */
            public int[] slice;           /* real-pass input: allocated pristine blocks, ascending */
            public int sliceTop;          /* pop from the top (highest block first, like serial) */
            public int created;
            public int updated;
            public Exception fault;
        }

        /// <summary>Home block of every packed row. Same 11-byte key and hash as
        /// FindFree/HashBlock use during serial placement.</summary>
        private int[] ComputeHomes(byte[][] packed)
        {
            int n = packed.Length;
            int[] homes = new int[n];
            byte[] key = new byte[KeyLen];
            for (int i = 0; i < n; i++)
            {
                Array.Copy(packed[i], 0, key, 0, KeyLen);
                homes[i] = HashBlock(key);
            }
            return homes;
        }

        /// <summary>Assign every row to the shard owning its home bucket.
        /// Shard s owns a contiguous equal-ish split of [1..divisor]; input
        /// order is preserved within each shard's row list.</summary>
        private int[][] PartitionRows(int[] homes, int K)
        {
            int baseH = _divisor / K, remH = _divisor % K;
            int[] homeShard = new int[_divisor + 1];
            for (int s = 0; s < K; s++)
            {
                int lo = 1 + s * baseH + Math.Min(s, remH);
                int n = baseH + (s < remH ? 1 : 0);
                for (int h = lo; h < lo + n && h <= _divisor; h++) homeShard[h] = s;
            }
            int[] counts = new int[K];
            for (int i = 0; i < homes.Length; i++)
            {
                int h = homes[i];
                if (h >= 1 && h <= _divisor) counts[homeShard[h]]++;
            }
            int[][] rowsByShard = new int[K][];
            for (int s = 0; s < K; s++) rowsByShard[s] = new int[counts[s]];
            int[] fill = new int[K];
            for (int i = 0; i < homes.Length; i++)
            {
                int h = homes[i];
                if (h < 1 || h > _divisor) continue;   /* degenerate file: the pass will fail it */
                int s = homeShard[h];
                rowsByShard[s][fill[s]++] = i;
            }
            return rowsByShard;
        }

        /// <summary>Build one shard's job state for a pass. The original overflow
        /// chain of each owned home is snapshotted (block ids + free counts) so
        /// the pass can simulate slot consumption with purely local arrays.</summary>
        private ShardJob[] MakeShardJobs(int[][] rowsByShard, byte[][] packed,
                int[] homes, int K, bool dry)
        {
            ShardJob[] jobs = new ShardJob[K];
            int baseH = _divisor / K, remH = _divisor % K;
            for (int s = 0; s < K; s++)
            {
                ShardJob j = new ShardJob();
                j.id = s;
                j.dry = dry;
                j.loHome = 1 + s * baseH + Math.Min(s, remH);
                j.nHomes = baseH + (s < remH ? 1 : 0);
                j.rows = rowsByShard[s];
                j.packed = packed;
                j.homes = homes;
                int cap = j.rows.Length > 4 ? (int)(j.rows.Length / 0.72f) + 8 : 8;
                j.localIndex = new Hashtable(cap);
                j.stage = new int[j.nHomes];
                j.pos = new int[j.nHomes];
                j.homeFr = new int[j.nHomes];
                j.chainBlks = new int[j.nHomes][];
                j.chainFr = new int[j.nHomes][];
                j.allocBlk = new int[j.nHomes];
                j.allocFree = new int[j.nHomes];
                for (int hi = 0; hi < j.nHomes; hi++)
                {
                    int h = j.loHome + hi;
                    if (h >= _nblk)   /* degenerate file: home block does not exist */
                    {
                        j.chainBlks[hi] = new int[0];
                        j.chainFr[hi] = new int[0];
                        continue;
                    }
                    j.homeFr[hi] = _freeCount[h];
                    /* walk the original overflow chain (read-only snapshot) */
                    ArrayList blks = new ArrayList();
                    ArrayList frs = new ArrayList();
                    int blk = Le16(_image, h * BlockSize);   /* 0 when no chain yet */
                    int guard = 0;
                    while (blk >= 1 && blk < _nblk && guard++ <= _nblk)
                    {
                        blks.Add(blk);
                        frs.Add(_freeCount[blk]);
                        blk = Le16(_image, blk * BlockSize);
                    }
                    j.chainBlks[hi] = (int[])blks.ToArray(typeof(int));
                    j.chainFr[hi] = (int[])frs.ToArray(typeof(int));
                }
                jobs[s] = j;
            }
            return jobs;
        }

        /// <summary>Next block a new record of this home can occupy, in exactly
        /// the order serial FindFree would use: owned home block first, then the
        /// original overflow chain in walk order, then previously allocated
        /// blocks. Consumption is monotone (slots are never freed during a
        /// batch), so a per-home cursor is equivalent to scanning for the first
        /// free slot. Returns -1 when the shard must allocate a new block.</summary>
        private static int NextBlock(ShardJob j, int hi)
        {
            if (j.stage[hi] == 0)
            {
                if (j.homeFr[hi] > 0) { j.homeFr[hi]--; return j.loHome + hi; }
                j.stage[hi] = 1;
                j.pos[hi] = 0;
            }
            if (j.stage[hi] == 1)
            {
                int[] cf = j.chainFr[hi];
                while (j.pos[hi] < cf.Length && cf[j.pos[hi]] <= 0) j.pos[hi]++;
                if (j.pos[hi] < cf.Length)
                {
                    cf[j.pos[hi]]--;
                    return j.chainBlks[hi][j.pos[hi]];
                }
                j.stage[hi] = 2;
            }
            /* stage 2: the most recently allocated block */
            if (j.allocFree[hi] > 0) { j.allocFree[hi]--; return j.allocBlk[hi]; }
            return -1;
        }

        /// <summary>First empty slot of a block we own (byte scan, exactly like
        /// FindFree's slot choice). Only this shard writes the block, so the
        /// bytes reflect initial state + our own placements.</summary>
        private int FirstEmptySlot(int blk)
        {
            for (int s = 0; s < SlotsPerBlock; s++)
                if (SlotEmptyAt(_image, SlotOffset(blk, s), _klen)) return s;
            throw new FileFullException("no empty slot in block " + blk);
        }

        /// <summary>One shard's pass over its rows. dry = demand simulation with
        /// zero shared mutation; real = placement into owned blocks only (home,
        /// original chain, own slice) plus the shard-local key map for new keys.
        /// Runs on the worker thread.</summary>
        private void RunShardPass(ShardJob j)
        {
            try
            {
                int[] rows = j.rows;
                for (int i = 0; i < rows.Length; i++)
                {
                    int r = rows[i];
                    byte[] p = j.packed[r];
                    byte[] key = new byte[KeyLen];
                    Array.Copy(p, 0, key, 0, KeyLen);
                    KeyRef kr = IndexKey(key);   /* same shape as shared-index entries */

                    object lv = j.localIndex[kr];
                    if (lv != null)
                    {
                        /* intra-batch duplicate: overwrite the earlier row's slot */
                        if (!j.dry) StoreRecord((int)lv >> 4, (int)lv & 15, p, false);
                        j.updated++;
                        continue;
                    }
                    object sv = _index[kr];   /* shared index: read-only while we hold _imageLock */
                    if (sv != null)
                    {
                        int loc = (int)sv;
                        if (!j.dry) StoreRecord(loc >> 4, loc & 15, p, false);
                        j.updated++;
                        continue;
                    }

                    /* create: new key, not present before the batch */
                    int h = j.homes[r];
                    if (h < 1 || h >= _nblk)
                        throw new FileFullException("home block " + h + " does not exist in this file");
                    int hi = h - j.loHome;
                    int blk = NextBlock(j, hi);
                    if (blk < 0)
                    {
                        if (j.dry)
                        {
                            j.demand++;
                            j.allocFree[hi] = SlotsPerBlock - 1;   /* virtual block: this record + 9 */
                        }
                        else
                        {
                            if (j.sliceTop <= 0)
                                throw new FileFullException("shard " + j.id + " ran out of allocated blocks");
                            blk = j.slice[--j.sliceTop];
                            AppendChain(h, blk);
                            j.allocBlk[hi] = blk;
                            j.allocFree[hi] = SlotsPerBlock - 1;
                        }
                    }
                    if (j.dry)
                    {
                        j.localIndex[kr] = 1;   /* marker: created earlier in this batch */
                    }
                    else
                    {
                        int slot = FirstEmptySlot(blk);
                        StoreRecord(blk, slot, p, true);
                        j.localIndex[kr] = (blk << 4) | slot;
                    }
                    j.created++;
                }
            }
            catch (Exception ex) { j.fault = ex; }   /* rethrown after Join */
        }

        /// <summary>Spawn one background thread per non-empty shard, run its pass,
        /// Join all, then rethrow the first fault (if any) - same pattern as
        /// PackRange. Whitelist-only: Thread + Join.</summary>
        private void RunShards(ShardJob[] jobs, string namePrefix)
        {
            Thread[] ths = new Thread[jobs.Length];
            for (int s = 0; s < jobs.Length; s++)
            {
                ShardJob job = jobs[s];
                if (job.rows.Length == 0) continue;
                ths[s] = new Thread(new ThreadStart(delegate { RunShardPass(job); }));
                ths[s].Name = namePrefix + s;
                ths[s].IsBackground = true;
            }
            for (int s = 0; s < jobs.Length; s++) if (ths[s] != null) ths[s].Start();
            for (int s = 0; s < jobs.Length; s++) if (ths[s] != null) ths[s].Join();

            Exception fault = null;
            for (int s = 0; s < jobs.Length; s++)
                if (jobs[s].fault != null && fault == null) fault = jobs[s].fault;
            if (fault != null) throw fault;
        }

        /// <summary>Import a whole batch of rows with the validate/pack front half
        /// AND the placement phase parallelized (auto worker count =
        /// ProcessorCount). See ImportBatchShardedAsync(rows, workerCount) for
        /// the full contract.</summary>
        public AsyncResult<BatchReport> ImportBatchShardedAsync(IEnumerable<EalItemrRecord> rows)
        {
            return BeginOp("batchshard", new EalItemrWork<BatchReport>(delegate
            {
                return RunBatchSharded(rows, 0);
            }));
        }

        /// <summary>Import a whole batch of rows with the validate/pack front half
        /// and the placement phase spread across <paramref name="workerCount"/>
        /// worker threads. Placement is sharded by home bucket: each shard
        /// exclusively owns a contiguous range of homes (home block + full
        /// overflow chain) plus its own slice of the pristine pool, sized
        /// exactly by a prior demand pass - so the parallel phase has zero
        /// shared mutable state and capacity is checked before any mutation
        /// (FileFullException fails fast; nothing placed or committed).
        /// Whitelist-only concurrency (Thread + Join; no TPL). Collect-all-
        /// then-refuse validation is preserved. Output is deterministic for a
        /// fixed (input, K) but NOT byte-identical to the serial tier's file:
        /// overflow blocks come from different slices of the pool - compare
        /// tiers semantically (same key set and values, zero orphans per
        /// VerifyAsync), not bytewise. workerCount &lt; 1 throws; K == 1 or a
        /// batch smaller than MinRowsForParallelism falls back to the serial
        /// path (report.Workers == 1).</summary>
        public AsyncResult<BatchReport> ImportBatchShardedAsync(IEnumerable<EalItemrRecord> rows, int workerCount)
        {
            if (workerCount < 1)
                throw new ArgumentException("workerCount must be >= 1", "workerCount");
            return BeginOp("batchshard", new EalItemrWork<BatchReport>(delegate
            {
                return RunBatchSharded(rows, workerCount);
            }));
        }

        private BatchReport RunBatchSharded(IEnumerable<EalItemrRecord> rows, int requestedK)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            EalItemrRecord[] arr = MaterializeRows(rows);

            int K = requestedK > 0 ? requestedK : Environment.ProcessorCount;
            if (arr.Length < MinRowsForParallelism || K <= 1)
                return RunBatch(arr);   /* serial fallback: identical behavior */
            if (K > arr.Length) K = arr.Length;

            /* Pass A - parallel validate/pack, shared with the Phase 3 tier. */
            byte[][] packed;
            double validateMs;
            ValidatePackParallel(arr, K, out packed, out validateMs);

            Stopwatch sw = new Stopwatch();
            long bytesWritten = 0;
            double placeMs = 0, commitMs = 0;
            int created = 0, updated = 0;
            lock (_imageLock)
            {
                /* pre-pass snapshot: any fault in the parallel placement phase
                   must leave the file object exactly as it was before the call
                   (disk is untouched either way - the single commit happens
                   only after all rows placed). Restoring also rebuilds every
                   placement structure from the image. */
                byte[] snapImg = (byte[])_image.Clone();

                sw.Start();
                try
                {
                    int[] homes = ComputeHomes(packed);
                    int[][] rowsByShard = PartitionRows(homes, K);

                    /* Pass B1 - parallel demand pass: each shard simulates its
                       own rows with purely local state and reports exactly how
                       many pristine blocks it will need. Zero shared mutation. */
                    ShardJob[] dryJobs = MakeShardJobs(rowsByShard, packed, homes, K, true);
                    RunShards(dryJobs, "ealitemr-shard-dry-");

                    int totalDemand = 0;
                    for (int s = 0; s < K; s++) totalDemand += dryJobs[s].demand;
                    if (totalDemand > _pristineCount)
                        throw new FileFullException("batch import needs " + totalDemand
                            + " overflow block(s) but only " + _pristineCount
                            + " pristine block(s) are available");

                    /* Pass B2 - deterministic slice assignment: shard 0 gets the
                       top (highest-numbered) demand[0] pristine blocks, shard 1
                       the next, and so on. Each shard pops its own slice from
                       the top, like serial placement pops the global pool. */
                    ShardJob[] jobs = MakeShardJobs(rowsByShard, packed, homes, K, false);
                    int cursor = _pristineCount;
                    for (int s = 0; s < K; s++)
                    {
                        int d = dryJobs[s].demand;
                        if (d > 0)
                        {
                            int[] sl = new int[d];
                            for (int i2 = 0; i2 < d; i2++) sl[i2] = _pristine[cursor - d + i2];
                            cursor -= d;
                            jobs[s].slice = sl;
                            jobs[s].sliceTop = d;
                        }
                    }

                    /* Pass B3 - parallel placement: each shard writes only its
                       own blocks (home / original chain / own slice), disjoint
                       _freeCount/_chainTail cells, and its own local key map. */
                    RunShards(jobs, "ealitemr-shard-place-");

                    for (int s = 0; s < K; s++)
                    {
                        created += jobs[s].created;
                        updated += jobs[s].updated;
                        foreach (DictionaryEntry de in jobs[s].localIndex)
                            _index[de.Key] = de.Value;   /* merge new keys into the shared index */
                    }
                }
                catch (Exception ex)
                {
                    _image = snapImg;
                    BuildIndex();
                    if (ex is FileFullException)
                        throw new FileFullException("sharded batch import failed: " + ex.Message);
                    throw;
                }
                sw.Stop();
                placeMs = sw.Elapsed.TotalMilliseconds;

                /* the batch's single commit. SaveImage() defers to a session
                   when one is open (_dirty), else AtomicWrite(). */
                sw.Reset(); sw.Start();   /* .NET 2.0 has no Stopwatch.Restart() */
                SaveImage();
                sw.Stop();
                commitMs = sw.Elapsed.TotalMilliseconds;
                bytesWritten = _session ? 0 : (long)_image.Length;
            }

            BatchReport rep = new BatchReport();
            rep.Rows = arr.Length;
            rep.Created = created;
            rep.Updated = updated;
            rep.ValidateMs = validateMs;
            rep.PlaceMs = placeMs;
            rep.CommitMs = commitMs;
            rep.BytesWritten = bytesWritten;
            rep.Workers = K;
            rep.Placement = "sharded";
            return rep;
        }

        /// <summary>Hash/layout consistency summary (home/chained/orphan,
        /// BCD vs raw keys, empty slots). Read-only.</summary>
        public AsyncResult<VerifyReport> VerifyAsync()
        {
            return BeginOp("verify", new EalItemrWork<VerifyReport>(delegate
            {
                lock (_imageLock)
                {
                    VerifyReport rep = new VerifyReport();
                    rep.BlockCount = _nblk;
                    rep.RecordSize = _rs;
                    rep.Divisor = _divisor;
                    rep.KeyLen = _klen;
                    rep.ChainThreshold = _thr;
                    for (int blk = 1; blk < _nblk; blk++)
                        for (int s = 0; s < SlotsPerBlock; s++)
                        {
                            int off = SlotOffset(blk, s);
                            if (SlotEmptyAt(_image, off, _klen))
                            { rep.EmptySlots++; continue; }
                            if (IsBcdKey(_image, off, _klen)) rep.BcdRecords++;
                            else rep.RawKeyRecords++;
                            byte[] key = new byte[_klen];
                            Array.Copy(_image, off, key, 0, _klen);
                            int hb = HashBlock(key);
                            if (hb == blk) rep.HomeRecords++;
                            else if (InChain(hb, blk)) rep.ChainedRecords++;
                            else rep.OrphanRecords++;
                        }
                    rep.TotalRecords =
                        rep.HomeRecords + rep.ChainedRecords + rep.OrphanRecords;
                    return rep;
                }
            }));
        }

        /* ---------- session mode (Phase 1): deferred commit ---------- */
        /// <summary>Begin a write session: subsequent Add/Write/Delete calls
        /// mutate the in-memory image and index but do NOT touch disk. Call
        /// CommitSessionAsync() to persist everything in one atomic rewrite,
        /// or RollbackSessionAsync() to discard all changes (the on-disk file
        /// was never modified, so rollback re-reads it). Close() auto-commits
        /// any uncommitted session changes. Only one session at a time.</summary>
        public void BeginSession()
        {
            lock (_opLock)
            {
                if (_closed)
                    throw new ObjectDisposedException("EalItemrFile", "file is closed");
            }
            lock (_imageLock) { _session = true; _dirty = false; }
        }

        /// <summary>Persist all changes made since BeginSession() in one atomic
        /// temp+rename rewrite (instead of one full-file write per record).
        /// Returns the number of bytes written (0 when nothing changed).
        /// Ends the session.</summary>
        public AsyncResult<long> CommitSessionAsync()
        {
            return BeginOp("commit", new EalItemrWork<long>(delegate
            {
                lock (_imageLock)
                {
                    if (!_session) throw new EalItemrException("no active session");
                    long n = 0;
                    if (_dirty)
                    {
                        AtomicWrite();
                        n = _image.Length;
                    }
                    _session = false; _dirty = false;
                    return n;
                }
            }));
        }

        /// <summary>Discard all changes made since BeginSession(): re-read the
        /// on-disk image (unmodified, because writes were deferred) and rebuild
        /// the index. Ends the session.</summary>
        public AsyncResult<bool> RollbackSessionAsync()
        {
            return BeginOp("rollback", new EalItemrWork<bool>(delegate
            {
                lock (_imageLock)
                {
                    if (!_session) throw new EalItemrException("no active session");
                    LoadImage();   /* re-reads disk + rebuilds _index */
                    _session = false; _dirty = false;
                    return true;
                }
            }));
        }

        /* ---------- lifecycle ---------- */
        internal void UnregisterOp(AsyncResult op)
        {
            lock (_opLock) { _activeOps.Remove(op); }
        }

        /// <summary>Wait for all outstanding operations, then mark the file closed.
        /// Subsequent operation calls return handles that fail with
        /// ObjectDisposedException. Idempotent.</summary>
        public void Close()
        {
            ArrayList pending;
            lock (_opLock)
            {
                if (_closed) return;
                _closed = true;
                pending = (ArrayList)_activeOps.Clone();
            }
            for (int i = 0; i < pending.Count; i++)
                ((AsyncResult)pending[i]).Wait();
            /* auto-commit: an open session's changes are flushed on close so
               nothing is silently lost (file-handle semantics). */
            lock (_imageLock)
            {
                if (_session && _dirty) AtomicWrite();
                _session = false; _dirty = false;
            }
        }
        public void Dispose() { Close(); }
    }
}