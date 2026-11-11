using System;
using System.Collections.Generic;

namespace C3.CSVToolkit
{
    /// <summary>Convenience facade and shared policy (worker-count
    /// normalization) for the CSVToolkit reader/writer.</summary>
    public static class Csv
    {
        /// <summary>Upper bound on parallel workers per operation. Keeps the
        /// live-memory bound (one group/batch of work at a time) small even if
        /// callers pass large values.</summary>
        public const int MaxWorkerCount = 32;

        /// <summary>Normalize a requested worker count: &lt;1 becomes 1 (the
        /// serial tier, CSV-9); above the cap it is clamped to the cap.</summary>
        public static int NormalizeWorkerCount(int workerCount)
        {
            if (workerCount < 1) return 1;
            if (workerCount > MaxWorkerCount) return MaxWorkerCount;
            return workerCount;
        }

        /// <summary>Read an entire file into memory. For large files prefer
        /// CsvReader.OpenAsync + TryReadRow to keep the streaming bound.</summary>
        public static List<CsvRow> ReadAll(string path, int workerCount)
        {
            if (path == null) throw new ArgumentNullException("path");
            AsyncResult<CsvReader> h = CsvReader.OpenAsync(path, NormalizeWorkerCount(workerCount));
            CsvReader r = h.Result;   // throws the open error on failure
            try
            {
                List<CsvRow> rows = new List<CsvRow>();
                CsvRow row;
                while (r.TryReadRow(out row)) rows.Add(row);
                return rows;
            }
            finally
            {
                r.Close();
            }
        }

        /// <summary>Write an entire in-memory row set. For large data prefer
        /// CsvWriter.OpenAsync + WriteHeader/WriteRow to keep the streaming
        /// bound.</summary>
        public static void WriteAll(string path, IList<string> header, IEnumerable<string[]> rows, int workerCount)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (rows == null) throw new ArgumentNullException("rows");
            AsyncResult<CsvWriter> h = CsvWriter.OpenAsync(path, NormalizeWorkerCount(workerCount));
            CsvWriter w = h.Result;   // throws the open error on failure
            try
            {
                w.WriteHeader(header);
                foreach (string[] row in rows) w.WriteRow(row);
            }
            finally
            {
                w.Close();
            }
        }
    }
}
