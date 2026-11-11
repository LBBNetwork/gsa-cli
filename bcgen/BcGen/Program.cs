using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using C3.CSVToolkit;

namespace BcGen
{
	/// <summary>
	/// bcgen - CSV item-code pre-processor (D68). One streaming pass:
	/// column 1 cells that are blank, zero-coded (B9), or carry an explicit
	/// rnd4/upc-a marker are replaced with generated codes; everything else
	/// passes through byte-for-byte. No conflict detection by owner ruling -
	/// gsa-cli rejects duplicated codes at import. Diagnostics go to stderr
	/// (exit 1); exactly one summary line goes to stdout on success.
	/// </summary>
	public static class Program
	{
		/// <summary>
		/// Internal failure carrier with the origin prefix already applied
		/// ("Input Error: ...", "Output Error: ...") - same error discipline as
		/// gsa-cli's CommandSyntaxException / Database Exception prefixes.
		/// </summary>
		private sealed class BcGenException : Exception
		{
			public BcGenException(string message) : base(message)
			{
			}
		}

		/// <summary>
		/// Entry point. Exit codes: 0 = success (one summary line on stdout),
		/// 1 = any failure (diagnostic on stderr, prefixed `bcgen:`).
		/// </summary>
		public static int Main(string[] args)
		{
			string input;
			string output;
			ulong seed;
			bool hasSeed;

			int parsed = ParseArgs(args, out input, out output, out seed, out hasSeed);
			if (parsed == 2) return 0;      // -h/--help: usage already shown on stdout
			if (parsed != 0) return 1;     // usage error already reported on stderr

			if (!hasSeed)
				seed = DefaultSeed();       // unseeded run: different codes each time

			string defaultMethod;
			try
			{
				defaultMethod = SettingsReader.CodeMethod();
			}
			catch (Xml2.XmlSettingsException ex)
			{
				Fail("Settings Error: " + ex.Message);
				return 1;
			}

			long rows;
			long rnd4Count;
			long upcaCount;
			long upcaNcCount;
			try
			{
				Process(input, output, seed, defaultMethod, out rows, out rnd4Count, out upcaCount, out upcaNcCount);
			}
			catch (BcGenException ex)
			{
				Fail(ex.Message);          // origin prefix already applied at the throw site
				return 1;
			}

			Console.WriteLine(string.Format(
				"bcgen: wrote {0} - rows={1}, generated={2} (rnd4={3}, upc-a={4}, upc-a-nc={5})",
				output, rows, rnd4Count + upcaCount + upcaNcCount, rnd4Count, upcaCount, upcaNcCount));
			return 0;
		}

		/// <summary>The single canonical usage line (pinned in tests).</summary>
		private static string UsageLine()
		{
			return "usage: bcgen <input.csv> <output.csv> [--seed N]";
		}

		/// <summary>
		/// Report a failure on stderr - stdout is reserved for the single
		/// success summary line so scripts can capture it cleanly.
		/// </summary>
		private static void Fail(string message)
		{
			Console.Error.WriteLine("bcgen: " + message);
		}

		/// <summary>
		/// Parse the command line. Returns 0 = ok, 1 = usage error (already
		/// reported on stderr), 2 = -h/--help shown on stdout.
		/// </summary>
		private static int ParseArgs(string[] args, out string input, out string output, out ulong seed, out bool hasSeed)
		{
			input = null;
			output = null;
			seed = 0UL;
			hasSeed = false;

			List<string> positional = new List<string>();
			for (int i = 0; i < args.Length; i++)
			{
				string a = args[i];

				if (a == "-h" || a == "--help")
				{
					Console.WriteLine(UsageLine());
					return 2;
				}

				if (a == "--seed")
				{
					if (i + 1 >= args.Length)
					{
						Fail("Usage Error: --seed takes a non-negative integer.");
						return 1;
					}

					i++;
					string s = args[i];
					ulong v;
					if (!TryParseSeed(s, out v))
					{
						Fail("Usage Error: --seed takes a non-negative integer (got '" + s + "').");
						return 1;
					}

					seed = v;
					hasSeed = true;
					continue;
				}

				if (a.Length > 0 && a[0] == '-')
				{
					Fail("Usage Error: unknown option '" + a + "'.");
					return 1;
				}

				positional.Add(a);
			}

			if (positional.Count != 2)
			{
				Fail("Usage Error: expected exactly two file arguments.");
				Console.Error.WriteLine(UsageLine());
				return 1;
			}

			input = positional[0];
			output = positional[1];

			// No in-place editing (D68): compare resolved full paths so a
			// trivially different spelling of the same file is still caught.
			try
			{
				if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
				{
					Fail("Usage Error: output must differ from input (no in-place editing).");
					return 1;
				}
			}
			catch (Exception)
			{
				// Illegal path characters etc. - the open will fail with a
				// proper Input/Output Error; nothing to decide here.
			}

			return 0;
		}

