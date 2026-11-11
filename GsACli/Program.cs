// Program - entry point, argument dispatch, and startup pipeline (Phase 3).
//
// Entry modes (PROJECT.md):
//   gsa-cli [DB.DAT] -import F   one-shot import  (Phase 5; stub for now)
//   gsa-cli [DB.DAT] -play S     one-shot play    (Phase 5; stub for now)
//   gsa-cli              interactive config mode
//   gsa-cli <DB.DAT>     interactive, database preloaded (plain-LOAD
//                        semantics; any existing lock refuses the start)
//   gsa-cli -verify F    one-shot integrity check (Phase 3; exit 0/1)
//
// Startup pipeline (config/db REPL modes only; one-shots stay stubs):
//   banner
//   settings template copy-out (D40, SettingsBootstrap)
//   settings load with corrupt recovery (D28: rename aside + fresh copy)
//   cross-instance scan (D29: edits gated, reads fine)
//   session start; <DB.DAT> argument loads via plain-LOAD semantics
//   REPL loop (Repl.Execute), exit code 0 on clean termination

using System;
using System.IO;

namespace GsACli
{
	public static class Program
	{
		/// <summary>
		/// Startup banner - authoritative wording per PROJECT.md (the
		/// spec's launch screen). Printed exactly once at launch;
		/// LOAD/UNLOAD never repeat it (D34).
		/// </summary>
		private static void PrintBanner()
		{
			Console.WriteLine("gsa-cli command line utility for EALITEMR.DAT");
			Console.WriteLine("Copyleft 2026 - The Crusty Computer Club, https://crusty.computer");
			Console.WriteLine("With thanks to the Dongvile Point-of-Sale Community");
		}

