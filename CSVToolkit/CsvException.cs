using System;

namespace C3.CSVToolkit
{
    /// <summary>CSV format error. Carries the offending line number so callers
    /// can report it precisely (gsa-cli maps this into its per-row rejection
    /// reports for IMPORT).</summary>
    public sealed class CsvException : Exception
    {
        private readonly int _line;

        internal CsvException(string message, int line)
            : base(message)
        {
            _line = line;
        }

        /// <summary>Offending line number (1-based; the header is line 1), or
        /// 0 when the error is not tied to a specific line.</summary>
        public int Line
        {
            get { return _line; }
        }
    }
}
