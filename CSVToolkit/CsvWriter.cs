using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace C3.CSVToolkit
{
    /// <summary>Streaming CSV writer (CSV-2) with a parallel, deterministic
    /// pipeline (CSV-8…CSV-11).
    ///
    /// Operating model: OpenAsync runs on a background thread and returns an
    /// AsyncResult handle; once open, the caller pushes WriteHeader/WriteRow.
    /// Rows accumulate into batches on the calling thread; each batch is
    /// formatted by up to workerCount worker threads over disjoint contiguous
    /// slices (the EalItemr idiom), joined, and written in strict row order by
    /// a single ordered write thread - so formatting runs ahead of the file
    /// writes while output depends only on the input rows (CSV-11). A bounded
    /// hand-off queue provides backpressure, keeping live memory to a few
    /// batches regardless of file size. Whitelist-only primitives: Thread +
    /// Join (+ Monitor for the bounded queue).</summary>
    public sealed class CsvWriter : IDisposable
    {
        // Rows per batch (the parallel formatting unit) and how many batches
        // may be queued ahead of the write thread. Together these bound live
        // memory to a few thousand rows at most (D55-D57 discipline).
        private const int BatchRows = 256;
        private const int QueueCapacityUnits = 4;

        /// <summary>One batch of raw rows handed from caller to pipeline.</summary>
        private sealed class Unit
        {
            public string[][] Rows;
            public int Count;
            public Unit(string[][] rows, int count) { Rows = rows; Count = count; }
        }

        private readonly string _path;
        private readonly int _workerCount;
        private Thread _pipelineThread;

        private readonly object _gate = new object();
        private readonly Queue<Unit> _batchQueue = new Queue<Unit>();
        private bool _closed;                 // Close() signalled to the pipeline
        private Exception _pipelineFault;     // first fault in file order (CSV-11)

        private int _columnCount = -1;        // set by WriteHeader
        private bool _headerWritten;
        private int _rowsWritten;
        private readonly List<string[]> _batch = new List<string[]>(BatchRows);
        private bool _closedOnce;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        internal CsvWriter(string path, int workerCount)
        {
            _path = path;
            _workerCount = workerCount;
        }

        // ---------- open (background thread, async handle) ----------

        /// <summary>Open on a background thread (serial tier); Result is the opened writer.</summary>
        public static AsyncResult<CsvWriter> OpenAsync(string path)
        {
            return OpenAsync(path, 1);
        }

        /// <summary>Open on a background thread; workerCount selects how many
        /// threads format each batch in parallel (CSV-9/CSV-10).</summary>
        public static AsyncResult<CsvWriter> OpenAsync(string path, int workerCount)
        {
            if (path == null) throw new ArgumentNullException("path");
            int wc = Csv.NormalizeWorkerCount(workerCount);
            AsyncResult<CsvWriter> r = new AsyncResult<CsvWriter>();
            r.Start("open", new ThreadStart(delegate
            {
                Exception err = null; CsvWriter res = null;
                try { res = Open(path, wc); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        private static CsvWriter Open(string path, int workerCount)
        {
            // Fail fast on the handle if the target cannot be created.
            FileStream probe = File.Create(path);   // create/truncate
            probe.Close();

            CsvWriter w = new CsvWriter(path, workerCount);
            w._pipelineThread = new Thread(new ThreadStart(w.PipelineLoop));
            w._pipelineThread.Name = "csvtoolkit-write";
            w._pipelineThread.IsBackground = true;
            w._pipelineThread.Start();
            return w;
        }

        // ---------- push rows (caller thread) ----------

        /// <summary>File path this writer is writing.</summary>
        public string Path { get { return _path; } }

        /// <summary>Column count fixed by WriteHeader (-1 before it).</summary>
        public int ColumnCount { get { return _columnCount; } }

        /// <summary>Write the header (line 1). Must be called exactly once,
        /// before any WriteRow. Values are validated now; formatting happens
        /// on the pipeline.</summary>
        public void WriteHeader(IList<string> columns)
        {
            EnsureOpen();
            if (_headerWritten)
                throw new InvalidOperationException("WriteHeader may only be called once");
            if (columns == null) throw new ArgumentNullException("columns");
            if (columns.Count < 1)
                throw new ArgumentException("a CSV file requires at least one column", "columns");

            string[] header = new string[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                string v = columns[i];
                if (v == null) throw new ArgumentNullException("columns[" + i + "]");
                CheckNoLineTerminator(v, 1);
                header[i] = v;
            }

            _columnCount = columns.Count;
            _headerWritten = true;
            HandOff(new Unit(new string[][] { header }, 1));
        }

        /// <summary>Write one data row. Field count must equal the header's;
        /// values are preserved byte-for-byte (CSV-5) and may not contain CR
        /// or LF (one record per row, CSV-4).</summary>
        public void WriteRow(IList<string> values)
        {
            EnsureOpen();
            if (!_headerWritten)
                throw new InvalidOperationException("WriteHeader must be called before WriteRow");
            if (values == null) throw new ArgumentNullException("values");

            int lineNo = 2 + _rowsWritten;   // header is line 1
            if (values.Count != _columnCount)
                throw new CsvException(
                    string.Format("expected {0} fields, found {1}", _columnCount, values.Count),
                    lineNo);

            for (int i = 0; i < values.Count; i++)
            {
                string v = values[i];
                if (v == null) throw new ArgumentNullException("values[" + i + "]");
                CheckNoLineTerminator(v, lineNo);
            }

            string[] copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++) copy[i] = values[i];
            _batch.Add(copy);
            _rowsWritten++;
            if (_batch.Count >= BatchRows)
            {
                HandOff(new Unit(_batch.ToArray(), _batch.Count));
                _batch.Clear();   // streaming bound: never accumulate past one batch
            }
        }

        // ---------- lifecycle ----------

        /// <summary>Flush the final partial batch, stop the pipeline, and
        /// surface the first fault in file order (if any). Idempotent.</summary>
        public void Close()
        {
            if (_closedOnce) return;
            _closedOnce = true;

            Exception closeErr = null;
            try
            {
                if (!_headerWritten)
                    throw new InvalidOperationException("WriteHeader must be called before Close");
                if (_batch.Count > 0)
                {
                    HandOff(new Unit(_batch.ToArray(), _batch.Count));
                    _batch.Clear();
                }
            }
            catch (Exception ex)
            {
                closeErr = ex;
            }

            lock (_gate)
            {
                _closed = true;
                Monitor.PulseAll(_gate);
            }
            if (_pipelineThread != null) _pipelineThread.Join();

            Exception fault = null;
            lock (_gate)
            {
                if (_pipelineFault != null) fault = _pipelineFault;
            }

            if (closeErr != null) throw closeErr;
            if (fault != null) throw fault;
        }

        public void Dispose()
        {
            Close();
        }

        // ---------- pipeline (write thread + formatter workers) ----------

        /// <summary>Single ordered write thread: drains the bounded queue in
        /// batch order, formats each batch with worker threads over disjoint
        /// slices, and writes lines in strict row order. Emits UTF-8 with BOM,
        /// CRLF between rows, no trailing newline (CSV-3).</summary>
        private void PipelineLoop()
        {
            Exception fault = null;
            FileStream fs = File.Create(_path);   // create/truncate
            bool firstLine = true;
            try
            {
                while (true)
                {
                    Unit u;
                    lock (_gate)
                    {
                        while (_batchQueue.Count == 0 && !_closed) Monitor.Wait(_gate);
                        if (_batchQueue.Count > 0)
                        {
                            u = _batchQueue.Dequeue();
                            Monitor.PulseAll(_gate);   // wake a producer blocked on fullness
                        }
                        else break;   // closed and drained
                    }

                    string[] formatted = FormatUnit(u);
                    for (int i = 0; i < formatted.Length; i++)
                        WriteLine(fs, formatted[i], ref firstLine);
                }
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            finally
            {
                try { fs.Flush(); } catch (Exception) { }
                try { fs.Close(); } catch (Exception) { }
            }

            lock (_gate)
            {
                _pipelineFault = fault;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Format one batch: worker threads over disjoint contiguous
        /// slices, joined, concatenated in row order. With workerCount == 1
        /// this is the serial tier (CSV-9).</summary>
        private string[] FormatUnit(Unit u)
        {
            int n = u.Count;
            if (n == 0) return new string[0];

            if (_workerCount <= 1)
            {
                string[] serial = new string[n];
                for (int i = 0; i < n; i++) serial[i] = CsvCore.FormatRow(u.Rows[i]);
                return serial;
            }

            int w = _workerCount;
            string[][] slices = new string[w][];
            Exception[] errors = new Exception[w];
            Thread[] threads = new Thread[w];

            for (int t = 0; t < w; t++)
            {
                int start = n * t / w;
                int end = n * (t + 1) / w;
                if (start >= end) continue;   // empty slice: no thread needed
                int tid = t;
                Unit unit = u;

                threads[t] = new Thread(delegate()
                {
                    try
                    {
                        string[] outSlice = new string[end - start];
                        for (int i = start; i < end; i++)
                            outSlice[i - start] = CsvCore.FormatRow(unit.Rows[i]);
                        slices[tid] = outSlice;
                    }
                    catch (Exception ex) { errors[tid] = ex; }
                });
                threads[t].Name = "csvwrite-fmt" + t;
                threads[t].IsBackground = true;
                threads[t].Start();
            }

            for (int t = 0; t < w; t++)
                if (threads[t] != null) threads[t].Join();

            // First error in row order wins: slices are contiguous and ordered.
            for (int t = 0; t < w; t++)
                if (errors[t] != null) throw errors[t];

            string[] outRows = new string[n];
            int o = 0;
            for (int t = 0; t < w; t++)
            {
                if (slices[t] == null) continue;
                Array.Copy(slices[t], 0, outRows, o, slices[t].Length);
                o += slices[t].Length;
            }
            return outRows;
        }

        /// <summary>Write one line: BOM before the very first line, CRLF
        /// between lines (separator-before style), so no trailing newline
        /// exists after the last row (CSV-3).</summary>
        private static void WriteLine(FileStream fs, string line, ref bool firstLine)
        {
            if (firstLine)
            {
                fs.Write(Bom, 0, Bom.Length);
                firstLine = false;
            }
            else
            {
                fs.WriteByte((byte)'\r');
                fs.WriteByte((byte)'\n');
            }
            byte[] bytes = Utf8NoBom.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }

        // ---------- helpers ----------

        private void EnsureOpen()
        {
            if (_closedOnce) throw new ObjectDisposedException("CsvWriter");
        }

        private static void CheckNoLineTerminator(string value, int lineNo)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n')
                    throw new CsvException(
                        "field value contains a line terminator; one record per row is required",
                        lineNo);
            }
        }

        /// <summary>Hand a batch to the pipeline, blocking while the bounded
        /// queue is full (backpressure) or rethrowing an existing fault.</summary>
        private void HandOff(Unit unit)
        {
            lock (_gate)
            {
                while (_batchQueue.Count >= QueueCapacityUnits && _pipelineFault == null)
                    Monitor.Wait(_gate);
                if (_pipelineFault != null) throw _pipelineFault;
                _batchQueue.Enqueue(unit);
                Monitor.PulseAll(_gate);
            }
        }
    }
}