		/// <summary>
		/// Settings boot (D28/D29/D40): template copy-out, load, corrupt
		/// recovery (rename aside + fresh copy, spec-locked notice), and
		/// the cross-instance read-only gate (notice printed here, the
		/// gate enforced in Session). Returns false on a fatal settings
		/// failure with the operator message already printed; the
		/// process then exits 1.
		/// </summary>
		private static bool BootSettings(out Settings settings, out bool readOnly)
		{
			settings = null;
			readOnly = false;

			string path = SettingsBootstrap.EnsureLocalTemplate();
			if (path == null)
			{
				Console.WriteLine("Settings Error: cannot locate the settings template or working directory.");
				return false;
			}

			try
			{
				settings = Settings.Load(path);
			}
			catch (Xml2.XmlSettingsException e)
			{
				// D28: name the corrupt file aside, drop a fresh one in its
				// place, print the recovery notice.
				string renamed;
				try
				{
					renamed = Settings.RenameCorruptAside(path);
				}
				catch (IOException moveFail)
				{
					Console.WriteLine(Settings.FormatXmlError(e));
					Console.WriteLine("Settings Error: could not rename the corrupt Settings.xml (" + moveFail.Message + ").");
					return false;
				}

				SettingsBootstrap.EnsureLocalTemplate();

				Console.WriteLine("Settings Error: Settings.xml was corrupt - renamed to " + renamed + "; a fresh defaults file was created.");

				try
				{
					settings = Settings.Load(path);
				}
				catch (Exception e2)
				{
					Console.WriteLine("Settings Error: cannot read the fresh settings file (" + e2.Message + ").");
					return false;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Settings Error: cannot read Settings.xml (" + e.Message + ").");
				return false;
			}

			string others = InstanceGuard.DescribeOtherInstances();
			if (others != null)
			{
				readOnly = true;
				Console.WriteLine("Note: another gsa-cli instance is running (" + others + "); settings editing is disabled for this session.");
			}

			return true;
		}

		/// <summary>
		/// Main dispatch. Exit codes: 0 clean, 1 fatal boot or preload
		/// failure, 2 usage (spec table + Phase 0 commitments).
		/// </summary>
		public static int Main(string[] args)
		{
			PrintBanner();

			// One-shot modes (Phase 5: D16/D41/D44).
			if (args.Length == 3 && args[0].Length > 0 && args[1].Length > 0)
			{
				string flag = args[1].ToLowerInvariant();
				if (flag == "-import")
				{
					return RunOneShotImport(args[0], args[2]);
				}
				if (flag == "-play")
				{
					return RunOneShotPlay(args[0], args[2]);
				}
			}

			// One-shot VERIFY (Phase 3): in-place integrity check. No settings
			// boot, no lock file - it never claims the database.
			if (args.Length == 2 && args[0].ToLowerInvariant() == "-verify")
			{
				return RunVerify(args[1]);
			}

			if (args.Length > 1 || args.Length == 1 && args[0].StartsWith("-"))
			{
				Usage();
				return 2;
			}

			// Interactive launch screen (spec): banner, blank line,
			// "No file has been loaded", blank line, then the config> prompt.
			// A database argument suppresses the notice - that session goes
			// straight to database mode and the load message speaks for itself.
			Console.WriteLine();
			if (args.Length == 0)
			{
				Console.WriteLine("No file has been loaded");
				Console.WriteLine();
			}

			Settings settings;
			bool readOnly;
			if (!BootSettings(out settings, out readOnly))
			{
				return 1;
			}

			Session session = new Session(settings, readOnly);

			if (args.Length == 1)
			{
				if (!session.TryAcquireDatabase(args[0], false))
				{
					session.Close();
					return 1;
				}
			}

			// ^C interrupts the current operation and returns to the prompt
			// instead of terminating the process (Abort.cs). One-shot modes
			// above keep the default terminate-on-^C: nothing in them polls
			// the flag, so cancelling would only look like a hang.
			Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
			{
				e.Cancel = true;
				Abort.Request();
			};

			return Repl.Run(session);
		}

		/// <summary>
		/// One-shot IMPORT (D41/D44): boot settings, load the database with
		/// plain-LOAD semantics, import the whole file (no slice/filter), exit
		/// 0 on a completed run and 1 on any hard failure. If the database
		/// cannot be loaded, no valid rows are accepted (all-or-nothing).
		/// </summary>
		private static int RunOneShotImport(string database, string source)
		{
			Settings settings;
			bool readOnly;
			if (!BootSettings(out settings, out readOnly))
			{
				return 1;
			}

			Session session = new Session(settings, readOnly);
			try
			{
				if (!session.TryAcquireDatabase(database, false))
				{
					return 1; // D41: nothing was staged - the run never started
				}

				return session.ImportWholeFile(source) ? 0 : 1;
			}
			finally
			{
				session.Close();
			}
		}

		/// <summary>
		/// One-shot PLAY (D16/D41): boot settings, load the database with plain
		/// LOAD semantics, play the script silently. Exit 0 only if every line
		/// completed cleanly (the terminal COMMIT made it durable); exit 1 on a
		/// pre-parse failure or any failed execution line (staged changes are
		/// rolled back by PlayScript before returning).
		/// </summary>
		private static int RunOneShotPlay(string database, string script)
		{
			Settings settings;
			bool readOnly;
			if (!BootSettings(out settings, out readOnly))
			{
				return 1;
			}

			Session session = new Session(settings, readOnly);
			try
			{
				if (!session.TryAcquireDatabase(database, false))
			{
					return 1; // D41: nothing was staged - the run never started
				}

				return session.PlayScript(script, false) ? 0 : 1;
			}
			finally
			{
				session.Close();
			}
		}

		/// <summary>
		/// One-shot VERIFY (spec "VERIFY"): open the .DAT read-only and run
		/// EalItemr's integrity check. Success prints the spec card and exits
		/// 0; any failure prints the exact library exception verbatim after
		/// "Database Exception:" and exits 1 (D31/D46). No settings boot, no
		/// lock file - VERIFY never mutates or claims the database.
		/// </summary>
		private static int RunVerify(string path)
		{
			C3.EalItemr.EalItemrFile file = null;
			try
			{
				file = C3.EalItemr.EalItemrFile.Open(path);

				int total = file.CountRecordSlotsAsync().Result;
				int used = file.CountUsedSlotsAsync().Result;
				C3.EalItemr.VerifyReport report = file.VerifyAsync().Result;

				// Independent cross-check for the spec card: derive the slot
				// count from the raw file size (blocks x 10, minus block 0)
				// and compare it against the header's own claim.
				long fileSize = new FileInfo(path).Length;
				int calcSlots = (int)(fileSize / C3.EalItemr.EalItemrFile.BlockSize - 1) * C3.EalItemr.EalItemrFile.SlotsPerBlock;

				Console.WriteLine("File: " + path);
				Console.WriteLine("Header OK");
				Console.WriteLine("Slots: " + total.ToString() + " (header) == " + calcSlots.ToString() + " (calculated from file size)");
				Console.WriteLine("Records: " + used.ToString() + " in use, " + (total - used).ToString() + " free");

				if (report.OrphanRecords != 0)
				{
					Console.WriteLine("VERIFY FAILED (" + report.OrphanRecords.ToString() + " orphaned record(s)).");
					return 1;
				}

				Console.WriteLine("VERIFY OK");
				return 0;
		}
		catch (Exception e)
		{
			Console.WriteLine("Database Exception: " + e.Message);
			return 1;
		}
		finally
		{
			if (file != null)
			{
				file.Dispose();
			}
		}
	}

		/// <summary>
		/// Usage table for bad invocations (Phase 1 wording kept; the
		/// one-shot forms list their Phase 5 status honestly).
		/// </summary>
		private static void Usage()
		{
			Console.WriteLine("Usage:");
			Console.WriteLine("  gsa-cli [DB.DAT] -import F   import a CSV (Phase 5)");
			Console.WriteLine("  gsa-cli [DB.DAT] -play S      run a play script (Phase 5)");
			Console.WriteLine("  gsa-cli -verify <DB.DAT>     verify database integrity (one-shot)");
			Console.WriteLine("  gsa-cli                    - interactive config mode");
			Console.WriteLine("  gsa-cli <DB.DAT>             - interactive, database preloaded");
		}
	}
}