		/// <summary>
		/// Strict non-negative integer parse for --seed: digits only (no sign,
		/// no whitespace), any magnitude up to ulong max.
		/// </summary>
		private static bool TryParseSeed(string s, out ulong v)
		{
			v = 0UL;
			if (s == null || s.Length == 0)
				return false;

			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c < '0' || c > '9')
					return false;
			}

			return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
		}

		/// <summary>
		/// Time-based entropy for unseeded runs: different codes on each run,
		/// no shared RNG state (D68 - generation stays a pure function of the
		/// seed once chosen).
		/// </summary>
		private static ulong DefaultSeed()
		{
			return unchecked((ulong)Environment.TickCount * 0x9E3779B97F4A7C15UL ^ (ulong)AppDomain.CurrentDomain.Id);
		}

		/// <summary>
		/// D68 cell rule for column 1. Returns "rnd4", "upc-a", or "upc-a-nc"
		/// (D70) when the trimmed cell requests generation - empty, a zero code
		/// in any zero-padded spelling (B9), or an explicit rnd4/upc-a/upc-a-nc
		/// marker (case-insensitive, gsa-cli's CODE_METHOD spellings); null
		/// otherwise, meaning pass the value through byte-for-byte.
		/// </summary>
		private static string Classify(string cell, string defaultMethod)
		{
			if (cell == null)   // the library never emits nulls; defensive
				return null;

			string t = cell.Trim();
			if (t.Length == 0)
				return defaultMethod;

			if (IsZeroCode(t))
				return defaultMethod;

			string lower = t.ToLowerInvariant();
			if (lower == "rnd4")
				return "rnd4";

			if (lower == "upc-a")
				return "upc-a";

			if (lower == "upc-a-nc")
				return "upc-a-nc";   // D70: UPC-A without check digit

			return null;   // real code or other text: untouched
		}

		/// <summary>
		/// B9's zero-code rule: an all-digits spelling that parses to 0
		/// ("0", "00", ...). Non-numeric codes and values too long for a long
		/// are not zero codes.
		/// </summary>
		private static bool IsZeroCode(string code)
		{
			if (code.Length == 0)
				return false;

			for (int i = 0; i < code.Length; i++)
			{
				char c = code[i];
				if (c < '0' || c > '9')
					return false;
			}

			try
			{
				return long.Parse(code) == 0L;
			}
			catch (Exception)
			{
				return false;   // digit string too long for a long: not zero
			}
		}

		/// <summary>
		/// Open the input CSV. Worker count = Environment.ProcessorCount -
		/// multi-core aware (D68); the library clamps to its [1, 32] bound
		/// (CSV-9/10) and runs the parallel chunk-parse pipeline.
		/// </summary>
		private static CsvReader OpenInput(string path)
		{
			try
			{
				return CsvReader.OpenAsync(path, Environment.ProcessorCount).Result;   // .Result rethrows open faults
			}
			catch (CsvException ex)
			{
				throw new BcGenException("Input Error: " + CsvText(ex));
			}
			catch (IOException ex)
			{
				throw new BcGenException("Input Error: " + ex.Message);
			}
		}

		/// <summary>
		/// Open the output CSV (created/truncated on open). Same worker-count
		/// policy as OpenInput; the writer runs parallel batch formatting with
		/// backpressure and emits rows in strict file order.
		/// </summary>
		private static CsvWriter OpenOutput(string path)
		{
			try
			{
				return CsvWriter.OpenAsync(path, Environment.ProcessorCount).Result;
			}
			catch (CsvException ex)
			{
				throw new BcGenException("Output Error: " + CsvText(ex));
			}
			catch (IOException ex)
			{
				throw new BcGenException("Output Error: " + ex.Message);
			}
		}

		/// <summary>
		/// Format a CsvException with its line number when the library tied it
		/// to one (header is line 1).
		/// </summary>
		private static string CsvText(CsvException ex)
		{
			if (ex.Line > 0)
				return "line " + ex.Line + ": " + ex.Message;

			return ex.Message;
		}

		/// <summary>
		/// Wrap any output-side fault into the origin-prefixed carrier.
		/// </summary>
		private static BcGenException WrapOutput(Exception ex)
		{
			if (ex is CsvException)
				return new BcGenException("Output Error: " + CsvText((CsvException)ex));

			return new BcGenException("Output Error: " + ex.Message);
		}

		/// <summary>
		/// Best-effort removal of a partial result file after a failure - bcgen
		/// never leaves a half-written output behind (D68). Swallows all faults:
		/// the operator already has the real error to look at.
		/// </summary>
		private static void TryDelete(string path)
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception)
			{
				// best effort only
			}
		}

		/// <summary>
		/// The single streaming pass: read row -> classify column 1 -> derive
		/// the code when requested -> write row. Rows are never materialized;
		/// memory stays inside CSVToolkit's chunk/batch bound (D55-D57). Code
		/// derivation is a pure function of (seed, ordinal) so there is no
		/// shared state to synchronize.
		/// </summary>
		private static void Process(string input, string output, ulong seed, string defaultMethod,
			out long rows, out long rnd4Count, out long upcaCount, out long upcaNcCount)
		{
			rows = 0;
			rnd4Count = 0;
			upcaCount = 0;
			upcaNcCount = 0;

			CsvReader reader = null;
			bool outputOpened = false;
			BcGenException fault = null;

			try
			{
				reader = OpenInput(input);

				string[] header = reader.Header;   // clone per library contract
				if (header.Length < 1 || !header[0].Equals("ITEM_CODE", StringComparison.OrdinalIgnoreCase))
				{
					throw new BcGenException("Input Error: the first column must be named ITEM_CODE"
						+ (header.Length > 0 ? " (found '" + header[0] + "')." : "."));
				}

				CsvWriter writer = null;
				try
				{
					writer = OpenOutput(output);
					outputOpened = true;
					writer.WriteHeader(header);

					while (true)
					{
						CsvRow row;
						bool more;
						try
						{
							more = reader.TryReadRow(out row);
						}
						catch (CsvException ex)
						{
							throw new BcGenException("Input Error: " + CsvText(ex));
						}
						catch (IOException ex)
						{
							throw new BcGenException("Input Error: " + ex.Message);
						}

						if (!more)
							break;

						string[] cols = row.Columns;   // clone per library contract - safe to edit
						string method = Classify(cols[0], defaultMethod);
						if (method == "rnd4")
						{
							cols[0] = Codes.Rnd4At(seed, rnd4Count);
							rnd4Count++;
						}
						else if (method == "upc-a")
						{
							cols[0] = Codes.UpcAAt(seed, upcaCount);
							upcaCount++;
						}
						else if (method == "upc-a-nc")
						{
							cols[0] = Codes.UpcANoCheckAt(seed, upcaNcCount);
							upcaNcCount++;
						}

						try
						{
							writer.WriteRow(cols);
						}
						catch (Exception ex)
						{
							throw WrapOutput(ex);
						}

						rows++;
					}
				}
				catch (BcGenException ex)
				{
					fault = ex;   // capture first...
				}
				catch (CsvException ex)
				{
					if (fault == null) fault = WrapOutput(ex);   // e.g. WriteHeader on a bad value
				}
				catch (IOException ex)
				{
					if (fault == null) fault = new BcGenException("Output Error: " + ex.Message);
				}
				finally
				{
					if (writer != null)
					{
						// ...then close. Close surfaces the first pipeline fault in
						// file order; a body fault always wins over a close fault.
						try
						{
							writer.Close();
						}
						catch (Exception ex)
						{
							if (fault == null)
								fault = WrapOutput(ex);
						}
					}
				}

				if (fault != null)
					throw fault;
			}
			catch (BcGenException)
			{
				if (outputOpened)
					TryDelete(output);   // never leave a partial result file behind
				throw;
			}
			finally
			{
				if (reader != null)
				{
					try { reader.Close(); } catch (Exception) { /* cleanup only */ }
				}
			}
		}
	}
}
