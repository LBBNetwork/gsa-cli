using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace C3.CSVToolkit
{
    /// <summary>Streaming CSV reader (CSV-2) with a parallel, deterministic
    /// pipeline (CSV-8…CSV-11).
    ///
    /// Operating model: OpenAsync runs on a background thread and returns an
    /// AsyncResult handle; once open, rows are pulled in strict file order via
    /// TryReadRow. The data region is split into contiguous line-aligned
    /// chunks (disjoint byte ranges); each group of up to workerCount chunks
    /// is parsed by that many worker threads over their own disjoint file
    /// regions (the EalItemr idiom), joined, and emitted in order - so only
    /// one group's rows are live at any moment (streaming bound holds under
    /// parallelism) and output depends only on the input file (CSV-11).
    /// Whitelist-only primitives: Thread + Join.</summary>
    public sealed class CsvReader : IDisposable
    {
        // Target bytes per chunk. Big enough that a group of workerCount
        // chunks is real parallel work; small enough that one live group stays
        // in the low single-digit MB range (D55-D57 memory discipline).
        private const int ChunkTargetBytes = 512 * 1024;

        /// <summary>One contiguous, line-aligned slice of the data region.</summary>
        private sealed class Chunk
        {
            public int Start;          // first byte offset (inclusive)
            public int End;            // one past last byte (exclusive)
            public int FirstLineNo;    // 1-based file number of its first row

            public Chunk(int start, int end, int firstLineNo)
            {
                Start = start;
                End = end;
                FirstLineNo = firstLineNo;
            }
        }

        private readonly string _path;
        private readonly int _workerCount;
        private string[] _header;
        private Chunk[] _chunks = new Chunk[0];
        private int _nextChunk;                    // next chunk not yet grouped
        private List<string[]> _pending;           // current group's rows, in order
        private int _posInPending;
        private bool _eof;
        private Exception _fault;                   // first error in file order (CSV-11)
        private bool _disposed;

        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        internal CsvReader(string path, int workerCount)
        {
            _path = path;
            _workerCount = workerCount;
        }

        // ---------- open (background thread, async handle) ----------

        /// <summary>Open on a background thread (serial tier); Result is the opened reader.</summary>
        public static AsyncResult<CsvReader> OpenAsync(string path)
        {
            return OpenAsync(path, 1);
        }

        /// <summary>Open on a background thread; workerCount selects how many
        /// chunks parse in parallel per group (CSV-9/CSV-10).</summary>
        public static AsyncResult<CsvReader> OpenAsync(string path, int workerCount)
        {
            if (path == null) throw new ArgumentNullException("path");
            int wc = Csv.NormalizeWorkerCount(workerCount);
            AsyncResult<CsvReader> r = new AsyncResult<CsvReader>();
            r.Start("open", new ThreadStart(delegate
            {
                Exception err = null; CsvReader res = null;
                try { res = Open(path, wc); }
                catch (Exception ex) { err = ex; }
                r.SetResult(res, err);
                r.RaiseCompleted(res, err);
            }));
            return r;
        }

        private static CsvReader Open(string path, int workerCount)
        {
            FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            try
            {
                long fileSize = fs.Length;
                if (fileSize > int.MaxValue)
                    throw new IOException("file is too large to address");

                // BOM tolerance (CSV-3): skip a UTF-8 BOM if present.
                int contentStart = 0;
                if (fileSize >= 3)
                {
                    byte[] head = ReadRange(fs, 0, 3);
                    if (head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                        contentStart = 3;
                }

                if (contentStart >= fileSize)
                    throw new CsvException("missing or empty header", 1);

                // Find the end of line 1 (the header).
                int pos = contentStart;
                int headerEnd = -1;   // offset of the '\n' terminating line 1, or -1
                byte[] scan = new byte[4096];
                while (headerEnd < 0 && pos < fileSize)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(scan.Length, fileSize - pos);
                    int got = fs.Read(scan, 0, toRead);
                    if (got <= 0) break;
                    for (int i = 0; i < got; i++)
                        if (scan[i] == (byte)'\n') { headerEnd = pos + i; break; }
                    pos += got;
                }

                string headerText;
                int dataStart;
                if (headerEnd < 0)
                {
                    // Header with no terminator: the whole remainder is line 1.
                    byte[] hb = ReadRange(fs, contentStart, (int)(fileSize - contentStart));
                    headerText = DecodeStrict(hb, 0, hb.Length, 1);
                    dataStart = (int)fileSize;
                }
                else
                {
                    int hLen = headerEnd - contentStart;
                    byte[] hb = ReadRange(fs, contentStart, hLen);
                    int textLen = hLen;
                    if (textLen > 0 && hb[textLen - 1] == (byte)'\r') textLen--;   // CRLF -> LF
                    headerText = DecodeStrict(hb, 0, textLen, 1);
                    dataStart = headerEnd + 1;
                }

                string[] header = CsvCore.ParseHeader(headerText);
                if (header.Length < 1 || (header.Length == 1 && header[0].Length == 0))
                    throw new CsvException("missing or empty header", 1);

                // Chunk the data region into line-aligned slices of ~ChunkTargetBytes.
                List<Chunk> chunks = new List<Chunk>();
                if (dataStart < fileSize)
                {
                    fs.Seek(dataStart, SeekOrigin.Begin);
                    int chunkPos = dataStart;
                    int chunkStart = dataStart;
                    int newlinesInData = 0;        // '\n' seen in [dataStart, chunkPos)
                    int newlinesAtChunkStart = 0; // '\n' count at current chunkStart
                    byte[] one = new byte[1];
                    while (chunkPos < fileSize)
                    {
                        if (fs.Read(one, 0, 1) <= 0) break;
                        chunkPos++;
                        if (one[0] == (byte)'\n')
                        {
                            newlinesInData++;
                            if (chunkPos - chunkStart >= ChunkTargetBytes)
                            {
                                chunks.Add(new Chunk(chunkStart, chunkPos, 2 + newlinesAtChunkStart));
                                chunkStart = chunkPos;
                                newlinesAtChunkStart = newlinesInData;
                            }
                        }
                    }
                    if (chunkStart < fileSize)
                        chunks.Add(new Chunk(chunkStart, (int)fileSize, 2 + newlinesAtChunkStart));
                }

                CsvReader reader = new CsvReader(path, workerCount);
                reader._header = header;
                reader._chunks = chunks.ToArray();
                return reader;
            }
            finally
            {
                fs.Close();
            }
        }

        // ---------- pull rows (strict file order) ----------

        /// <summary>File path this reader is reading.</summary>
        public string Path { get { return _path; } }

        /// <summary>Header column names in file order (line 1).</summary>
        public string[] Header
        {
            get { return (string[])_header.Clone(); }
        }

        /// <summary>Zero-based index of the named header column, or -1 when
        /// absent. Exact match only - no case folding (CSV-5).</summary>
        public int ColumnIndex(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            for (int i = 0; i < _header.Length; i++)
                if (_header[i] == name) return i;
            return -1;
        }

        /// <summary>Read the next row in file order. Returns false at end of
        /// file. Throws CsvException (with the offending line number) on a
        /// format error; repeated calls keep throwing that same first-in-file-
        /// order error until Close (CSV-11).</summary>
        public bool TryReadRow(out CsvRow row)
        {
            if (_disposed) throw new ObjectDisposedException("CsvReader");
            if (_fault != null) throw _fault;

            while (true)
            {
                if (_pending != null && _posInPending < _pending.Count)
                {
                    row = new CsvRow(_pending[_posInPending]);
                    _posInPending++;
                    return true;
                }
                if (_eof)
                {
                    row = null;
                    return false;
                }
                try
                {
                    RunNextGroup();
                }
                catch (Exception ex)
                {
                    _fault = ex;
                    throw ex;
                }
            }
        }

        /// <summary>Parse the next group of up to workerCount chunks in
        /// parallel over disjoint file regions, join, and stage their rows in
        /// strict chunk order. Only one group's rows are live at a time.</summary>
        private void RunNextGroup()
        {
            if (_nextChunk >= _chunks.Length)
            {
                _eof = true;
                return;
            }

            int groupEnd = Math.Min(_nextChunk + _workerCount, _chunks.Length);
            int n = groupEnd - _nextChunk;

            List<string[]>[] results = new List<string[]>[n];
            Exception[] errors = new Exception[n];
            Thread[] threads = new Thread[n];

            for (int i = 0; i < n; i++)
            {
                Chunk chunk = _chunks[_nextChunk + i];
                int idx = i;                       // C# 2: capture a copy, not the loop var
                string path = _path;
                int expectedCount = _header.Length;

                threads[i] = new Thread(delegate()
                {
                    try { results[idx] = ParseChunk(path, chunk, expectedCount); }
                    catch (Exception ex) { errors[idx] = ex; }
                });
                threads[i].Name = "csvread-chunk" + (_nextChunk + i);
                threads[i].IsBackground = true;
                threads[i].Start();
            }

            for (int i = 0; i < n; i++) threads[i].Join();

            // First error in file order wins: chunks are in file order and each
            // worker reports its own first error.
            for (int i = 0; i < n; i++)
                if (errors[i] != null) throw errors[i];

            List<string[]> pending = new List<string[]>();
            for (int i = 0; i < n; i++)
            {
                List<string[]> chunkRows = results[i];
                if (chunkRows != null) pending.AddRange(chunkRows);
            }
            _pending = pending;
            _posInPending = 0;
            _nextChunk = groupEnd;
        }

        /// <summary>Worker body: parse one chunk's disjoint byte range into rows.</summary>
        private static List<string[]> ParseChunk(string path, Chunk chunk, int expectedCount)
        {
            List<string[]> rows = new List<string[]>();
            FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            try
            {
                fs.Seek(chunk.Start, SeekOrigin.Begin);
                int remaining = chunk.End - chunk.Start;

                byte[] buf = new byte[8192];
                byte[] lineBytes = new byte[256];
                int lineLen = 0;
                int lineNo = chunk.FirstLineNo;

                while (remaining > 0)
                {
                    int toRead = Math.Min(buf.Length, remaining);
                    int got = fs.Read(buf, 0, toRead);
                    if (got <= 0) break;
                    remaining -= got;

                    for (int i = 0; i < got; i++)
                    {
                        byte b = buf[i];
                        if (b == (byte)'\n')
                        {
                            int len = lineLen;
                            if (len > 0 && lineBytes[len - 1] == (byte)'\r') len--;   // CRLF -> LF
                            string text = DecodeStrict(lineBytes, 0, len, lineNo);
                            rows.Add(CsvCore.ParseLine(text, expectedCount, lineNo));
                            lineLen = 0;
                            lineNo++;
                        }
                        else
                        {
                            if (lineLen == lineBytes.Length)
                                lineBytes = Grow(lineBytes);
                            lineBytes[lineLen++] = b;
                        }
                    }
                }

                // Final line without a terminator (files need not end in a newline, CSV-3).
                if (lineLen > 0)
                {
                    string text = DecodeStrict(lineBytes, 0, lineLen, lineNo);
                    rows.Add(CsvCore.ParseLine(text, expectedCount, lineNo));
                }

                return rows;
            }
            finally
            {
                fs.Close();
            }
        }

        // ---------- lifecycle ----------

        /// <summary>Release resources. Idempotent. Safe to call after an error.</summary>
        public void Close()
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
        }

        public void Dispose()
        {
            Close();
        }

        // ---------- helpers ----------

        private static byte[] Grow(byte[] bytes)
        {
            byte[] grown = new byte[bytes.Length * 2];
            Array.Copy(bytes, grown, bytes.Length);
            return grown;
        }

        private static string DecodeStrict(byte[] bytes, int offset, int count, int lineNo)
        {
            try
            {
                return Utf8Strict.GetString(bytes, offset, count);
            }
            catch (DecoderFallbackException)
            {
                throw new CsvException("invalid UTF-8 encoding", lineNo);
            }
        }

        private static byte[] ReadRange(FileStream fs, long start, int count)
        {
            byte[] data = new byte[count];
            long saved = fs.Position;
            try
            {
                fs.Seek(start, SeekOrigin.Begin);
                int total = 0;
                while (total < count)
                {
                    int got = fs.Read(data, total, count - total);
                    if (got <= 0) break;
                    total += got;
                }
            }
            finally
            {
                fs.Position = saved;
            }
            return data;
        }
    }
}
