using System;
using System.Collections.Generic;

namespace GsACli.Bridge
{
	/// <summary>
	/// The CSV engine seam (Decision 39, reshaped in Phase 5a / D60): a
	/// streaming reader/writer pair over the external C3.CSVToolkit library.
	/// gsa-cli never materializes a whole file - rows are pulled one at a time
	/// in strict file order (CSV-2), so a million-row IMPORT upserts row by
	/// row and an EXPORT streams records out without holding them all.
	/// </summary>
	public interface ICsvEngine
	{
		/// <summary>Open the CSV file for streaming reads (throws on open failure).</summary>
		ICsvReader OpenReader(string path);

		/// <summary>Open with an explicit worker count (CSV-10: gsa-cli picks it).</summary>
		ICsvReader OpenReader(string path, int workerCount);

		/// <summary>Create/overwrite the CSV file for streaming writes.</summary>
		ICsvWriter OpenWriter(string path);

		/// <summary>Create with an explicit worker count (CSV-10: gsa-cli picks it).</summary>
		ICsvWriter OpenWriter(string path, int workerCount);
	}

	/// <summary>
	/// One open CSV file for reading. Rows come back in strict file order;
	/// a format error throws CsvException-style (the library's exception)
	/// with the offending line number (CSV-11). Dispose/Close releases the
	/// file and its worker pool.
	/// </summary>
	public interface ICsvReader : IDisposable
	{
		/// <summary>The header row, in file order (a clone - safe to keep).</summary>
		string[] Header { get; }

		/// <summary>Zero-based index of the named column, or -1 when absent (exact match).</summary>
		int ColumnIndex(string name);

		/// <summary>Read the next row in file order. False at end of file.</summary>
		bool TryReadRow(out CsvRow row);
	}

	/// <summary>
	/// One open CSV file for writing. WriteHeader once, then WriteRow per
	/// data row; Close flushes and surfaces any pipeline fault (first in
	/// file order). Output is byte-identical across runs (CSV-6).
	/// </summary>
	public interface ICsvWriter : IDisposable
	{
		void WriteHeader(IList<string> columns);

		void WriteRow(IList<string> values);
	}

	/// <summary>One CSV row as gsa-cli sees it - ordered string values.</summary>
	public class CsvRow
	{
		public readonly string[] Columns;

		public CsvRow(string[] columns)
		{
			Columns = columns;
		}

		public string this[int index]
		{
			get { return Columns[index]; }
		}
	}
}
