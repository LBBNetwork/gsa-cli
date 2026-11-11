using System;
using System.Collections.Generic;

namespace GsACli.Bridge
{
	/// <summary>
	/// The real CSV engine (Phase 5a, D60): a thin synchronous wrapper over
	/// C3.CSVToolkit's async streaming API. Mirrors EalItemrEngine's idiom -
	/// the library's AsyncResult handles are joined with .Result at the seam
	/// so command code stays synchronous; all row traffic is one-at-a-time in
	/// strict file order (CSV-2), never a materialized list.
	/// </summary>
	public class CsvEngine : ICsvEngine
	{
		/// <summary>
		/// Default worker pool size for CSV operations (CSV-10: gsa-cli picks
		/// the count so total threads stay sane). 4 matches a conservative
		/// XP-era core budget; EalItemr's own defaults are not exposed here.
		/// </summary>
		public const int DefaultWorkers = 4;

		public ICsvReader OpenReader(string path)
		{
			return OpenReader(path, DefaultWorkers);
		}

		public ICsvReader OpenReader(string path, int workerCount)
		{
			C3.CSVToolkit.CsvReader inner = C3.CSVToolkit.CsvReader.OpenAsync(path, workerCount).Result; // .Result rethrows on failure
			return new Reader(inner);
		}

		public ICsvWriter OpenWriter(string path)
		{
			return OpenWriter(path, DefaultWorkers);
		}

		public ICsvWriter OpenWriter(string path, int workerCount)
		{
			C3.CSVToolkit.CsvWriter inner = C3.CSVToolkit.CsvWriter.OpenAsync(path, workerCount).Result; // .Result rethrows on failure
			return new Writer(inner);
		}

		private class Reader : ICsvReader
		{
			private readonly C3.CSVToolkit.CsvReader _inner;

			public Reader(C3.CSVToolkit.CsvReader inner)
			{
				this._inner = inner;
			}

			public string[] Header
			{
				get { return this._inner.Header; } // library already returns a clone
			}

			public int ColumnIndex(string name)
			{
				return this._inner.ColumnIndex(name); // -1 when absent (exact match)
			}

			public bool TryReadRow(out CsvRow row)
			{
				C3.CSVToolkit.CsvRow lib;
				bool ok = this._inner.TryReadRow(out lib);
				if (ok && lib != null)
				{
					row = new CsvRow(lib.Columns); // Columns is a clone - safe to hand out
					return true;
				}

				row = null;
				return false;
			}

			public void Dispose()
			{
				this._inner.Close();
			}
		}

		private class Writer : ICsvWriter
		{
			private readonly C3.CSVToolkit.CsvWriter _inner;

			public Writer(C3.CSVToolkit.CsvWriter inner)
			{
				this._inner = inner;
			}

			public void WriteHeader(IList<string> columns)
			{
				this._inner.WriteHeader(columns);
			}

			public void WriteRow(IList<string> values)
			{
				this._inner.WriteRow(values);
			}

			public void Dispose()
			{
				this._inner.Close(); // surfaces the first pipeline fault in file order
			}
		}
	}
}
