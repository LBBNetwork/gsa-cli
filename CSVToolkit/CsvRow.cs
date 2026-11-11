using System;

namespace C3.CSVToolkit
{
    /// <summary>One parsed CSV row: ordered string values, index-accessible.
    /// Immutable after creation (values are preserved byte-for-byte per CSV-5).</summary>
    public sealed class CsvRow
    {
        private readonly string[] _columns;

        internal CsvRow(string[] columns)
        {
            _columns = columns;
        }

        /// <summary>Number of values in this row (equals the header column count).</summary>
        public int ColumnCount
        {
            get { return _columns.Length; }
        }

        /// <summary>Value at the given zero-based position.</summary>
        public string this[int index]
        {
            get
            {
                if (index < 0 || index >= _columns.Length)
                    throw new ArgumentOutOfRangeException("index");
                return _columns[index];
            }
        }

        /// <summary>A copy of the row's values in file order.</summary>
        public string[] Columns
        {
            get { return (string[])_columns.Clone(); }
        }

        public override string ToString()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(_columns.Length * 8);
            for (int i = 0; i < _columns.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_columns[i]);
            }
            return sb.ToString();
        }
    }
}
