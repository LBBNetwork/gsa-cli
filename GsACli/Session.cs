// Session - command dispatch and session state (Phase 3).
//
// One Session owns everything a REPL loop needs: the loaded settings
// file (D40), the read-only gate (D29: another gsa-cli running), the
// current mode (config vs database loaded), the loaded database's
// raw-typed path (D30/D34 - operator spelling is preserved for prompts
// and messages), the staged-changes counter (always 0 until Phase 4;
// the BYE/UNLOAD FORCE machinery around it is wired now so Phase 4 only
// flips the counter), and the record engine (Bridge/EalItemrEngine over
// C3.EalItemr - read side live in Phase 3, mutation stubbed for Phase 4).
//
// Message discipline follows SPECIFICATION.md:
//   - Command Error:      grammar / unknown-command / mode conflicts
//   - Validation Error:    value or syntax validation failures
//   - Database Exception:  .DAT-level load failures
//   - Settings Error:      settings.xml IO failures and corrupt recovery
//   - bare strings:        verbatim spec examples (Exiting!, in-use,
//                          LOAD OK, UNLOAD OK)
//
// Phase 4 command surface: BYE, QUIT, CONFIG, SHOW, LOAD, UNLOAD, HELP,
// SELECT, COUNT plus the mutating verbs - INSERT, UPDATE, DROP, RECODE,
// EXTEND, COMMIT, ROLLBACK, CREATE NEW (D1/D2/D3/D15-D19). Mutations stage
// in RAM and are visible in-session; they become durable on COMMIT (D1),
// and the stagedChanges counter feeds the BYE/UNLOAD FORCE machinery.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GsACli.Bridge;

namespace GsACli
{
	public class Session
	{
		private readonly Settings settings;
		private readonly bool settingsReadOnly;
		private readonly IRecordEngine engine;
		private readonly ICsvEngine csv;
		private bool databaseLoaded;
		private string dbAsGiven;
		private int stagedChanges;

		/// <summary>
		/// D60: IMPORT suppresses per-row inline warnings (name truncation) and
		/// aggregates them into one summary line at the end of the run.
		/// </summary>
		private bool quietWarnings;
		private int nameTruncations;

		/// <summary>
		/// D51: the most recent successful SHOW DATABASES listing - the paths
		/// behind the printed numbers, so LOAD can accept them. null means no
		/// valid numbering (never listed, or the last search found nothing).
		/// </summary>
		private string[] listedDatabases;

		/// <summary>
		/// Create a session over an already-loaded settings file.
		/// settingsReadOnly applies the D29 gate (another gsa-cli is
		/// running); reads stay fully usable in that mode.
		/// </summary>
		public Session(Settings settings, bool settingsReadOnly)
		{
			this.settings = settings;
			this.settingsReadOnly = settingsReadOnly;
			this.engine = new EalItemrEngine();
			this.csv = new CsvEngine(); // Phase 5a: real streaming CSV bridge (D60)
			this.databaseLoaded = false;
			this.dbAsGiven = null;
			this.stagedChanges = 0;
		}

		/// <summary>
		/// True while a database is loaded; the Repl switches prompts on
		/// this (spec: "gsa [EALITEMR.DAT]> " vs "config> ").
		/// </summary>
		public bool DatabaseLoaded
		{
			get
			{
				return this.databaseLoaded;
			}
		}

		/// <summary>
		/// The loaded database's path exactly as the operator gave it
		/// (D30/D34 preserved spelling, not normalized), null when no
		/// database is loaded.
		/// </summary>
		public string DatabaseAsGiven
		{
			get
			{
				return this.dbAsGiven;
			}
		}

		/// <summary>
		/// Runs one operator line. Returns false only for BYE/QUIT (the
		/// session is done); every other outcome - including all errors -
		/// returns true so the Repl keeps prompting.
		/// </summary>
		public bool Execute(string line)
		{
			string[] tokens;
			int[] starts; // token offsets in `line` (Phase 4 clause re-scans)
			try
			{
				tokens = CommandLex.Lex(line, out starts);
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return true;
			}

			if (tokens.Length == 0)
			{
				return true;
			}

			string verb = tokens[0].ToUpperInvariant();

			switch (verb)
			{
				case "BYE":
				case "QUIT":
					return DoBye(tokens);

				case "CONFIG":
					DoConfig(tokens);
					return true;

				case "SHOW":
					DoShow(tokens);
					return true;

				case "LOAD":
					DoLoad(tokens);
					return true;

				case "UNLOAD":
					DoUnload(tokens);
					return true;

				case "HELP":
					DoHelp();
					return true;

				case "SELECT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoSelect(tokens);
					return true;

				case "COUNT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoCount(tokens);
					return true;

				case "EXPORT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoExport(tokens);
					return true;

				case "IMPORT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}

					DoImport(tokens);
					return true;

				case "PLAY":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoPlay(tokens);
					return true;

				case "INSERT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoInsert(tokens, line, starts);
					return true;

				case "UPDATE":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoUpdate(tokens, line, starts);
					return true;

				case "DROP":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoDrop(tokens);
					return true;

				case "RECODE":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoRecode(tokens);
					return true;

				case "EXTEND":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoExtend(tokens);
					return true;

				case "COMMIT":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoCommit();
					return true;

				case "ROLLBACK":
					if (!this.databaseLoaded)
					{
						Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
						return true;
					}
					DoRollback();
					return true;

				case "CREATE":
					if (tokens.Length >= 2 && tokens[1].Equals("CSV", StringComparison.OrdinalIgnoreCase))
					{
						// CREATE CSV works in config mode and with a database loaded.
						DoCreateCsv(tokens);
					}
					else
					{
						// CREATE NEW works in config mode (it creates a new file);
						// the same-file guard inside DoCreateNew covers the loaded case.
						DoCreateNew(tokens);
					}
					return true;
			}

			Console.WriteLine("Command Error: unknown command '" + tokens[0] + "'.");
			return true;
		}

		/// <summary>
		/// BYE / QUIT (spec "BYE - Clean exit" table): plain exits clean;
		/// with staged work pending the session refuses and points at
		/// FORCE; FORCE discards staged work and exits. In Phase 2 the
		/// staged counter is always 0, so the refusal branch exercises
		/// only once Phase 4 lands - the strings are spec-locked now.
		/// </summary>
		private bool DoBye(string[] tokens)
		{
			bool force = false;

			if (tokens.Length > 1)
			{
				if (tokens.Length == 2 && tokens[1].ToUpperInvariant() == "FORCE")
				{
					force = true;
				}
				else
				{
					Console.WriteLine("Command Error: BYE takes no argument, or BYE FORCE.");
					return true;
				}
			}

			if (!force && this.stagedChanges > 0)
			{
				Console.WriteLine("The database has pending commits. COMMIT your changes first or specify QUIT / BYE FORCE; to exit anyway.");
				return true;
			}

			if (force && this.stagedChanges > 0)
			{
				Console.WriteLine("Staged changes discarded.");
			}

			Close();
			Console.WriteLine("Exiting!");
			return false;
		}

		/// <summary>
		/// UNLOAD (spec "UNLOAD" + D34 mode-crossing rule). In config
		/// mode the crossing error is the spec-locked string;
		/// with a database loaded, plain UNLOAD refuses while work is
		/// staged, FORCE discards, both release the lock file (D14)
		/// and return to config mode.
		/// </summary>
		private void DoUnload(string[] tokens)
		{
			if (!this.databaseLoaded)
			{
				Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
				return;
			}

			bool force = false;

			if (tokens.Length > 1)
			{
				if (tokens.Length == 2 && tokens[1].ToUpperInvariant() == "FORCE")
				{
					force = true;
				}
				else
				{
					Console.WriteLine("Command Error: UNLOAD takes no argument, or UNLOAD FORCE.");
					return;
				}
			}

			if (!force && this.stagedChanges > 0)
			{
				Console.WriteLine("The database has pending commits. COMMIT your changes first, or specify UNLOAD FORCE; to unload anyway.");
				return;
			}

			if (force && this.stagedChanges > 0)
			{
				Console.WriteLine("Staged changes discarded.");
			}

			ReleaseDatabase();

			// D53: force a full GC so the database's heap (potentially GBs of
			// records) is reclaimed now, not on some future allocation trigger.
			// An idle REPL makes no allocations, so without this the garbage
			// would sit committed forever - and its commit charge keeps counting
			// against the system limit on RAM-constrained 32-bit hosts. BYE does
			// not do this: process exit reclaims everything anyway.
			GC.Collect();
			GC.WaitForPendingFinalizers();

			Console.WriteLine("Database successfully unloaded");
		}

		/// <summary>
		/// LOAD (spec "LOAD": load .DAT, acquire lock, enter database
		/// mode). Phase 2 surface: file-existence check plus the D14/D30
		/// lock contract; the record engine itself stays a stub until
		/// Phase 4, and the success line says so. The argument may also be
		/// a number from the last SHOW DATABASES listing (D51).
		/// </summary>
		private void DoLoad(string[] tokens)
		{
			if (this.databaseLoaded)
			{
				Console.WriteLine("Command Error: A database is already loaded (" + Path.GetFileName(this.dbAsGiven) + "). UNLOAD first (UNLOAD FORCE discards staged changes).");
				return;
			}

			bool allowOverride = false;
			string path = null;

			if (tokens.Length == 2)
			{
				path = tokens[1];
			}
			else if (tokens.Length == 3 && tokens[1].ToUpperInvariant() == "OVERRIDE")
			{
				allowOverride = true;
				path = tokens[2];
			}
			else
			{
				Console.WriteLine("Command Error: LOAD takes a file, or LOAD OVERRIDE <file>.");
				return;
			}

			if (path.Length == 0)
			{
				Console.WriteLine("Command Error: LOAD takes a file, or LOAD OVERRIDE <file>.");
				return;
			}

			string listed = null;
			if (this.TryResolveListingNumber(path, out listed))
			{
				path = listed;
			}
			else if (IsAllDigits(path))
			{
				return; // numbered reference that failed to resolve - error already printed
			}

			TryAcquireDatabase(path, allowOverride);
		}

		/// <summary>
		/// D52: newest first by last-modified time; ties break alphabetically
		/// by name so the order is deterministic regardless of sort stability.
		/// </summary>
		private static int CompareNewestFirst(FileInfo a, FileInfo b)
		{
			int byTime = b.LastWriteTime.CompareTo(a.LastWriteTime);

			if (byTime != 0)
			{
				return byTime;
			}

			return String.CompareOrdinal(a.Name, b.Name);
		}

		/// <summary>
		/// D51: true when every character is an ASCII digit (a LOAD number
		/// candidate). Empty strings are not numbers.
		/// </summary>
		private static bool IsAllDigits(string s)
		{
			if (s.Length == 0)
			{
				return false;
			}

			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c < '0' || c > '9')
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// D51: resolve a LOAD argument against the most recent SHOW DATABASES
		/// listing. Returns true and sets path when token is a valid 1-based
		/// number in the stored listing. When token is all digits but does not
		/// resolve (no listing yet, or out of range), prints the Command Error
		/// and returns false; callers must then stop (see IsAllDigits).
		/// Non-numeric tokens return false silently - they are file paths.
		/// </summary>
		private bool TryResolveListingNumber(string token, out string path)
		{
			path = null;

			if (!IsAllDigits(token))
			{
				return false; // not a numbered reference - caller treats it as a path
			}

			long n;
			bool inRange = long.TryParse(token, out n) && this.listedDatabases != null && n >= 1 && n <= (long)this.listedDatabases.Length;

			if (!inRange)
			{
				if (this.listedDatabases == null)
				{
					Console.WriteLine("Command Error: no numbered databases yet. Run SHOW DATABASES first.");
				}
				else
				{
					Console.WriteLine("Command Error: no database #" + token + " in the last listing (1-" + this.listedDatabases.Length.ToString() + ").");
				}
				return false;
			}

			path = this.listedDatabases[(int)n - 1];
			return true;
		}

		/// <summary>
		/// Core of LOAD (spec "LOAD" success/failure examples): lock
		/// acquisition with the D14 in-use / OVERRIDE matrix, then engine
		/// open (D31: exact EalItemr exception verbatim after
		/// "Database Exception:"), then real counts for the success line.
		/// Returns false on refusal (already printed); true on success.
		/// Shared by DoLoad and the gsa-cli <DB.DAT> startup - the startup
		/// path is plain LOAD (no OVERRIDE).
		/// </summary>
		public bool TryAcquireDatabase(string asGiven, bool allowOverride)
		{
			LockResult outcome;
			try
			{
				outcome = DatabaseLock.TryAcquire(asGiven, allowOverride);
			}
			catch (IOException e)
			{
				Console.WriteLine("Database Exception: Could not write lock file for " + asGiven + " (" + e.Message + ").");
				return false;
			}

			if (outcome.Outcome != LockOutcome.Acquired)
			{
				if (outcome.Outcome == LockOutcome.OverrideRefused)
				{
					Console.WriteLine(asGiven + " is in use by live gsa-cli process " + outcome.HolderPid.ToString() + "; the override was refused.");
				}
				else if (outcome.HolderPid > 0)
				{
					Console.WriteLine(asGiven + " is already in use by process " + outcome.HolderPid.ToString() + ".");
				}
				else
				{
					Console.WriteLine(asGiven + " is already in use (lock file present, owner unknown - use LOAD OVERRIDE if you are sure it is stale).");
				}
				return false;
			}

			try
			{
				this.engine.Open(asGiven);
			}
			catch (Exception e)
			{
				DatabaseLock.Release(asGiven);
				Console.WriteLine("Database Exception: " + e.Message);
				return false;
			}

			int used;
			int total;
			try
			{
				used = this.engine.UsedSlots();
				total = this.engine.TotalSlots();
			}
			catch (Exception e)
			{
				this.engine.Close();
				DatabaseLock.Release(asGiven);
				Console.WriteLine("Database Exception: " + e.Message);
				return false;
			}

			this.databaseLoaded = true;
			this.dbAsGiven = asGiven;
			this.stagedChanges = 0;

			Console.WriteLine("Loaded " + asGiven + " OK - " + used.ToString() + " records in use, " + (total - used).ToString() + " slots free");
			return true;
		}

		/// <summary>
		/// CONFIG in config mode: key=value pairs (D34) plus the
		/// D34 department form "CONFIG DEPARTMENT <n> '<Name>'". In database
		/// mode the spec-locked crossing answer wins over everything
		/// else on the line (mode conflict is checked before grammar).
		/// </summary>
		private void DoConfig(string[] tokens)
		{
			if (this.databaseLoaded)
			{
				Console.WriteLine("Invalid command for database mode. Use SHOW CONFIG to view settings.");
				return;
			}

			string[] rest = TokenTail(tokens, 1);

			if (rest.Length == 0)
			{
				Console.WriteLine("Command Error: CONFIG takes KEY=VALUE pairs, or CONFIG DEPARTMENT <n> '<name>'.");
				return;
			}

			if (rest[0].ToUpperInvariant() == "DEPARTMENT")
			{
				HandleDepartment(rest);
				return;
			}

			HandlePairs(rest);
		}

		/// <summary>
		/// tokens[start..] as a new array (empty array when start is out
		/// of range). Keeps the handlers free of index arithmetic.
		/// </summary>
		private static string[] TokenTail(string[] tokens, int start)
		{
			if (start <= 0)
			{
				return tokens;
			}
			if (start >= tokens.Length)
			{
				return new string[0];
			}

			string[] tail = new string[tokens.Length - start];
			for (int i = 0; i < tail.Length; i++)
			{
				tail[i] = tokens[start + i];
			}

			return tail;
		}

		/// <summary>
		/// CONFIG DEPARTMENT <n> '<Name>' (D34): upserts number n (1-99)
		/// to Name, saving atomically. Validation order: shape, read-only
		/// gate, number range, empty name. The success line carries the
		/// operator's spelling of the name (D30 philosophy).
		/// </summary>
		private void HandleDepartment(string[] rest)
		{
			if (rest.Length != 3)
			{
				Console.WriteLine("Command Error: CONFIG DEPARTMENT takes a number (1-99) and a name.");
				return;
			}

			if (this.settingsReadOnly)
			{
				Console.WriteLine("Command Error: settings editing is disabled while another gsa-cli instance is running.");
				return;
			}

			int number;
			if (!int.TryParse(rest[1], out number) || number < 1 || number > 99)
			{
				Console.WriteLine("Validation Error: department number must be 1-99.");
				return;
			}

			string name = rest[2];
			if (name.Length == 0)
			{
				Console.WriteLine("Validation Error: department name must not be empty.");
				return;
			}

			this.settings.SetDepartment(number.ToString(), name);

			if (!SaveSettings())
			{
				return;
			}

			Console.WriteLine("OK, Settings.xml updated (DEPARTMENT " + number.ToString() + "=" + name + ")");
		}

		/// <summary>
		/// CONFIG key=value [...] (D34 pairs, D40 key list). Atomic: every
		/// pair is looked up and value-validated before any write; the
		/// first failure aborts with its message and nothing is saved.
		/// Single-pair success echoes the change; multi-pair just the
		/// file (spec's two verbatim success lines).
		/// </summary>
		private void HandlePairs(string[] rest)
		{
			if (this.settingsReadOnly)
			{
				Console.WriteLine("Command Error: settings editing is disabled while another gsa-cli instance is running.");
				return;
			}

			ListEntry[] pending = new ListEntry[rest.Length];
			int made = 0;

			for (int i = 0; i < rest.Length; )
			{
				int eq = rest[i].IndexOf('=');
				string operatorKey;
				string rawValue;
				int step;

				if (eq < 0)
				{
					// "KEY = VALUE": fully space-separated pair - the most
					// common human form against the attached KEY=VALUE, so
					// accept it rather than force a retype.
					if (i + 2 >= rest.Length || rest[i + 1] != "=")
					{
						Console.WriteLine("Command Error: CONFIG pairs need a form key=value.");
						return;
					}
					operatorKey = rest[i];
					rawValue = rest[i + 2];
					step = 3;
				}
				else if (eq == 0)
				{
					Console.WriteLine("Command Error: CONFIG pairs need a form key=value.");
					return;
				}
				else if (eq == rest[i].Length - 1)
				{
					// "KEY= value": the value is the next token (the spec's
					// value forms allow quoted strings, which a paste or a
					// space may leave as a separate token).
					if (i + 1 >= rest.Length)
					{
						Console.WriteLine("Command Error: CONFIG pairs need a form key=value.");
						return;
					}
					operatorKey = rest[i].Substring(0, eq);
					rawValue = rest[i + 1];
					step = 2;
				}
				else
				{
					// "KEY=VALUE" as one token
					operatorKey = rest[i].Substring(0, eq);
					rawValue = rest[i].Substring(eq + 1);
					step = 1;
				}

				i += step;

				string fileKey = null;
				string current = null;
				if (!this.settings.TryGet(operatorKey, out fileKey, out current))
				{
					Console.WriteLine("Command Error: unknown settings parameter '" + operatorKey + "'. SHOW CONFIG lists valid parameters.");
					return;
				}

				if (rawValue.Length == 0)
				{
					Console.WriteLine("Validation Error: " + fileKey + " value must not be empty.");
					return;
				}

				string stored = null;
				if (!ValidateValue(fileKey, rawValue, out stored))
				{
					return;
				}

				pending[made] = new ListEntry(fileKey, stored);
				made += 1;
			}

			for (int i = 0; i < made; i++)
			{
				this.settings.Set(pending[i].Key, pending[i].Value);
			}

			if (!SaveSettings())
			{
				return;
			}

			if (made == 1)
			{
				Console.WriteLine("OK, Settings.xml updated (" + pending[0].Key + "=" + pending[0].Value + ")");
			}
			else
			{
				Console.WriteLine("OK, Settings.xml updated");
			}
		}

		/// <summary>
		/// A validated key/stored-value pair awaiting save; lets
		/// HandlePairs separate the all-or-nothing validate pass from
		/// the apply pass.
		/// </summary>
		private struct ListEntry
		{
			public readonly string Key;
			public readonly string Value;

			public ListEntry(string key, string value)
			{
				this.Key = key;
				this.Value = value;
			}
		}

		/// <summary>
		/// Validates rawValue against the file key's allowed set and
		/// produces the canonical stored form (booleans Y/N uppercase,
		/// method names lowercase, numerics trimmed). Returns false
		/// with the operator message printed when the value is outside
		/// the key's set. Unknown key classes (future D40 additions)
		/// accept any non-empty value - permissiveness at the edge is
		/// the simpler failure mode (Rule 2) until a phase pins a rule.
		/// </summary>
		private bool ValidateValue(string fileKey, string rawValue, out string stored)
		{
			string key = fileKey.ToUpperInvariant();
			string lower = rawValue.ToLowerInvariant();

			if (key == "CODE_METHOD")
			{
				if (lower == "upc-a" || lower == "rnd4")
				{
					stored = lower;
					return true;
				}
				Console.WriteLine("Validation Error: CODE_METHOD accepts 'upc-a' or 'rnd4'.");
				stored = null;
				return false;
			}

			if (key == "NAME_VALIDATION")
			{
				if (lower == "reject" || lower == "truncate")
				{
					stored = lower;
					return true;
				}
				Console.WriteLine("Validation Error: NAME_VALIDATION accepts 'reject' or 'truncate'.");
				stored = null;
				return false;
			}

			if (IsBooleanKey(key))
			{
				if (lower == "y" || lower == "n")
				{
					stored = lower.ToUpperInvariant();
					return true;
				}
				Console.WriteLine("Validation Error: " + fileKey + " accepts 'Y' or 'N'.");
				stored = null;
				return false;
			}

			if (key == "QTY")
			{
				// B5: the GSA file format stores SALEQUAN$ as one packed-decimal
				// byte (2 digits, 0-99) - gsasrc/EALDMESC.BAS packs it with
				// NEW.PACK$(..., 2). The old 0-99999 range accepted values that
				// INSERT then rejected with a raw engine exception.
				long q;
				if (long.TryParse(rawValue, out q) && q >= 0 && q <= 99)
				{
					stored = q.ToString();
					return true;
				}
				Console.WriteLine("Validation Error: QTY must be a whole number 0-99.");
				stored = null;
				return false;
			}

			if (key == "PRICE_METHOD" || key == "DCS_METHOD")
			{
				if (rawValue.Length == 1 && rawValue[0] >= '0' && rawValue[0] <= '9')
				{
					stored = rawValue;
					return true;
				}
				Console.WriteLine("Validation Error: " + fileKey + " must be a digit 0-9.");
				stored = null;
				return false;
			}

			stored = rawValue;
			return true;
		}

		/// <summary>
		/// The D22/D38 boolean-class keys: everything on the D40 list
		/// that takes Y or N.
		/// </summary>
		private static bool IsBooleanKey(string upperKey)
		{
			switch (upperKey)
			{
				case "TRACK_MOVEMENT":
				case "REQUIRE_QTY":
				case "REQUIRE_PRICE":
				case "SELL_HOURS":
				case "SELL_RESTRICTED":
				case "NO_RETURN":
				case "GIVEAWAY":
				case "TAX1":
				case "TAX2":
				case "TAX3":
				case "TAX4":
					return true;
			}
			return false;
		}

		/// <summary>
		/// SHOW family (D22, D50; spec "SHOW" tables).
		///   SHOW CONFIG          - both modes, file-order KEY=value lines
		///   SHOW DEPARTMENTS     - config mode only; " <n> - <NAME>" lines
		///   SHOW DATABASES [dir] - config mode only; *.DAT files in dir
		///                         (default: working directory)
		///   SHOW DATABASE        - database mode only; the 4-line card
		/// The db-mode SHOW CONFIG answer exists because the crossing
		/// errors point the operator at it (spec's CONFIG-in-db-mode
		/// line).
		/// </summary>
		private void DoShow(string[] tokens)
		{
			if (tokens.Length < 2)
			{
				Console.WriteLine("Command Error: SHOW takes CONFIG, DEPARTMENTS, DATABASES, or DATABASE.");
				return;
			}

			string sub = tokens[1].ToUpperInvariant();

			if (sub == "CONFIG")
			{
				if (tokens.Length != 2)
				{
					Console.WriteLine("Command Error: SHOW CONFIG takes no argument.");
					return;
				}

				List<KeyValuePair<string, string>> rows = this.settings.EnumerateKeys();
				for (int i = 0; i < rows.Count; i++)
				{
					Console.WriteLine(rows[i].Key + "=" + rows[i].Value);
				}
				return;
			}

			if (sub == "DEPARTMENTS")
			{
				if (tokens.Length != 2)
				{
					Console.WriteLine("Command Error: SHOW DEPARTMENTS takes no argument.");
					return;
				}

				if (this.databaseLoaded)
				{
					Console.WriteLine("Command Error: only SHOW DATABASE is available in database mode.");
					return;
				}

				List<KeyValuePair<string, string>> depts = this.settings.EnumerateDepartments();
				if (depts.Count == 0)
				{
					Console.WriteLine("Command Error: no departments found in Settings.xml.");
					return;
				}

				for (int i = 0; i < depts.Count; i++)
				{
					Console.WriteLine(" " + depts[i].Key + " - " + depts[i].Value);
				}
				return;
			}

			if (sub == "DATABASES")
			{
				if (this.databaseLoaded)
				{
					Console.WriteLine("Command Error: SHOW DATABASES is only available in config mode. UNLOAD first.");
					return;
				}

				string dir = null;
				if (tokens.Length == 3)
				{
					dir = tokens[2];
				}
				else if (tokens.Length > 3)
				{
					Console.WriteLine("Command Error: SHOW DATABASES takes no argument, or one directory.");
					return;
				}

				if (dir == null)
				{
					dir = Directory.GetCurrentDirectory();
				}

				if (!Directory.Exists(dir))
				{
					Console.WriteLine("Command Error: no such directory '" + dir + "'.");
					return;
				}

				string[] matches;
				try
				{
					matches = Directory.GetFiles(dir, "*.DAT");
				}
				catch (IOException e)
				{
					Console.WriteLine("Command Error: cannot read directory '" + dir + "': " + e.Message);
					return;
				}

				if (matches.Length == 0)
				{
					this.listedDatabases = null; // D51: an empty search clears the numbering
					Console.WriteLine("No Databases Found");
					return;
				}

				FileInfo[] files = new FileInfo[matches.Length];
				for (int i = 0; i < matches.Length; i++)
				{
					files[i] = new FileInfo(matches[i]);
				}

				Array.Sort(files, new Comparison<FileInfo>(CompareNewestFirst)); // D52: most recent on top

				string[] ordered = new string[files.Length];
				for (int i = 0; i < files.Length; i++)
				{
					ordered[i] = files[i].FullName;
				}

				this.listedDatabases = ordered; // D51: LOAD <n> references this listing, in displayed order

				// D52: SELECT-style aligned table - FILENAME / MODIFIED headings,
				// data left-aligned under the headings (same renderer as DoSelect).
				string[][] grid = new string[files.Length + 1][];
				grid[0] = new string[] { "FILENAME", "MODIFIED" };
				for (int i = 0; i < files.Length; i++)
				{
					string modified = files[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
					grid[i + 1] = new string[] { (i + 1).ToString() + ". " + ordered[i], modified };
				}

				int[] widths = new int[2];
				for (int r = 0; r < grid.Length; r++)
				{
					if (grid[r][0].Length > widths[0])
					{
						widths[0] = grid[r][0].Length;
					}

					if (grid[r][1].Length > widths[1])
					{
						widths[1] = grid[r][1].Length;
					}
				}

				for (int r = 0; r < grid.Length; r++)
				{
					Console.WriteLine(RenderAlignedRow(grid[r], widths));
				}

				return;
			}

			if (sub == "DATABASE")
			{
				if (tokens.Length != 2)
				{
					Console.WriteLine("Command Error: SHOW DATABASE takes no argument.");
					return;
				}

				if (!this.databaseLoaded)
				{
					Console.WriteLine("Command Error: No database loaded. LOAD '<file>.DAT' first.");
					return;
				}

				int used;
				int total;
				try
				{
					used = this.engine.UsedSlots();
					total = this.engine.TotalSlots();
				}
				catch (Exception e)
				{
					Console.WriteLine("Database Exception: " + e.Message);
					return;
				}

				// Spec card: the number column aligns at index 16 in all three
			// lines ("Records in use: " is already 16 chars; pad "Free slots:").
				Console.WriteLine("Database: " + Path.GetFileName(this.dbAsGiven));
				Console.WriteLine("Records in use: " + used.ToString());
				string freeLabel = "Free slots:";
				while (freeLabel.Length < 16)
				{
					freeLabel += " ";
				}
				Console.WriteLine(freeLabel + (total - used).ToString());

				if (this.stagedChanges > 0)
				{
					Console.WriteLine("Staged changes: " + this.stagedChanges.ToString() + " (not yet committed - run COMMIT to persist)");
				}
				else
				{
					Console.WriteLine("Staged changes: 0 (none pending)");
				}
				return;
			}

			Console.WriteLine("Command Error: SHOW takes CONFIG, DEPARTMENTS, DATABASES, or DATABASE.");
		}

		/* ================= Phase 3 read surface: HELP / SELECT / COUNT ================= */

		/// <summary>
		/// HELP - mode-aware command list. Project convention (the spec has
		/// no HELP section yet); one line per command with a short blurb.
		/// </summary>
		private void DoHelp()
		{
			if (this.databaseLoaded)
			{
				Console.WriteLine("Commands in database mode:");
				// Descriptions align on one column: syntax field padded to the
				// longest entry (SELECT), then " - blurb".
				Console.WriteLine("  SELECT <code|range|*> [WHERE ...] [VALUES (...)] - show records");
				Console.WriteLine("  COUNT <code|range|*> [WHERE ...]                 - count matching records");
				Console.WriteLine("  SHOW DATABASE                                    - database status card");
				Console.WriteLine("  UNLOAD [FORCE]                                   - release the database");
				Console.WriteLine("  HELP                                             - this help");
				Console.WriteLine("  BYE / QUIT                                       - exit gsa-cli");
			}
			else
			{
				Console.WriteLine("Commands in config mode:");
				// Same alignment rule as database mode above.
				Console.WriteLine("  CONFIG <KEY>=<VALUE> [...] - change settings (config mode only)");
				Console.WriteLine("  SHOW CONFIG                - show current settings");
				Console.WriteLine("  SHOW DEPARTMENTS           - list departments from Settings.xml");
				Console.WriteLine("  SHOW DATABASES [dir]       - list .DAT databases in dir (default: working dir)");
				Console.WriteLine("  LOAD <n|'<file>.DAT'>      - load by listing number or file (OVERRIDE for stale locks)");
				Console.WriteLine("  UNLOAD [FORCE]             - release the loaded database");
				Console.WriteLine("  HELP                       - this help");
				Console.WriteLine("  BYE / QUIT                 - exit gsa-cli");
			}
		}

		/// <summary>
		/// SELECT (spec "SELECT"): target is * , one code, or a lo-hi range;
		/// optional WHERE (D9 boolean predicates) and VALUES projection.
		/// Aligned fixed-width table, all columns by default, ITEM_NAME in full,
		/// prices raw cents, DEPT as number + Settings.xml name, SALEPRICE2
		/// added for PRICE_METHOD 2/3 records (D36). No match prints the
		/// bare "No matching records." line and is not an error (D17).
		/// </summary>
		private void DoSelect(string[] tokens)
		{
			Selection sel;
			try
			{
				sel = ParseSelection(tokens, "SELECT");
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return;
			}

			RecordData[] rows;
			try
			{
				rows = SelectRows(sel);
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
				return;
			}

			if (rows == null) // ^C during the scan
			{
				Console.WriteLine("Aborted.");
				return;
			}

			if (rows.Length == 0)
			{
				Console.WriteLine("No matching records.");
				return;
			}

			string[] columns = ResolveColumns(rows, sel.Columns);

			// Aligned table (not raw tabs): a tab stop is relative to where
			// the previous cell ended, so with mixed-length cells each row's
			// columns would start at different positions. Instead every column
			// gets one fixed width - the widest entry across header and data
			// rows - so the first character of a value sits under the first
			// character of its heading in every row.
			//
			// Two passes (D57): size the columns with CellLength (mirrors
			// RenderCell without allocating the cell strings), then stream the
			// rows one at a time. The old single grid held all 600k x N cells
			// live at once (~200 MB on p1-600k) and .NET 2.0 never decommits
			// it, so every select/^C cycle ratcheted the working set; with
			// streaming only one row of cells exists in memory.
			int[] widths = new int[columns.Length];
			for (int c = 0; c < columns.Length; c++)
			{
				widths[c] = columns[c].Length; // header sets the floor, as before
			}

			for (int i = 0; i < rows.Length; i++)
			{
				if (Abort.IsRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}

				for (int c = 0; c < columns.Length; c++)
				{
					int len = CellLength(rows[i], columns[c]);
					if (len > widths[c])
					{
						widths[c] = len;
					}
				}
			}

			// Header row: the column names are already strings - render direct.
			Console.WriteLine(RenderAlignedRow(columns, widths));

			for (int i = 0; i < rows.Length; i++)
			{
				if (Abort.IsRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}

				string[] cells = new string[columns.Length];
				for (int c = 0; c < columns.Length; c++)
				{
					cells[c] = RenderCell(rows[i], columns[c]);
				}

				Console.WriteLine(RenderAlignedRow(cells, widths));
			}
		}

		/// <summary>
		/// COUNT (spec "COUNT"): same target/WHERE machinery as SELECT,
		/// prints the bare integer count; zero matches prints 0 and is not
		/// an error (D17, D21 update).
		/// </summary>
		private void DoCount(string[] tokens)
		{
			Selection sel;
			try
			{
				sel = ParseSelection(tokens, "COUNT");
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return;
			}

			if (sel.Columns != null)
			{
				Console.WriteLine("Command Error: COUNT takes no VALUES clause.");
				return;
			}

			try
			{
				RecordData[] rows = SelectRows(sel);
				if (rows == null) // ^C during the scan
				{
					Console.WriteLine("Aborted.");
					return;
				}
				Console.WriteLine(rows.Length.ToString());
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

	/* ---------- Phase 4 mutating commands (D1/D2/D3/D15-D19) ----------
	 *
	 * All mutations stage in RAM via the engine's session mode and are visible
	 * in-session; COMMIT makes them durable, ROLLBACK discards them (D1).
	 * Multi-record operations validate every row before applying any of them,
	 * and undo applied rows if a later one fails (D3 all-or-nothing).
	 */

		/// <summary>
		/// INSERT (spec order: ITEM_CODE, ITEM_NAME, DEPT, SALEPRICECENTS first;
		/// the remaining 16 are optional and must be given as a full set - D4,
		/// four-or-all rule). New code only (D2); existing codes get UPDATE.
		/// rnd4/upc-a generate a free code (D32).
		/// </summary>
		/* ---------- EXPORT (D20) / IMPORT (D41/D44) CSV surface ---------- */

		/// <summary>
		/// The 19-column reference schema in sample.csv order (D39). EXPORT
		/// writes exactly these names; IMPORT maps them onto the INSERT field
		/// set (InsertFields already uses these spellings).
		/// </summary>
		private static readonly string[] CsvColumns = new string[] {
			"ITEM_CODE", "ITEM_NAME", "DEPT", "SALEPRICECENTS", "SALEPRICE2",
			"QTY", "TRACK_MOVEMENT", "REQUIRE_QTY", "REQUIRE_PRICE", "SELL_HOURS",
			"SELL_RESTRICTED", "NO_RETURN", "GIVEAWAY", "TAX1", "TAX2", "TAX3",
			"TAX4", "PRICE_METHOD", "DCS_METHOD"
		};

		/// <summary>
		/// One record as 19 CSV values in CsvColumns order - the exact inverse
		/// of IMPORT's ApplyField mapping (D39 round-trip identity): raw cents,
		/// Y/N flags, SELL_HOURS from the inverted-bit AllHours field (D5/D46).
		/// </summary>
		private static string[] RecordToCsvValues(RecordData r)
		{
			return new string[] {
				r.Code,
				r.ItemName,
				r.Dept,
				r.SalePriceCents.ToString(),
				r.SalePrice2Cents.ToString(),
				r.Qty.ToString(),
				FlagStr(r.Mvmt),
				FlagStr(r.ReqQty),
				FlagStr(r.ReqPrice),
				FlagStr(r.AllHours),
				FlagStr(r.RestrictedHours),
				FlagStr(r.NoReturn),
				FlagStr(r.Giveaway),
				FlagStr(r.Tax1),
				FlagStr(r.Tax2),
				FlagStr(r.Tax3),
				FlagStr(r.Tax4),
				r.PriceMethod.ToString(),
				r.DcsMethod.ToString()
			};
		}

		/// <summary>
		/// EXPORT (D20): write the selected records to a CSV file in the 19-
		/// column reference schema. OVERWRITE is required to replace an existing
		/// file; rows stream out one at a time through the Phase 5a bridge -
		/// no whole-file materialization on this side (the record list itself is
		/// the engine's cached array, D56). A failed or aborted run leaves no
		/// partial file behind.
		/// </summary>
		private void DoExport(string[] tokens)
		{
			string path = null;
			bool overwrite = false;
			Selection sel = new Selection();

			try
			{
				if (tokens.Length < 2 || tokens[1].Length == 0)
				{
					throw new CommandSyntaxException("Command Error: EXPORT takes a target (*, code, or range), optionally WHERE and OVERWRITE, then TO '<file>.csv'.");
				}

				ApplyTarget(sel, tokens[1]);

				int i = 2;
				while (i < tokens.Length)
				{
					string t = tokens[i].ToUpperInvariant();

					if (t == "WHERE")
					{
						i++;
						int end = i;
						while (end < tokens.Length && !IsExportClauseKeyword(tokens[end]))
						{
							end++;
						}

						sel.Where = ParseWhere(tokens, i, end);
						i = end;
					}
					else if (t == "OVERWRITE")
					{
						overwrite = true;
						i++;
					}
					else if (t == "TO")
					{
						if (i + 1 >= tokens.Length)
						{
							throw new CommandSyntaxException("Command Error: TO takes a file path.");
						}

						path = UnquoteValue(tokens[i + 1]);
						i += 2;
					}
					else
					{
						throw new CommandSyntaxException("Command Error: unexpected token '" + tokens[i] + "' in EXPORT.");
					}
				}

				if (path == null)
				{
					throw new CommandSyntaxException("Command Error: EXPORT needs TO '<file>.csv'.");
				}
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return;
			}

			if (File.Exists(path) && !overwrite)
			{
				Console.WriteLine("Command Error: " + path + " already exists. Re-run with OVERWRITE to replace it.");
				return;
			}

			RecordData[] rows;
			try
			{
				rows = SelectRows(sel);
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
				return;
			}

			if (rows == null) // ^C during the scan
			{
				Console.WriteLine("Aborted.");
				return;
			}

			ICsvWriter writer = null;
			bool done = false;
			int lastProgress = -1; // progress indicator, same rhythm as IMPORT

			try
			{
				writer = this.csv.OpenWriter(path);
				writer.WriteHeader(CsvColumns);

				for (int r = 0; r < rows.Length && !Abort.IsRequested; r++)
				{
					writer.WriteRow(RecordToCsvValues(rows[r]));

					if ((r + 1) - lastProgress >= 50000)
					{
						lastProgress = r + 1;
						Console.WriteLine("Progress: " + (r + 1).ToString() + " rows written");
					}
				}

				if (Abort.IsRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}

				writer.Dispose(); // Close: flushes and surfaces any pipeline fault
				done = true;
			}
			catch (Exception e)
			{
				Console.WriteLine("Command Error: " + e.Message);
				return;
			}
			finally
			{
				// A failed or aborted run must not leave a truncated file behind.
				if (!done && File.Exists(path))
				{
					try { File.Delete(path); } catch (Exception) { }
				}
			}

			Console.WriteLine("Progress: 100%");
			Console.WriteLine("OK, exported " + rows.Length + " records to '" + path + "'");
		}

		private static bool IsExportClauseKeyword(string token)
		{
			string t = token.ToUpperInvariant();
			return t == "TO" || t == "OVERWRITE";
		}

		/* ---------- IMPORT (D41/D44) ---------- */

		/// <summary>
		/// IMPORT (D41/D44): upsert CSV rows by ITEM_CODE - a new code is an
		/// INSERT, an existing one becomes a lossless UPDATE rewrite on top of
		/// the stored record (hidden fields survive). Rows stream in one at a
		/// time through the Phase 5a bridge; rejected rows are reported with
		/// their ITEM_CODE and never silently dropped. The run ends in COMMIT
		/// for valid rows; ^C or a mid-file failure rolls back everything this
		/// command staged.
		/// </summary>
		/// <summary>
		/// PLAY '<script>' [LOUD] (spec "PLAY", D16/D41). Pre-parse pass: every
		/// line must lex cleanly, name a known command, and not end the session
		/// (BYE/QUIT are refused up front - a script cannot quit its host).
		/// B6: CONFIG and UNLOAD are also refused up front - they are
		/// session-level commands that either silently no-op in database mode
		/// (CONFIG) or break the run by unloading it (UNLOAD), so a script
		/// containing them fails before any line executes.
		/// The last non-blank line must be COMMIT. Execution is all-or-nothing:
		/// the first failed line stops playback, rolls back every staged change
		/// from this script, and reports line N with the failure message.
		/// Silent mode prints only the final OK; LOUD echoes each command and
		/// its response as if typed. A mid-script ROLLBACK is legal (D41) - it
		/// discards staged changes up to that point and playback continues.
		/// </summary>
		private void DoPlay(string[] tokens)
		{
			// Grammar: PLAY '<script>' [LOUD]  (tokens[0] is "PLAY")
			if (tokens.Length < 2 || tokens[1].Length == 0)
			{
				Console.WriteLine("Command Error: PLAY takes a script file path, optionally LOUD.");
				return;
			}

			bool loud = false;
			if (tokens.Length > 3)
			{
				Console.WriteLine("Command Error: PLAY takes a script file path, optionally LOUD.");
				return;
			}

			if (tokens.Length == 3)
			{
				if (!tokens[2].Equals("LOUD", StringComparison.OrdinalIgnoreCase))
				{
					Console.WriteLine("Command Error: PLAY takes a script file path, optionally LOUD.");
					return;
				}
				loud = true;
			}

			PlayScript(tokens[1], loud);
		}

		/// <summary>Command verbs a PLAY script may contain (mirrors Execute's switch).</summary>
		private static readonly string[] PlayVerbs = new string[] {
			"BYE", "COMMIT", "CONFIG", "COUNT", "CREATE", "DROP", "EXPORT",
			"EXTEND", "HELP", "IMPORT", "INSERT", "LOAD", "PLAY", "QUIT",
			"RECODE", "ROLLBACK", "SELECT", "SHOW", "UNLOAD", "UPDATE"
		};

		private static bool PlayIsKnownVerb(string v)
		{
			for (int i = 0; i < PlayVerbs.Length; i++)
			{
				if (PlayVerbs[i] == v) return true;
			}
			return false;
		}

		/// <summary>
		/// A line failed if its output carries one of gsa-cli's error markers.
		/// Warnings (D42 drops, name truncation) and clean no-match responses do
		/// not count as failures - they are legitimate command outcomes.
		/// </summary>
		private static bool PlayLineFailed(string outText)
		{
			return outText.IndexOf("Command Error:") >= 0
				|| outText.IndexOf("Validation Error:") >= 0
				|| outText.IndexOf("Database Exception:") >= 0;
		}

		private static string PlayFailureMessage(string outText)
		{
			string[] ls = outText.Split('\n');
			for (int i = 0; i < ls.Length; i++)
			{
				string t = ls[i].Trim();
				if (t.StartsWith("Command Error:") || t.StartsWith("Validation Error:") || t.StartsWith("Database Exception:"))
				{
					return t;
				}
			}
			return "command failed";
		}

		/// <summary>
		/// Shared PLAY core (interactive PLAY and one-shot -play, D16/D41).
		/// Returns true only if every line completed without an error marker.
		/// </summary>
		public bool PlayScript(string path, bool loud)
		{
			string[] raw;
			try
			{
				raw = File.ReadAllLines(path);
			}
			catch (Exception e)
			{
				Console.WriteLine("Command Error: could not open '" + path + "' (" + e.Message + ").");
				return false;
			}

			int lastNonBlank = -1;
			for (int i = 0; i < raw.Length; i++)
			{
				if (raw[i].Trim().Length > 0) lastNonBlank = i;
			}

			// Pre-parse pass: lex + known-verb + no session-ending commands.
			for (int i = 0; i < raw.Length; i++)
			{
				string t = raw[i].Trim();
				if (t.Length == 0) continue;

				string[] tk;
				try
				{
					tk = CommandLex.Lex(t);
				}
				catch (CommandSyntaxException e)
				{
					Console.WriteLine("Script Error: '" + path + "' line " + (i + 1) + ": " + e.Message);
					return false;
				}

				string verb = tk[0].ToUpperInvariant();
				if (!PlayIsKnownVerb(verb))
				{
					Console.WriteLine("Script Error: '" + path + "' line " + (i + 1) + ": unknown command '" + tk[0] + "'.");
					return false;
				}

				if (verb == "BYE" || verb == "QUIT")
				{
					Console.WriteLine("Script Error: '" + path + "' line " + (i + 1) + ": BYE/QUIT is not allowed in PLAY scripts.");
					return false;
				}

				if (verb == "CONFIG" || verb == "UNLOAD")
				{
					Console.WriteLine("Script Error: '" + path + "' line " + (i + 1) + ": " + verb + " is not allowed in PLAY scripts.");
					return false;
				}
			}

			// Terminal-COMMIT rule (D41).
			if (lastNonBlank < 0)
			{
				Console.WriteLine("Script Error: '" + path + "': the script is empty.");
				return false;
			}

			string finalLine = raw[lastNonBlank].Trim().ToUpperInvariant();
			if (!(finalLine == "COMMIT" || finalLine == "COMMIT;"))
			{
				Console.WriteLine("Script Error: '" + path + "' line " + (lastNonBlank + 1) + ": the last line must be COMMIT.");
				return false;
			}

			if (loud)
			{
				Console.WriteLine("OK, playing '" + path + "'...");
				Console.WriteLine();
			}

			TextWriter realOut = Console.Out;
			bool ok = true;
			int failedLineNo = 0;
			string failureMsg = "";

			for (int i = 0; i < raw.Length && ok; i++)
			{
				string line = raw[i].Trim();
				if (line.Length == 0) continue;

				// Capture this line's output so silent mode stays quiet and the
				// failure path can cite the exact message.
				StringWriter capture = new StringWriter();
				Console.SetOut(capture);
				bool cont;
				try
				{
					cont = Execute(line);
				}
				finally
				{
					Console.SetOut(realOut);
				}

				string outText = capture.ToString();

				if (loud)
				{
					realOut.WriteLine(line);
					if (outText.Length > 0) realOut.Write(outText);
					realOut.WriteLine();
				}

				if (!cont || PlayLineFailed(outText))
				{
					ok = false;
					failedLineNo = i + 1;
					failureMsg = cont ? PlayFailureMessage(outText) : "BYE/QUIT ended the session";
				}
			}

			if (!ok)
			{
				// D16: stop at the first failure and roll back every staged change
				// from this script - nothing partial survives.
				try
				{
					this.engine.Rollback();
					this.stagedChanges = 0;
				}
				catch (Exception e2)
				{
					realOut.WriteLine("Database Exception: " + e2.Message);
				}

				realOut.WriteLine("Script Error: '" + path + "' line " + failedLineNo + ": " + failureMsg);
				realOut.WriteLine("Staged changes rolled back.");
				return false;
			}

			if (loud)
			{
				Console.WriteLine();
				Console.WriteLine("Played '" + path + "' successfully");
			}
			else
			{
				Console.WriteLine("OK, played '" + path + "' successfully");
			}
			return true;
		}

		/// <summary>
		/// One-shot IMPORT entry (D41/D44): whole file, no slice/filter.
		/// Returns false on any hard failure so the caller can exit non-zero.
		/// </summary>
		public bool ImportWholeFile(string path)
		{
			return DoImport(new string[] { "IMPORT", path });
		}

		private bool DoImport(string[] tokens)
		{
			string path = null;
			long sliceStart = 0;
			long sliceCount = -1; // -1 = no ROWS limit
			bool hasSlice = false;
			WhereExpr filter = null;

			try
			{
				if (tokens.Length < 2 || tokens[1].Length == 0)
				{
					throw new CommandSyntaxException("Command Error: IMPORT takes '<file>.csv', optionally WHERE START='n' ROWS='m' and/or field filters.");
				}

				path = UnquoteValue(tokens[1]);

				int i = 2;
				if (i < tokens.Length && tokens[i].ToUpperInvariant() == "WHERE")
				{
					i++;

					if (i < tokens.Length && tokens[i].StartsWith("START=", StringComparison.OrdinalIgnoreCase))
					{
						sliceStart = ParseSliceNumber(tokens[i], 6, "START");
						
						if (i + 1 >= tokens.Length || !tokens[i + 1].StartsWith("ROWS=", StringComparison.OrdinalIgnoreCase))
						{
							throw new CommandSyntaxException("Command Error: START='n' must be followed by ROWS='m'.");
						}

						sliceCount = ParseSliceNumber(tokens[i + 1], 5, "ROWS");
						i += 2;
						hasSlice = true;

						if (i < tokens.Length && tokens[i].ToUpperInvariant() == "AND")
						{
							i++;
						}

						if (i < tokens.Length)
						{
							filter = ParseWhere(tokens, i, tokens.Length, true);
						}

					}
					else if (i < tokens.Length && tokens[i].StartsWith("ROWS=", StringComparison.OrdinalIgnoreCase))
					{
						throw new CommandSyntaxException("Command Error: ROWS='m' must be paired with START='n'.");
					}

					if (!hasSlice && i < tokens.Length)
					{
						filter = ParseWhere(tokens, i, tokens.Length, true);
					}
					else if (!hasSlice && i >= tokens.Length)
					{
						throw new CommandSyntaxException("Command Error: WHERE takes at least one predicate.");
					}
				}

				if (filter == null && !hasSlice && i >= tokens.Length)
				{
					// plain IMPORT '<file>' - whole file, no slice/filter
				}
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return false;
			}

			if (!File.Exists(path))
			{
				Console.WriteLine("Command Error: could not open '" + path + "' (file not found).");
				return false;
			}

			ICsvReader reader = null;
			try
			{
				reader = this.csv.OpenReader(path);
			}
			catch (Exception e)
			{
				Console.WriteLine("Command Error: " + e.Message);
				return false;
			}

			int[] colIdx = new int[CsvColumns.Length];
			for (int c = 0; c < CsvColumns.Length; c++)
			{
				colIdx[c] = reader.ColumnIndex(CsvColumns[c]);
			}

			// Only ITEM_CODE must be present as a column - rejected rows are
			// reported by code (D41). The other required fields are enforced per
			// row for NEW records (INSERT rules, spec IMPORT); updates keep the
			// stored value when the column or cell is absent.
			if (colIdx[ColumnIndexOf("ITEM_CODE")] < 0)
			{
				reader.Dispose();
				Console.WriteLine("Command Error: '" + path + "' is missing required column 'ITEM_CODE'.");
				return false;
			}

			// D60: one snapshot of the numeric codes up front. Existence checks use
			// the O(1) hash read (ReadRecord), and only the leading-zero-spelling
			// fallback consults this map - never a per-row full-database scan.
			Dictionary<long, string> numericCodes = new Dictionary<long, string>();
			try
			{
				RecordData[] existing = this.engine.ListRecords();
				for (int e2 = 0; e2 < existing.Length; e2++)
				{
					string code = existing[e2].Code;
					if (!IsDigits(code))
					{
						continue;
					}

					long v;
					try { v = long.Parse(code); } catch (Exception) { continue; }
					if (!numericCodes.ContainsKey(v))
					{
						numericCodes.Add(v, code);
					}
				}
			}
			catch (Exception e3)
			{
				reader.Dispose();
				Console.WriteLine("Database Exception: " + e3.Message);
				return false;
			}

			long dataRow = -1;
			int imported = 0;
			int updated = 0;
			List<string> rejected = new List<string>();
			int d42Drops = 0;
			bool aborted = false;
			bool failed = false;
			long lastProgress = -1;

			this.nameTruncations = 0;
			this.quietWarnings = true;

			try
			{
				CsvRow row;
				while (reader.TryReadRow(out row))
				{
					dataRow++;

					if (hasSlice && (dataRow < sliceStart || dataRow - sliceStart >= sliceCount))
			{
						continue; // outside the START/ROWS window
				}

					if (filter != null && !EvaluateWhereCsv(filter, colIdx, row))
				{
						continue;
				}

					if (Abort.IsRequested)
				{
						aborted = true;
						break;
					}

					bool bad = false;
					string reason = null;
					string codeRaw = "";

					try
					{
						codeRaw = CsvCell(row, colIdx[ColumnIndexOf("ITEM_CODE")]);
						if (codeRaw.Length == 0)
						{
							throw new CommandSyntaxException("no ITEM_CODE in row");
						}

						// Existence: O(1) hash read; the engine is write-through, so
						// earlier rows of this same import are already visible.
						RecordData stored = null;
						stored = this.engine.ReadRecord(codeRaw);

						if (stored == null && IsDigits(codeRaw))
						{
							// Leading-zero spelling of an existing numeric code is the same item.
							long v;
							try { v = long.Parse(codeRaw); } catch (Exception) { v = 0; }

							string canonical = null;
							if (v != 0 && numericCodes.TryGetValue(v, out canonical) && canonical != codeRaw)
							{
								stored = this.engine.ReadRecord(canonical);
							}
						}

						bool isNew = stored == null;

						// Required fields: a NEW record must carry them; an update keeps
						// the stored value when the cell is empty (UPDATE semantics).
						if (isNew)
						{
							if (CsvCell(row, colIdx[ColumnIndexOf("ITEM_NAME")]).Length == 0)
							{
								throw new CommandSyntaxException("no ITEM_NAME in row");
							}

							if (CsvCell(row, colIdx[ColumnIndexOf("DEPT")]).Length == 0)
							{
								throw new CommandSyntaxException("no DEPT in row");
							}

							if (CsvCell(row, colIdx[ColumnIndexOf("SALEPRICECENTS")]).Length == 0)
							{
								throw new CommandSyntaxException("no SALEPRICECENTS in row");
							}
						}

						RecordData rec = isNew ? NewRecordFromSettings() : CopyRecord(stored);

						bool saleprice2Present = false;
						for (int c = 0; c < CsvColumns.Length && !bad; c++)
						{
							string name = CsvColumns[c];
							if (name == "ITEM_CODE" || colIdx[c] < 0)
							{
								continue;
							}

							string v2 = CsvCell(row, colIdx[c]);
							if (v2.Length == 0)
							{
								continue; // empty cell: settings default (new) or stored value (update)
							}

							try
							{
								ApplyField(rec, name, v2);
							}
							catch (CommandSyntaxException e4)
							{
								bad = true;
								reason = StripValidationPrefix(e4.Message);
								break;
							}

							if (name == "SALEPRICE2")
							{
								saleprice2Present = true;
							}
						}

						if (!bad)
						{
							try
							{
								ValidateInsertCode(codeRaw);
							}
							catch (CommandSyntaxException e5)
							{
								bad = true;
								reason = StripValidationPrefix(e5.Message);
							}

							if (!bad && isNew)
							{
								// B9: a zero code is invalid - substitute a generated free
								// code per CODE_METHOD (updates keep the stored spelling, D46).
								rec.Code = IsZeroCode(codeRaw) ? GenerateByCodeMethod() : codeRaw;
							}
						}

						// D42/B2: SALEPRICE2 is only valid with Pricing Method 2 or 3.
						// A zero value under method 0/1 carries no information (it is
						// the default), so it is dropped silently - the warning counts
						// only supplied non-zero values (B2).
						if (!bad && saleprice2Present && rec.SalePrice2Cents > 0 && (rec.PriceMethod == 0 || rec.PriceMethod == 1))
						{
							d42Drops++;
							rec.SalePrice2Cents = 0;
						}

						if (!bad)
						{
							this.engine.UpsertRecord(rec);
							if (isNew) { imported++; } else { updated++; }
						}
					}
					catch (CommandSyntaxException e6)
					{
						bad = true;
						reason = StripValidationPrefix(e6.Message);
					}

					if (bad)
					{
						rejected.Add((codeRaw.Length > 0 ? codeRaw : "(no code)") + " - " + reason);
						continue;
					}

					if (dataRow - lastProgress >= 50000)
					{
						lastProgress = dataRow;
						Console.WriteLine("Progress: " + dataRow.ToString() + " rows read");
					}
				}
			}
			catch (Exception e7)
			{
				// Mid-file CSV fault or engine failure: nothing from this run survives.
				failed = true;
				Console.WriteLine("Command Error: " + e7.Message);
			}
			finally
			{
				this.quietWarnings = false;
				try { reader.Dispose(); } catch (Exception) { }
			}

			if (aborted || failed)
			{
				try { this.engine.Rollback(); } catch (Exception) { }
				Console.WriteLine("Aborted.");
				Console.WriteLine("Staged import changes rolled back.");
				return false;
			}

			if (imported + updated > 0)
			{
				try
				{
					this.engine.Commit(); // D41: the run ends in COMMIT for valid rows
				}
				catch (Exception e8)
				{
					Console.WriteLine("Database Exception: " + e8.Message);
					return false;
				}

				// D41: the run ends in COMMIT, so nothing is left staged -
				// stagedChanges stays untouched (no phantom pending state for BYE).
			}

			Console.WriteLine("Progress: 100%");

			string summary;
			if (hasSlice)
			{
				// Spec IMPORT success example - row slicing wording.
				summary = "OK, imported the first " + sliceCount.ToString() + " rows starting at position " + sliceStart.ToString();
			}
			else
			{
				summary = "OK, imported " + imported.ToString() + " records";
				if (updated > 0)
				{
					summary += ", updated " + updated.ToString() + " records";
				}
			}

			if (rejected.Count > 0)
			{
				Console.WriteLine(summary + ", rejected the following ITEM_CODEs:");
				for (int r2 = 0; r2 < rejected.Count; r2++)
				{
					Console.WriteLine("- " + rejected[r2]);
				}
			}
			else
			{
				Console.WriteLine(summary);
			}

			if (d42Drops > 0)
			{
				Console.WriteLine("Warning: " + d42Drops.ToString() + " rows had SALEPRICE2 dropped (Pricing Method 0 or 1).");
			}

			if (this.nameTruncations > 0)
			{
				Console.WriteLine("Warning: " + this.nameTruncations.ToString() + " ITEM_NAMEs truncated to 18 characters.");
			}

			if (imported + updated > 0)
			{
				Console.WriteLine("OK, changes committed to disk!");
			}

			return true;
		}

		private static int ColumnIndexOf(string name)
		{
			for (int i = 0; i < CsvColumns.Length; i++)
			{
				if (CsvColumns[i] == name)
				{
					return i;
				}
			}

			return -1;
		}

		private static string CsvCell(CsvRow row, int idx)
		{
			if (row == null || idx < 0 || idx >= row.Columns.Length)
			{
				return "";
			}

			string v = row.Columns[idx];
			return v != null ? v : "";
		}

		private static long ParseSliceNumber(string token, int prefixLen, string what)
		{
			string v = UnquoteValue(token.Substring(prefixLen));
			if (!IsDigits(v))
			{
				throw new CommandSyntaxException("Command Error: " + what + " takes a non-negative integer row number.");
			}

			try
			{
				return long.Parse(v);
			}
			catch (Exception)
			{
				throw new CommandSyntaxException("Command Error: " + what + " value is out of range.");
			}
		}

		private static string StripValidationPrefix(string msg)
		{
			const string p = "Validation Error: ";
			if (msg.StartsWith(p))
			{
				return msg.Substring(p.Length);
			}

			return msg;
		}

		/// <summary>
		/// Evaluate an IMPORT WHERE filter against one CSV row. Field names were
		/// validated as CSV-schema names at parse time (csvFields mode).
		/// </summary>
		private static bool EvaluateWhereCsv(WhereExpr e, int[] colIdx, CsvRow row)
		{
			if (!e.IsLeaf)
			{
				bool l = EvaluateWhereCsv(e.Left, colIdx, row);
				bool r = EvaluateWhereCsv(e.Right, colIdx, row);
				return e.Op == "AND" ? (l && r) : (l || r);
			}

			int c = ColumnIndexOf(e.Field);
			string value = (c >= 0) ? CsvCell(row, colIdx[c]) : "";
			return ValueMatches(value, e.Value);
		}

		private void DoInsert(string[] tokens, string line, int[] starts)
		{
			Dictionary<string, string> fields;
			try
			{
				fields = ParseInsertFields(tokens, line, starts);
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
				return;
			}

			string[] required = new string[] { "ITEM_CODE", "ITEM_NAME", "DEPT", "SALEPRICECENTS" };
			for (int i = 0; i < required.Length; i++)
			{
				if (!fields.ContainsKey(required[i]))
				{
					Console.WriteLine("Validation Error: You are missing a required parameter.");
					return;
				}
			}

			// Defaults from Settings.xml (D4), then the operator's values applied in
			// documented field order so validation errors surface deterministically.
			RecordData rec = NewRecordFromSettings();

			try
			{
				for (int i = 0; i < InsertFields.Length; i++)
				{
					string f = InsertFields[i];
					if (!fields.ContainsKey(f) || f == "ITEM_CODE")
					{
						continue;
					}
					ApplyField(rec, f, fields[f]);
				}

				string code = fields["ITEM_CODE"];
				string lower = code.ToLowerInvariant();
				// B9: code 0 is invalid - the engine silently drops records stored
				// under it, so substitute a generated free code per CODE_METHOD.
				bool zeroCode = IsZeroCode(code);
				bool generated = (lower == "rnd4" || lower == "upc-a") || zeroCode;

				if (!generated)
				{
					ValidateInsertCode(code);
					if (CodeExists(code))
					{
						Console.WriteLine("Validation Error: ITEM_CODE " + code + " already exists. Use UPDATE " + code + " SET ... to modify it, or choose a different code.");
						return;
				}
				}

				// D42/D36/B2: SALEPRICE2 with Pricing Method 0/1 warns and drops, but
				// a zero value is just the default - dropped silently (B2). Methods
				// 2/3 without SALEPRICE2 are legal (the record keeps its default).
				if ((rec.PriceMethod == 0 || rec.PriceMethod == 1) && fields.ContainsKey("SALEPRICE2") && rec.SalePrice2Cents > 0)
				{
					Console.WriteLine("Warning: SALEPRICE2 is only valid with Pricing Method 2 or 3 - value dropped.");
					rec.SalePrice2Cents = 0;
				}

				if (zeroCode)
				{
					rec.Code = GenerateByCodeMethod(); // B9: code 0 is invalid
				}
				else if (generated)
				{
					rec.Code = (lower == "rnd4") ? GenerateRnd4() : GenerateUpcA();
				}
				else
				{
					rec.Code = code;
				}

				this.engine.UpsertRecord(rec);
				this.stagedChanges++;
				Console.WriteLine("OK, inserted item " + rec.Code);
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

		/// <summary>
		/// UPDATE target SET FIELD='VALUE' [,...] [WHERE ...]. Omitted fields keep
		/// their stored values (lossless rewrite - the engine carries MpgGroup,
		/// User and unsurfaced flag bits). Zero matches is not an error (D17).
		/// </summary>
		private void DoUpdate(string[] tokens, string line, int[] starts)
		{
			if (tokens.Length < 3)
			{
				Console.WriteLine("Command Error: UPDATE takes a target and SET FIELD='VALUE' [,...].");
				return;
			}

			Selection sel = new Selection();
			sel.Target = tokens[1];

			try
			{
				ValidateTarget(sel);

				// SET must be present; WHERE may precede it.
				int setIdx = -1;
				for (int i = 2; i < tokens.Length; i++)
				{
					if (tokens[i].ToUpperInvariant() == "SET")
					{
						setIdx = i;
						break;
					}
				}

				if (setIdx < 0)
				{
					throw new CommandSyntaxException("Command Error: UPDATE takes SET FIELD='VALUE' [,...].");
				}

				if (setIdx > 2)
				{
					if (tokens[2].ToUpperInvariant() != "WHERE")
					{
						throw new CommandSyntaxException("Command Error: expected WHERE or SET after the target.");
					}
					sel.Where = ParseWhere(tokens, 3, setIdx);
			}

				// SET assignments come from the raw line (quote-aware): values may
				// contain spaces that the whitespace tokenizer cannot see.
				int setEnd = ClauseEnd(line);
				string setText = line.Substring(starts[setIdx] + 3, Math.Max(0, setEnd - starts[setIdx] - 3));
				List<string> entries = CommandLex.ScanClause(setText);
				if (entries.Count == 0)
				{
					throw new CommandSyntaxException("Command Error: UPDATE takes at least one SET assignment.");
				}

				Dictionary<string, string> set_ = new Dictionary<string, string>();
				for (int i = 0; i < entries.Count; i++)
				{
					string e = entries[i];
					int eq = e.IndexOf('=');
					if (eq <= 0)
					{
						throw new CommandSyntaxException("Validation Error: SET takes FIELD='VALUE' assignments.");
					}

					string f = e.Substring(0, eq).Trim().ToUpperInvariant();
					if (f == "ITEM_CODE")
					{
						throw new CommandSyntaxException("Command Error: use RECODE to change an item code.");
					}

					bool known = false;
					for (int k = 0; k < InsertFields.Length; k++)
					{
						if (InsertFields[k] == f)
						{
							known = true;
							break;
						}
					}
					if (!known)
					{
						throw new CommandSyntaxException("Validation Error: Unknown field " + e.Substring(0, eq).Trim());
					}

					set_[f] = UnquoteValue(e.Substring(eq + 1).Trim());
				}

				// Select the affected rows (D56 cached list; ^C-aware).
				RecordData[] rows;
				try
				{
					rows = SelectRows(sel);
				}
				catch (Exception ex)
				{
					Console.WriteLine("Database Exception: " + ex.Message);
					return;
				}

				if (rows == null)
				{
					Console.WriteLine("Aborted.");
					return;
				}

				// D17: zero matches is not an error.
				if (rows.Length == 0)
				{
					Console.WriteLine("OK, 0 items updated.");
					return;
				}

				// D42/B2: SALEPRICE2 is dropped on rows whose effective pricing method
				// is 0/1 - warn once if any affected row will drop it. B2: only a
				// supplied non-zero value counts for the warning; a zero under method
				// 0/1 is just the default and is dropped silently.
				bool sp2Given = set_.ContainsKey("SALEPRICE2");
				bool sp2NonZero = false;
				if (sp2Given && IsDigits(set_["SALEPRICE2"]))
				{
					try { sp2NonZero = long.Parse(set_["SALEPRICE2"]) > 0; } catch (Exception) { }
				}
				int forcedPm = -1;
				if (set_.ContainsKey("PRICE_METHOD"))
				{
					string pmRaw = set_["PRICE_METHOD"];
					if (IsDigits(pmRaw) && pmRaw.Length == 1)
					{
						forcedPm = int.Parse(pmRaw);
					}
				}

				// D3: validate + build every new row BEFORE applying any of them,
				// reporting the failing item. Per-row SALEPRICE2 drop (D42) is decided
				// against each row's effective pricing method.
				List<RecordData> originals = new List<RecordData>();
				List<RecordData> updated = new List<RecordData>();
				bool anyDrop = false;

				for (int i = 0; i < rows.Length; i++)
				{
					RecordData orig = CopyRecord(rows[i]);
					int effPm = (forcedPm >= 0) ? forcedPm : orig.PriceMethod;
					bool dropSp2 = sp2Given && (effPm == 0 || effPm == 1);
					if (dropSp2 && sp2NonZero)
					{
						anyDrop = true;
					}

					RecordData nr;
					try
					{
						nr = ApplySet(orig, set_, dropSp2);
					}
					catch (CommandSyntaxException e)
					{
						throw new CommandSyntaxException(e.Message + " (item " + orig.Code + ")");
					}

					originals.Add(orig);
					updated.Add(nr);
				}

				if (anyDrop)
				{
					Console.WriteLine("Warning: SALEPRICE2 is only valid with Pricing Method 2 or 3 - value dropped.");
				}

				// Apply; undo applied rows if a later one fails (D3).
				int applied = 0;
				try
				{
					for (int i = 0; i < updated.Count; i++)
					{
						this.engine.UpsertRecord(updated[i]);
						applied++;
					}
				}
				catch (Exception ex)
				{
					for (int k = 0; k < applied; k++)
					{
						try
						{
							this.engine.UpsertRecord(originals[k]);
						}
						catch (Exception)
						{
						}
					}

					Console.WriteLine("Database Exception: " + ex.Message);
					return;
				}

				this.stagedChanges += updated.Count;

				if (!sel.IsRange && sel.Target != "*" && sel.Where == null)
				{
					Console.WriteLine("OK, updated item " + rows[0].Code);
				}
				else if (sel.IsRange)
				{
					string noun = updated.Count == 1 ? "record" : "records";
					Console.WriteLine("OK, updated " + updated.Count + " " + noun + " (range " + sel.RangeLo + "-" + sel.RangeHi + ")");
				}
				else
				{
					string noun = updated.Count == 1 ? "item" : "items";
					Console.WriteLine("OK, updated " + updated.Count + " " + noun);
				}
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
			}
		}

		/// <summary>
		/// DROP target [WHERE ...]. Bare DROP * is forbidden (D15); WHERE-scoped
		/// deletion of everything is the explicit way to empty a database. Zero
		/// matches is not an error (D17).
		/// </summary>
		private void DoDrop(string[] tokens)
		{
			if (tokens.Length < 2 || tokens[1].Length == 0)
			{
				Console.WriteLine("Command Error: DROP takes a target (*, code, or range), optionally WHERE.");
				return;
			}

			Selection sel = new Selection();
			sel.Target = tokens[1];

			try
			{
				ValidateTarget(sel);

				if (sel.Target == "*" && tokens.Length <= 2)
				{
					throw new CommandSyntaxException("Validation Error: DROP * is forbidden. If you want to start from scratch, use CREATE NEW <file> SLOTS n.");
				}

				if (tokens.Length > 2)
				{
					if (tokens[2].ToUpperInvariant() != "WHERE")
					{
						throw new CommandSyntaxException("Command Error: DROP takes a target, optionally WHERE.");
					}
					sel.Where = ParseWhere(tokens, 3, tokens.Length);
			}

				RecordData[] rows;
				try
				{
					rows = SelectRows(sel);
				}
				catch (Exception ex)
				{
					Console.WriteLine("Database Exception: " + ex.Message);
					return;
				}

				if (rows == null)
				{
					Console.WriteLine("Aborted.");
					return;
				}

				// D17: zero matches is not an error.
				if (rows.Length == 0)
				{
					Console.WriteLine("OK, deleted 0 records.");
					return;
				}

				// D3: undo applied deletions if a later one fails.
				List<RecordData> originals = new List<RecordData>();
				int applied = 0;

				try
				{
					for (int i = 0; i < rows.Length; i++)
					{
						originals.Add(CopyRecord(rows[i]));
						this.engine.DeleteRecord(rows[i].Code);
						applied++;
					}
				}
				catch (Exception ex)
				{
					for (int k = 0; k < applied; k++)
					{
						try
						{
							this.engine.UpsertRecord(originals[k]);
						}
						catch (Exception)
						{
						}
					}

					Console.WriteLine("Database Exception: " + ex.Message);
					return;
				}

				this.stagedChanges += rows.Length;

				if (rows.Length == 1)
				{
					Console.WriteLine("OK, deleted 1 record.");
				}
				else
				{
					// Spec examples: multi-record DROP reports without a trailing period.
					Console.WriteLine("OK, deleted " + rows.Length + " records");
				}
			}
			catch (CommandSyntaxException e)
			{
				Console.WriteLine(e.Message);
			}
		}

		/// <summary>
		/// RECODE old TO new (D19): single-code scope, explicit collision failure,
		/// staged like other mutations. Implemented as rewrite + delete of the old
		/// code; the undo path restores the original row.
		/// </summary>
		private void DoRecode(string[] tokens)
		{
			if (tokens.Length != 4 || tokens[2].ToUpperInvariant() != "TO")
			{
				Console.WriteLine("Command Error: RECODE takes <old-code> TO <new-code>.");
				return;
			}

			string oldCode = tokens[1];
			string newCode = tokens[3];

			try
			{
				// B9: code 0 is invalid - substitute a generated free code per CODE_METHOD.
				if (IsZeroCode(newCode))
				{
					newCode = GenerateByCodeMethod();
				}

				ValidateInsertCode(newCode);

				if (!CodeExists(oldCode))
				{
					Console.WriteLine("Validation Error: ITEM_CODE " + oldCode + " does not exist.");
					return;
				}

				if (CodeExists(newCode))
				{
					Console.WriteLine("Validation Error: target ITEM_CODE " + newCode + " already exists.");
					return;
				}

				RecordData row = this.engine.ReadRecord(oldCode);
			if (row == null)
			{
				// Leading-zero spelling of an existing numeric code: find the
				// canonical stored form.
				long v;
				try
				{
					v = long.Parse(oldCode);
				}
				catch (Exception)
				{
					Console.WriteLine("Validation Error: ITEM_CODE " + oldCode + " does not exist.");
					return;
				}

				RecordData[] rows = this.engine.ListRecords();
				for (int i = 0; i < rows.Length; i++)
				{
					if (IsDigits(rows[i].Code))
					{
						try
						{
							if (long.Parse(rows[i].Code) == v)
							{
								row = rows[i];
								oldCode = rows[i].Code;
								break;
							}
						}
						catch (Exception)
						{
						}
					}
				}

				if (row == null)
				{
					Console.WriteLine("Validation Error: ITEM_CODE " + tokens[1] + " does not exist.");
					return;
				}
			}

			RecordData orig = CopyRecord(row);
			RecordData nr = CopyRecord(row);
			nr.Code = newCode;

			try
			{
				this.engine.UpsertRecord(nr);
				this.engine.DeleteRecord(oldCode);
			}
			catch (Exception ex)
			{
				// D3: undo the rewrite if the delete failed.
				try
				{
					this.engine.UpsertRecord(orig);
				}
				catch (Exception)
				{
				}

				Console.WriteLine("Database Exception: " + ex.Message);
				return;
			}

			this.stagedChanges++;
			Console.WriteLine("OK, recoded " + oldCode + " to " + newCode);
		}
		catch (CommandSyntaxException e)
		{
			Console.WriteLine(e.Message);
		}
	}

		/// <summary>
		/// EXTEND n (D18): add capacity, report requested vs granted. Refused while
		/// staged changes are pending - the library re-reads from disk afterwards,
		/// which would silently discard them.
		/// </summary>
		private void DoExtend(string[] tokens)
		{
			if (tokens.Length != 2 || !IsDigits(tokens[1]))
			{
				Console.WriteLine("Command Error: EXTEND takes a positive slot count.");
				return;
			}

			int requested;
			try
			{
				requested = int.Parse(tokens[1]);
			}
			catch (Exception)
			{
				Console.WriteLine("Command Error: EXTEND takes a positive slot count.");
				return;
			}

			if (requested <= 0)
			{
				Console.WriteLine("Command Error: EXTEND takes a positive slot count.");
				return;
			}

			if (this.stagedChanges > 0)
			{
				Console.WriteLine("Command Error: EXTEND would discard " + this.stagedChanges + " staged change(s). COMMIT or ROLLBACK first.");
				return;
			}

			try
			{
				int before = this.engine.TotalSlots();
				int total = this.engine.ExtendDatabase(requested);
				Console.WriteLine("OK, extended database (requested " + requested + " slots, got " + (total - before) + "; new total: " + total + ")");
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

		/// <summary>COMMIT (D1): make all staged changes durable.</summary>
		private void DoCommit()
		{
			try
			{
				this.engine.Commit();
				this.stagedChanges = 0;
				Console.WriteLine("OK, changes committed to disk!");
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

		/// <summary>ROLLBACK (D1): discard staged changes, re-read the disk image.</summary>
		private void DoRollback()
		{
			try
			{
				this.engine.Rollback();
				this.stagedChanges = 0;
				Console.WriteLine("OK, flushed RAM and reloaded!");
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

		/// <summary>
		/// CREATE NEW file SLOTS n (D18): create a brand-new database. The
		/// layout floors to whole 10-slot data blocks, so the granted slot count is
		/// reported from the created file rather than echoed back. A slot count is
		/// required: without one there is no defined default size (B1), and the
		/// command is rejected with a clean error instead of surfacing an engine
		/// exception.
		/// </summary>
		private void DoCreateNew(string[] tokens)
		{
			// Grammar: CREATE NEW <file> SLOTS n  (tokens[0] is "CREATE")
			if (tokens.Length < 3 || !tokens[1].Equals("NEW", StringComparison.OrdinalIgnoreCase) || tokens[2].Length == 0)
			{
				Console.WriteLine("Command Error: CREATE NEW takes a file path and SLOTS n.");
				return;
			}

			string path = tokens[2];

			if (tokens.Length != 5 || !tokens[3].Equals("SLOTS", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("Command Error: CREATE NEW requires a slot count. Use CREATE NEW <file> SLOTS n.");
				return;
			}

			if (!IsDigits(tokens[4]))
			{
				Console.WriteLine("Command Error: CREATE NEW requires a slot count. Use CREATE NEW <file> SLOTS n.");
				return;
			}

			int slots;
			try
			{
				slots = int.Parse(tokens[4]);
			}
			catch (Exception)
			{
				Console.WriteLine("Command Error: CREATE NEW requires a slot count. Use CREATE NEW <file> SLOTS n.");
				return;
			}

			if (slots <= 0)
			{
				Console.WriteLine("Command Error: CREATE NEW requires a slot count. Use CREATE NEW <file> SLOTS n.");
				return;
			}

			// Never clobber the loaded database (or any existing file) silently.
			if (this.databaseLoaded && this.dbAsGiven != null)
			{
				try
				{
					string a = Path.GetFullPath(path);
					string b = Path.GetFullPath(this.dbAsGiven);
					if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
					{
						Console.WriteLine("Command Error: that is the loaded database. UNLOAD first.");
						return;
					}
				}
				catch (Exception)
				{
				}
			}

			if (File.Exists(path))
			{
				Console.WriteLine("Command Error: " + path + " already exists. Delete it first.");
				return;
			}

			try
			{
				int granted = this.engine.CreateNewDatabase(path, slots);
				long bytes = new FileInfo(path).Length;
				Console.WriteLine("OK, created " + path + " with " + granted + " record slots (" + bytes + " bytes)");
			}
			catch (Exception e)
			{
				Console.WriteLine("Database Exception: " + e.Message);
			}
		}

			/// <summary>
		/// CREATE CSV [path] (D59): create a blank gsa-cli-compatible CSV by
		/// copying the embedded template resource out to disk verbatim. Works in
		/// config mode and with a database loaded. No path means template.csv in
		/// the current working directory. Never overwrites an existing file.
		/// </summary>
		private void DoCreateCsv(string[] tokens)
		{
			// Grammar: CREATE CSV [path]  (tokens[0] is "CREATE", tokens[1] is "CSV")
			if (tokens.Length > 3)
			{
				Console.WriteLine("Command Error: CREATE CSV takes an optional file path.");
				return;
			}

			string path = (tokens.Length == 3) ? tokens[2] : "template.csv";

			if (File.Exists(path))
			{
				Console.WriteLine("Command Error: " + path + " already exists. Delete it first.");
				return;
			}

			try
			{
				long bytes = CsvTemplate.CreateBlankCsv(path);
				Console.WriteLine("OK, created " + path + " (" + bytes + " bytes)");
			}
			catch (Exception e)
			{
				Console.WriteLine("Command Error: could not create " + path + " (" + e.Message + ").");
			}
		}

	/* ---------- Phase 4 helpers: INSERT/UPDATE validation, codegen, defaults ---------- */

		/// <summary>
		/// Documented INSERT field order (SPECIFICATION.md #INSERT) - positional
		/// VALUES mapping and deterministic application order both follow this.
		/// </summary>
		private static readonly string[] InsertFields = new string[] {
			"ITEM_CODE", "ITEM_NAME", "DEPT", "SALEPRICECENTS", "SALEPRICE2",
			"QTY", "TRACK_MOVEMENT", "REQUIRE_QTY", "SELL_HOURS", "SELL_RESTRICTED",
			"NO_RETURN", "GIVEAWAY", "TAX1", "TAX2", "TAX3", "TAX4",
			"PRICE_METHOD", "DCS_METHOD", "REQUIRE_PRICE", "MPGROUP"
		};

		/// <summary>
		/// Effective end of a raw line: trailing whitespace and one optional
		/// trailing ';' excluded (mirrors CommandLex.Lex) so clause re-scans do not
		/// swallow the terminator.
		/// </summary>
		private static int ClauseEnd(string line)
		{
			int end = line.Length;
			while (end > 0 && char.IsWhiteSpace(line[end - 1]))
			{
				end--;
			}

			if (end > 0 && line[end - 1] == ';')
			{
				end--;
			}

			return end;
		}

		/// <summary>
		/// INSERT VALUES in both documented forms: positional (4 or all 20 values)
		/// and named (INSERT INTO (fields...) VALUES (...)). Returns field name to
		/// unquoted value.
		/// </summary>
		private static Dictionary<string, string> ParseInsertFields(string[] tokens, string line, int[] starts)
		{
			int valsIdx = -1;
			for (int i = 1; i < tokens.Length; i++)
			{
				if (tokens[i].ToUpperInvariant() == "VALUES")
				{
					valsIdx = i;
					break;
				}
			}

			if (valsIdx < 0)
			{
				throw new CommandSyntaxException("Command Error: INSERT takes a VALUES clause.");
			}

			int end = ClauseEnd(line);
			int from = starts[valsIdx] + 6; // past "VALUES"
			string text = (from <= end) ? line.Substring(from, end - from) : string.Empty;
			List<string> values = CommandLex.ScanClause(text);

			// Named form: the field list sits between INTO and VALUES as plain tokens.
			int intoIdx = -1;
			for (int i = 1; i < valsIdx; i++)
			{
				if (tokens[i].ToUpperInvariant() == "INTO")
				{
					intoIdx = i;
					break;
				}
			}

			Dictionary<string, string> fields = new Dictionary<string, string>();

			if (intoIdx >= 0)
			{
				List<string> names = new List<string>();
				for (int i = intoIdx + 1; i < valsIdx; i++)
				{
					string t = tokens[i];
					while (t.Length > 0 && (t[0] == '(' || t[0] == ','))
					{
						t = t.Substring(1);
					}

					while (t.Length > 0 && (t[t.Length - 1] == ')' || t[t.Length - 1] == ','))
					{
						t = t.Substring(0, t.Length - 1);
					}

					t = t.Trim().ToUpperInvariant();
					if (t.Length > 0)
					{
						names.Add(t);
					}
				}

				if (names.Count == 0)
				{
					throw new CommandSyntaxException("Command Error: INSERT INTO takes a field list in parentheses.");
				}

				if (values.Count != names.Count)
				{
					throw new CommandSyntaxException("Command Error: INSERT INTO lists " + names.Count + " fields but VALUES has " + values.Count + " value(s).");
				}

				for (int i = 0; i < names.Count; i++)
				{
					bool known = false;
					for (int k = 0; k < InsertFields.Length; k++)
					{
						if (InsertFields[k] == names[i])
						{
							known = true;
							break;
						}
					}

					if (!known)
					{
						throw new CommandSyntaxException("Validation Error: Unknown field " + names[i]);
					}

					fields[names[i]] = UnquoteValue(values[i]);
				}

				return fields;
			}

			// Positional form: exactly the four required fields, or all twenty.
			if (values.Count != 4 && values.Count != InsertFields.Length)
			{
				throw new CommandSyntaxException("Validation Error: When specifying optional parameters, all parameters must be specified. Please specify all parameters or build a custom query string.");
			}

			for (int i = 0; i < values.Count; i++)
			{
				fields[InsertFields[i]] = UnquoteValue(values[i]);
			}

			return fields;
		}

		/// <summary>
		/// Applies one validated field value to a record (INSERT and UPDATE SET
		/// share this). Throws CommandSyntaxException with the operator-facing
		/// message on invalid values.
		/// </summary>
		private void ApplyField(RecordData rec, string f, string v)
		{
			switch (f)
			{
				case "ITEM_NAME":
					rec.ItemName = ValidateName(v);
					break;

				case "DEPT":
					rec.Dept = ValidateDept(v);
					break;

				case "SALEPRICECENTS":
					rec.SalePriceCents = ParseCents("SALEPRICECENTS", v);
					break;

				case "SALEPRICE2":
					rec.SalePrice2Cents = ParseCents("SALEPRICE2", v);
					break;

				case "QTY":
					rec.Qty = (byte)ValidateSmall("QTY", v, 99);
					break;

				case "TRACK_MOVEMENT":
					rec.Mvmt = ValidateYn(f, v);
					break;

				case "REQUIRE_QTY":
					rec.ReqQty = ValidateYn(f, v);
					break;

				case "SELL_HOURS":
					rec.AllHours = ValidateYn(f, v); // D5: friendly name for the inverted bit
					break;

				case "SELL_RESTRICTED":
					rec.RestrictedHours = ValidateYn(f, v);
					break;

				case "NO_RETURN":
					rec.NoReturn = ValidateYn(f, v);
					break;

				case "GIVEAWAY":
					rec.Giveaway = ValidateYn(f, v);
					break;

				case "TAX1":
					rec.Tax1 = ValidateYn(f, v);
					break;

				case "TAX2":
					rec.Tax2 = ValidateYn(f, v);
					break;

				case "TAX3":
					rec.Tax3 = ValidateYn(f, v);
					break;

				case "TAX4":
					rec.Tax4 = ValidateYn(f, v);
					break;

				case "PRICE_METHOD":
					rec.PriceMethod = (byte)ValidateSmall("PRICE_METHOD", v, 3);
					break;

				case "DCS_METHOD":
					rec.DcsMethod = (byte)ValidateSmall("DCS_METHOD", v, 8);
					break;

				case "REQUIRE_PRICE":
					rec.ReqPrice = ValidateYn(f, v);
					break;

				case "MPGROUP":
					rec.MpgGroup = ValidateSmall("MPGROUP", v, 99);
					break;

				default:
					throw new CommandSyntaxException("Validation Error: Unknown field " + f);
			}
		}

		private static bool ValidateYn(string field, string v)
		{
			if (v == "Y")
			{
				return true;
			}

			if (v == "N")
			{
				return false;
			}

			throw new CommandSyntaxException("Validation Error: " + field + " accepts 'Y' or 'N'.");
		}

		private static int ValidateSmall(string field, string v, int max)
		{
			if (!IsDigits(v))
			{
				throw new CommandSyntaxException("Validation Error: " + field + " must be a number 0-" + max + ".");
			}

			int n;
			try
			{
				n = int.Parse(v);
			}
			catch (Exception)
			{
				throw new CommandSyntaxException("Validation Error: " + field + " is out of range (0-" + max + ").");
			}

			if (n < 0 || n > max)
			{
				throw new CommandSyntaxException("Validation Error: " + field + " is out of range (0-" + max + ").");
			}

			return n;
		}

		private static int ParseCents(string field, string v)
		{
			// Dollar notation (Decision 23): offer the cents conversion.
			int dot = v.IndexOf('.');
			if (dot > 0)
			{
				string ip = v.Substring(0, dot);
				string fp = v.Substring(dot + 1);

				if (IsDigits(ip) && IsDigits(fp) && fp.Length <= 2)
				{
					long dollars;
					try
					{
						dollars = long.Parse(ip);
					}
					catch (Exception)
					{
						dollars = -1;
					}

					if (dollars >= 0)
					{
						int frac = 0;
						for (int i = 0; i < fp.Length && i < 2; i++)
						{
							frac = frac * 10 + (fp[i] - '0');
						}

						if (fp.Length == 1)
						{
							frac *= 10;
						}

						throw new CommandSyntaxException("Validation Error: " + field + " takes integer cents. Did you mean " + (dollars * 100L + frac) + "?");
					}
				}
			}

			if (!IsDigits(v))
			{
				throw new CommandSyntaxException("Validation Error: " + field + " must be a non-negative integer number of cents.");
			}

			long n;
			try
			{
				n = long.Parse(v);
			}
			catch (Exception)
			{
				throw new CommandSyntaxException("Validation Error: " + field + " is out of range.");
			}

			if (n > 9999999L)
			{
				throw new CommandSyntaxException("Validation Error: " + field + " is out of range (max 9,999,999 cents).");
			}

			return (int)n;
		}

		private string ValidateName(string v)
		{
			if (v.Length == 0)
			{
				throw new CommandSyntaxException("Validation Error: ITEM_NAME must not be empty.");
			}

			// Allowed: letters, digits, spaces. Names are stored uppercase -
			// lowercase input is auto-translated (operator-friendly).
			char[] chars = v.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				char c = chars[i];
				if (c >= 'a' && c <= 'z')
				{
					c = (char)(c - ('a' - 'A'));
				}

				bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ';
				if (!ok)
				{
					throw new CommandSyntaxException("Validation Error: ITEM_NAME contains invalid characters (alphanumeric and spaces only).");
				}

				chars[i] = c;
			}

			v = new string(chars);

			if (v.Length > 18)
			{
				string mode = "truncate";
				string fileKey;
				string val;
				if (this.settings.TryGet("NAME_VALIDATION", out fileKey, out val))
				{
					mode = val.ToLowerInvariant();
				}

				if (mode == "reject")
				{
					throw new CommandSyntaxException("Validation Error: ITEM_NAME is too long (max 18 characters; got " + v.Length + ").");
				}

				// D60: during IMPORT one warning per row would flood the console;
				// count instead and print a single aggregate line at run end.
				if (this.quietWarnings)
				{
					this.nameTruncations++;
				}
				else
				{
					Console.WriteLine("Warning: ITEM_NAME truncated to 18 characters.");
				}

				v = v.Substring(0, 18);
			}

			return v;
		}

		private static string ValidateDept(string v)
		{
			if (!IsDigits(v) || v.Length > 6)
			{
				throw new CommandSyntaxException("Validation Error: DEPT must be up to 6 digits.");
			}

			int n;
			try
			{
				n = int.Parse(v);
			}
			catch (Exception)
			{
				throw new CommandSyntaxException("Validation Error: DEPT is out of range (1-999999).");
			}

			if (n < 1 || n > 999999)
			{
				throw new CommandSyntaxException("Validation Error: DEPT is out of range (1-999999).");
			}

			return v;
		}

		private static void ValidateInsertCode(string code)
		{
			if (!IsDigits(code))
			{
				throw new CommandSyntaxException("Validation Error: The required parameter ITEM_CODE contains invalid characters (numeric digits only)");
			}

			if (code.Length > 12)
			{
				throw new CommandSyntaxException("Validation Error: The required parameter ITEM_CODE is out of range (max 12 digits; got " + code.Length + ")");
			}
		}

		private static RecordData CopyRecord(RecordData r)
		{
			RecordData c = new RecordData();
			c.Code = r.Code;
			c.ItemName = r.ItemName;
			c.Dept = r.Dept;
			c.SalePriceCents = r.SalePriceCents;
			c.SalePrice2Cents = r.SalePrice2Cents;
			c.Qty = r.Qty;
			c.Mvmt = r.Mvmt;
			c.ReqQty = r.ReqQty;
			c.ReqPrice = r.ReqPrice;
			c.AllHours = r.AllHours;
			c.RestrictedHours = r.RestrictedHours;
			c.NoReturn = r.NoReturn;
			c.Giveaway = r.Giveaway;
			c.Tax1 = r.Tax1;
			c.Tax2 = r.Tax2;
			c.Tax3 = r.Tax3;
			c.Tax4 = r.Tax4;
			c.PriceMethod = r.PriceMethod;
			c.DcsMethod = r.DcsMethod;
			c.MpgGroup = r.MpgGroup;
			c.User = r.User;
			c.WeightPriceReq = r.WeightPriceReq;
			c.StockNumberReq = r.StockNumberReq;
			c.Indicat1Reserved = r.Indicat1Reserved;
			return c;
		}

		private RecordData NewRecordFromSettings()
		{
			RecordData rec = new RecordData();
			rec.Code = string.Empty;
			rec.ItemName = string.Empty;
			rec.Dept = string.Empty;
			rec.SalePriceCents = 0;
			rec.SalePrice2Cents = 0;

			rec.Qty = (byte)SettingInt("QTY", 0);
			rec.Mvmt = SettingBool("TRACK_MOVEMENT", false);
			rec.ReqQty = SettingBool("REQUIRE_QTY", false);
			rec.AllHours = SettingBool("SELL_HOURS", true); // D5: SELL_HOURS=Y clears NotAuthorizedSale
			rec.RestrictedHours = SettingBool("SELL_RESTRICTED", true);
			rec.NoReturn = SettingBool("NO_RETURN", false);
			rec.Giveaway = SettingBool("GIVEAWAY", false);
			rec.Tax1 = SettingBool("TAX1", false);
			rec.Tax2 = SettingBool("TAX2", false);
			rec.Tax3 = SettingBool("TAX3", false);
			rec.Tax4 = SettingBool("TAX4", false);
			rec.PriceMethod = (byte)SettingInt("PRICE_METHOD", 0);
			rec.DcsMethod = (byte)SettingInt("DCS_METHOD", 0);
			rec.ReqPrice = SettingBool("REQUIRE_PRICE", false);
			rec.MpgGroup = SettingInt("MPGROUP", 0);

			return rec;
		}

		private int SettingInt(string key, int def)
		{
			string fileKey;
			string v;
			if (this.settings.TryGet(key, out fileKey, out v))
			{
				try
				{
					return int.Parse(v);
				}
				catch (Exception)
				{
				}
			}

			return def;
		}

		private bool SettingBool(string key, bool def)
		{
			string fileKey;
			string v;
			if (this.settings.TryGet(key, out fileKey, out v))
			{
				if (v == "Y")
				{
					return true;
				}

				if (v == "N")
				{
					return false;
				}
			}

			return def;
		}

		private bool CodeExists(string code)
		{
			if (this.engine.ReadRecord(code) != null)
			{
				return true;
			}

			// A leading-zero spelling of an existing numeric code is the same item.
			if (IsDigits(code))
			{
				try
				{
					long v = long.Parse(code);
					return ContainsLong(NumericCodes(), v);
				}
				catch (Exception)
				{
				}
			}

			return false;
		}

		private static bool ContainsLong(List<long> sorted, long v)
		{
			// .NET 2.0 has no HashSet<T>; a sorted list + binary search is the
			// portable stand-in (and stays fast on large databases).
			return sorted.BinarySearch(v) >= 0;
		}

		private List<long> NumericCodes()
		{
			List<long> list = new List<long>();
			RecordData[] rows = this.engine.ListRecords();
			for (int i = 0; i < rows.Length; i++)
			{
				if (IsDigits(rows[i].Code))
				{
					try
					{
						list.Add(long.Parse(rows[i].Code));
					}
					catch (Exception)
					{
					}
				}
			}

			list.Sort();
			return list;
		}

		private string GenerateRnd4()
		{
			// D32: exhaustive deterministic scan of all ~10,000 candidates.
			List<long> used = NumericCodes();
			for (int n = 1000; n <= 9999; n++)
			{
				if (!ContainsLong(used, n))
				{
					return n.ToString();
				}
			}

			throw new CommandSyntaxException("Database Exception: no free 4-digit code (all 1000-9999 in use).");
		}

		private string GenerateUpcA()
		{
			List<long> used = NumericCodes();
			for (long base11 = 10000000000L; base11 <= 99999999999L; base11++)
			{
				long code = base11 * 10 + UpcACheckDigit(base11);
				if (!ContainsLong(used, code))
				{
					return code.ToString();
				}
			}

			throw new CommandSyntaxException("Database Exception: no free UPC-A code.");
		}

		private static int UpcACheckDigit(long first11)
		{
			int sum = 0;
			long x = first11;
			bool odd = true; // rightmost of the 11 digits is an odd position (weight 3)

			for (int i = 0; i < 11; i++)
			{
				int d = (int)(x % 10);
				sum += odd ? d * 3 : d;
				x /= 10;
				odd = !odd;
			}

			return (10 - sum % 10) % 10;
		}

		/// <summary>
		/// B9: code 0 in any zero-padded spelling is invalid - the engine silently
		/// drops records stored under it, so INSERT/RECODE/IMPORT substitute a
		/// generated free code instead of using it.
		/// </summary>
		private static bool IsZeroCode(string code)
		{
			if (!IsDigits(code))
			{
				return false;
			}

			try
			{
				return long.Parse(code) == 0L;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>B9: generate a free code using the CODE_METHOD setting (default upc-a).</summary>
		private string GenerateByCodeMethod()
		{
			string fileKey;
			string v;
			if (this.settings.TryGet("CODE_METHOD", out fileKey, out v))
			{
				if (v.ToLowerInvariant() == "rnd4")
				{
					return GenerateRnd4();
				}
			}

			return GenerateUpcA(); // settings.xml default is upc-a
		}

		private RecordData ApplySet(RecordData orig, Dictionary<string, string> set_, bool dropSp2)
		{
			RecordData r = CopyRecord(orig);

			foreach (KeyValuePair<string, string> kv in set_)
			{
				string f = kv.Key;
				if (f == "SALEPRICE2" && dropSp2)
				{
					continue; // D42: value dropped for this row
				}

				ApplyField(r, f, kv.Value);
			}

			return r;
		}

		private static void ValidateTarget(Selection sel)
		{
			if (sel.Target == null || sel.Target.Length == 0)
			{
				throw new CommandSyntaxException("Validation Error: target must be *, an item code, or a range <lo>-<hi>.");
			}

			if (sel.Target.IndexOf('-') >= 0)
			{
				string[] parts = sel.Target.Split(new char[] { '-' });
				long lo = 0;
				long hi = 0;
				bool ok = parts.Length == 2 && IsDigits(parts[0]) && IsDigits(parts[1]);

				if (ok)
				{
					try
					{
						lo = long.Parse(parts[0]);
						hi = long.Parse(parts[1]);
					}
					catch (Exception)
					{
						ok = false;
					}
				}

				if (!ok)
				{
					throw new CommandSyntaxException("Validation Error: range bounds must be plain item codes (patterns belong in WHERE).");
				}

				sel.IsRange = true;
				sel.RangeLo = lo;
				sel.RangeHi = hi;
			}
			else if (sel.Target != "*" && !IsDigits(sel.Target))
			{
				throw new CommandSyntaxException("Validation Error: target must be *, an item code, or a range <lo>-<hi>.");
			}
		}

		/* ---------- selection parsing (target / WHERE / VALUES) ---------- */

		private class Selection
		{
			public string Target;
			public bool IsRange;
			public long RangeLo;
			public long RangeHi;
			public WhereExpr Where;
			public string[] Columns;
		}

		private Selection ParseSelection(string[] tokens, string verb)
		{
			if (tokens.Length < 2 || tokens[1].Length == 0)
			{
				throw new CommandSyntaxException(verb + " takes a target (*, code, or range), optionally WHERE and VALUES.");
			}

			Selection sel = new Selection();
			ApplyTarget(sel, tokens[1]);

			int i = 2;
			while (i < tokens.Length)
			{
				string t = tokens[i].ToUpperInvariant();

				if (t == "WHERE")
				{
					i++;
					int end = i;
					while (end < tokens.Length && tokens[end].ToUpperInvariant() != "VALUES")
					{
						end++;
					}
					sel.Where = ParseWhere(tokens, i, end);
					i = end;
				}
				else if (t == "VALUES")
				{
					if (i + 1 >= tokens.Length)
					{
						throw new CommandSyntaxException("Command Error: VALUES takes a column list in parentheses.");
					}
					sel.Columns = ParseColumns(tokens, i + 1);
					break; // VALUES is the last clause on the line
				}
				else
				{
					throw new CommandSyntaxException(verb + " syntax error: unexpected token '" + tokens[i] + "'.");
				}
			}

			return sel;
		}

		/// <summary>
		/// Target validation shared by SELECT/COUNT/UPDATE/DROP (via
		/// ParseSelection) and EXPORT: * / plain code / lo-hi range.
		/// </summary>
		private static void ApplyTarget(Selection sel, string raw)
		{
			sel.Target = raw;

			if (raw.IndexOf('-') >= 0)
			{
				string[] parts = raw.Split(new char[] { '-' });
				long lo = 0;
				long hi = 0;
				bool ok = parts.Length == 2 && IsDigits(parts[0]) && IsDigits(parts[1]);

				if (ok)
				{
					try
					{
						lo = long.Parse(parts[0]);
						hi = long.Parse(parts[1]);
					}
					catch (Exception)
					{
						ok = false;
					}
				}

				if (!ok)
				{
					throw new CommandSyntaxException("Validation Error: range bounds must be plain item codes (patterns belong in WHERE).");
				}

				sel.IsRange = true;
				sel.RangeLo = lo;
				sel.RangeHi = hi;
			}
			else if (raw != "*" && !IsDigits(raw))
			{
				throw new CommandSyntaxException("Validation Error: target must be *, an item code, or a range <lo>-<hi>.");
			}
		}

		private static string[] DefaultColumns()
		{
			return new string[] { "ITEM_CODE", "ITEM_NAME", "DEPT", "SALEPRICECENTS", "QTY", "MVMT", "REQ_QTY", "REQ_PRICE", "ALL_HOURS", "RESTRICTED_HOURS", "NO_RETURN", "GIVEAWAY", "TAX1", "TAX2", "TAX3", "TAX4", "PRICE_METHOD", "DCS_METHOD" };
		}

		private string[] ParseColumns(string[] tokens, int start)
		{
			List<string> names = new List<string>();

			for (int i = start; i < tokens.Length; i++)
			{
				string t = tokens[i];
				if (t.StartsWith("("))
				{
					t = t.Substring(1);
				}
				if (t.EndsWith(")"))
				{
					t = t.Substring(0, t.Length - 1);
				}

				string[] pieces = t.Split(new char[] { ',' });
				for (int p = 0; p < pieces.Length; p++)
				{
					string name = pieces[p].Trim().ToUpperInvariant();
					if (name.Length > 0)
					{
						names.Add(name);
					}
				}
			}

			if (names.Count == 0)
			{
				throw new CommandSyntaxException("Command Error: VALUES takes a column list in parentheses.");
			}

			for (int n = 0; n < names.Count; n++)
			{
				if (!IsKnownField(names[n]))
				{
					throw new CommandSyntaxException("Validation Error: unknown column '" + names[n] + "' in VALUES.");
				}
			}

			return names.ToArray();
		}

		/* ---------- WHERE (D9 boolean predicates, D7 normalized match) ---------- */

		private class WhereExpr
		{
			public bool IsLeaf;
			public string Field;
			public string Value;
			public string Op; // "AND" | "OR"
			public WhereExpr Left;
			public WhereExpr Right;
		}

		private static List<string> ExpandWhereTokens(string[] tokens, int from, int to)
		{
			List<string> out_ = new List<string>();
			for (int i = from; i < to; i++)
			{
				string t = tokens[i];
				bool open = t.StartsWith("(");
				if (open)
				{
					t = t.Substring(1);
				}
				bool close = t.EndsWith(")");
				if (close)
				{
					t = t.Substring(0, t.Length - 1);
				}

				if (open)
				{
					out_.Add("(");
				}
				if (t.Length > 0)
				{
					out_.Add(t);
				}
				if (close)
				{
					out_.Add(")");
				}
			}
			return out_;
		}

		private WhereExpr ParseWhere(string[] tokens, int from, int to)
		{
			return ParseWhere(tokens, from, to, false);
		}

		/// <summary>
		/// csvFields=true (IMPORT filters): field names are validated against the
		/// 19-column CSV schema instead of the SELECT column set - the two
		/// surfaces spell flags differently (TRACK_MOVEMENT vs MVMT, D39).
		/// </summary>
		private WhereExpr ParseWhere(string[] tokens, int from, int to, bool csvFields)
		{
			List<string> tk = ExpandWhereTokens(tokens, from, to);
			if (tk.Count == 0)
			{
				throw new CommandSyntaxException("Command Error: WHERE takes at least one predicate.");
			}

			int pos = 0;
			WhereExpr e = ParseOr(tk, ref pos, csvFields);
			if (pos != tk.Count)
			{
				throw new CommandSyntaxException("Command Error: unexpected token in WHERE clause.");
			}
			return e;
		}

		private WhereExpr ParseOr(List<string> tk, ref int pos, bool csvFields)
		{
			WhereExpr left = ParseAnd(tk, ref pos, csvFields);
			while (pos < tk.Count && tk[pos].ToUpperInvariant() == "OR")
			{
				pos++;
				WhereExpr n = new WhereExpr();
				n.Op = "OR";
				n.Left = left;
				n.Right = ParseAnd(tk, ref pos, csvFields);
				left = n;
			}
			return left;
		}

		private WhereExpr ParseAnd(List<string> tk, ref int pos, bool csvFields)
		{
			WhereExpr left = ParseAtom(tk, ref pos, csvFields);
			while (pos < tk.Count && tk[pos].ToUpperInvariant() == "AND")
			{
				pos++;
				WhereExpr n = new WhereExpr();
				n.Op = "AND";
				n.Left = left;
				n.Right = ParseAtom(tk, ref pos, csvFields);
				left = n;
			}
			return left;
		}

		private WhereExpr ParseAtom(List<string> tk, ref int pos)
		{
			return ParseAtom(tk, ref pos, false);
		}

		private WhereExpr ParseAtom(List<string> tk, ref int pos, bool csvFields)
		{
			if (pos >= tk.Count)
			{
				throw new CommandSyntaxException("Command Error: WHERE clause is incomplete.");
			}

			string t = tk[pos];

			if (t == "(")
			{
				pos++;
				WhereExpr inner = ParseOr(tk, ref pos, csvFields);
				if (pos >= tk.Count || tk[pos] != ")")
				{
					throw new CommandSyntaxException("Command Error: missing ')' in WHERE clause.");
				}
				pos++;
				return inner;
			}

			int eq = t.IndexOf('=');
			if (eq <= 0)
			{
				throw new CommandSyntaxException("Validation Error: predicate must be FIELD='VALUE'.");
			}

			string field = t.Substring(0, eq).Trim().ToUpperInvariant();

			// B3: WHERE on ITEM_CODE used to be accepted and then silently match
			// nothing (the code is the record key, selected via the target form).
			// Reject it with a nudge toward the target instead. IMPORT's CSV row
			// filter (csvFields) keeps working - there is no target to replace.
			if (!csvFields && field == "ITEM_CODE")
			{
				throw new CommandSyntaxException("Validation Error: WHERE on ITEM_CODE is not supported - use the item code as the command target instead (e.g. SELECT 100 rather than SELECT * WHERE ITEM_CODE='100').");
			}

			if (!IsKnownField(field, csvFields))
			{
				throw new CommandSyntaxException("Validation Error: unknown field '" + field + "' in WHERE.");
			}

			WhereExpr leaf = new WhereExpr();
			leaf.IsLeaf = true;
			leaf.Field = field;
			leaf.Value = UnquoteValue(t.Substring(eq + 1));
			pos++;
			return leaf;
		}

		private static bool IsKnownField(string field)
		{
			return IsKnownField(field, false);
		}

		private static bool IsKnownField(string field, bool csvFields)
		{
			if (csvFields)
			{
				for (int i = 0; i < CsvColumns.Length; i++)
				{
					if (CsvColumns[i] == field)
					{
						return true;
					}
				}

				return false;
			}

			if (field == "SALEPRICE2")
			{
				return true;
			}
			string[] known = DefaultColumns();
			for (int i = 0; i < known.Length; i++)
			{
				if (known[i] == field)
				{
					return true;
				}
			}
			return false;
		}

		private static string UnquoteValue(string s)
		{
			s = s.Trim();
			if (s.Length >= 2)
			{
				char a = s[0];
				char b = s[s.Length - 1];
				if ((a == '\'' && b == '\'') || (a == '"' && b == '"'))
				{
					return s.Substring(1, s.Length - 2);
				}
			}
			return s;
		}

		private bool EvaluateWhere(WhereExpr e, RecordData row)
		{
			if (!e.IsLeaf)
			{
				bool l = EvaluateWhere(e.Left, row);
				bool r = EvaluateWhere(e.Right, row);
				return e.Op == "AND" ? (l && r) : (l || r);
			}

			return ValueMatches(FieldString(row, e.Field), e.Value);
		}

		/// <summary>
		/// The row's value for the named column in its matchable string form
		/// (D55 fixed schema: typed fields formatted on demand). Every field in
		/// IsKnownField's set is always present, so there is no miss case.
		/// </summary>
		private static string FieldString(RecordData r, string field)
		{
			switch (field)
			{
				case "ITEM_NAME": return r.ItemName;
				case "DEPT": return r.Dept;
				case "SALEPRICECENTS": return r.SalePriceCents.ToString();
				case "QTY": return r.Qty.ToString();
				case "MVMT": return FlagStr(r.Mvmt);
				case "REQ_QTY": return FlagStr(r.ReqQty);
				case "REQ_PRICE": return FlagStr(r.ReqPrice);
				case "ALL_HOURS": return FlagStr(r.AllHours);
				case "RESTRICTED_HOURS": return FlagStr(r.RestrictedHours);
				case "NO_RETURN": return FlagStr(r.NoReturn);
				case "GIVEAWAY": return FlagStr(r.Giveaway);
				case "TAX1": return FlagStr(r.Tax1);
				case "TAX2": return FlagStr(r.Tax2);
				case "TAX3": return FlagStr(r.Tax3);
				case "TAX4": return FlagStr(r.Tax4);
				case "PRICE_METHOD": return r.PriceMethod.ToString();
				case "DCS_METHOD": return r.DcsMethod.ToString();
				case "SALEPRICE2": return r.SalePrice2Cents.ToString();
			}

			return string.Empty; // unknown field - IsKnownField rejects these upstream
		}

		private static string FlagStr(bool b)
		{
			return b ? "Y" : "N";
		}

		private static bool IsDigits(string s)
		{
			if (s.Length == 0)
			{
				return false;
			}
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] < '0' || s[i] > '9')
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// D7 normalization: trim, and for all-digit values drop leading
		/// zeros (at least one digit kept). Names keep case; matching is
		/// case-insensitive.
		/// </summary>
		private static string NormalizeForMatch(string s)
		{
			s = s.Trim();
			if (IsDigits(s))
			{
				int cut = 0;
				while (cut < s.Length - 1 && s[cut] == '0')
				{
					cut++;
				}
				return s.Substring(cut);
			}
			return s;
		}

		private static bool ValueMatches(string stored, string wanted)
		{
			string a = NormalizeForMatch(stored);
			string b = NormalizeForMatch(wanted);

			if (b.IndexOf('*') >= 0 || b.IndexOf('?') >= 0)
			{
				return PatternMatch(b, a);
			}

			return a.Equals(b, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// '*' = any run of characters (including empty), '?' = exactly one.
		/// Case-insensitive. Patterns are short operator input against
		/// bounded field values, so the naive recursion is fine.
		/// </summary>
		private static bool PatternMatch(string pattern, string value)
		{
			for (int i = 0; i < pattern.Length; i++)
			{
				char c = pattern[i];

				if (c == '*')
				{
					string rest = pattern.Substring(i + 1);
					for (int k = value.Length; k >= 0; k--)
					{
						// D63: a leading '*' consumes a prefix of the value, so 'rest'
						// must match some SUFFIX - this is what makes '*KROGER*' work.
						if (PatternMatch(rest, value.Substring(k)))
						{
							return true;
						}
					}
					return false;
				}

				if (i >= value.Length)
				{
					return false;
				}
				// D63: '?' matches exactly one character of any kind.
				if (c != '?' && char.ToUpper(c) != char.ToUpper(value[i]))
				{
					return false;
				}
			}

			return pattern.Length == value.Length;
		}

		/* ---------- selection + rendering ---------- */

		// ^C contract for long-running commands: poll Abort.IsRequested at
		// record boundaries and bail early. SelectRows signals an abort by
		// returning null (its normal result is always a non-null array); the
		// caller prints "Aborted." and returns to the prompt, and Repl's post-
		// Execute sweep consumes the flag. Note ListRecords() itself runs to
		// completion - it is library code we do not instrument; everything
		// after materialization aborts promptly.
		private RecordData[] SelectRows(Selection sel)
		{
			RecordData[] all = this.engine.ListRecords();

			// Fast path (D57): unfiltered * over the whole table - hand back the
			// engine's array directly instead of copying 600k references through
			// a List + ToArray every cycle. Session treats rows as read-only.
			if (sel.Target == "*" && sel.Where == null)
			{
				return all;
			}

			List<RecordData> kept = new List<RecordData>();

			for (int i = 0; i < all.Length; i++)
			{
				if (Abort.IsRequested)
				{
					return null;
				}
				if (!TargetMatches(sel, all[i]))
				{
					continue;
				}
				if (sel.Where != null && !EvaluateWhere(sel.Where, all[i]))
				{
					continue;
				}
				kept.Add(all[i]);
			}

			return kept.ToArray();
		}

		private static bool TargetMatches(Selection sel, RecordData row)
		{
			if (sel.Target == "*")
			{
				return true;
			}

			long code;
			try
			{
				code = long.Parse(row.Code);
			}
			catch (Exception)
			{
				return false; // non-numeric (hex) codes never match a numeric target
			}

			if (sel.IsRange)
			{
				return code >= sel.RangeLo && code <= sel.RangeHi;
			}

			long want;
			try
			{
				want = long.Parse(sel.Target);
			}
			catch (Exception)
			{
				return false;
			}

			return code == want;
		}

		private string[] ResolveColumns(RecordData[] rows, string[] requested)
		{
			if (requested != null)
			{
				return requested;
			}

			bool needPrice2 = false;
			for (int i = 0; i < rows.Length && !needPrice2; i++)
			{
				if (rows[i].PriceMethod == 2 || rows[i].PriceMethod == 3)
				{
					needPrice2 = true;
				}
			}

			List<string> cols = new List<string>();
			foreach (string c in DefaultColumns())
			{
				cols.Add(c);
				if (c == "SALEPRICECENTS" && needPrice2)
				{
					cols.Add("SALEPRICE2"); // D36: shown additionally for method 2/3
				}
			}

			return cols.ToArray();
		}

		private string RenderCell(RecordData row, string column)
		{
			switch (column)
			{
				case "ITEM_CODE": return row.Code; // canonical library read-back form (D24 update / D46)
				case "ITEM_NAME": return row.ItemName;
				case "DEPT": return RenderDept(row.Dept);
				case "SALEPRICECENTS": return row.SalePriceCents.ToString();
				case "QTY": return row.Qty.ToString();
				case "MVMT": return FlagStr(row.Mvmt);
				case "REQ_QTY": return FlagStr(row.ReqQty);
				case "REQ_PRICE": return FlagStr(row.ReqPrice);
				case "ALL_HOURS": return FlagStr(row.AllHours);
				case "RESTRICTED_HOURS": return FlagStr(row.RestrictedHours);
				case "NO_RETURN": return FlagStr(row.NoReturn);
				case "GIVEAWAY": return FlagStr(row.Giveaway);
				case "TAX1": return FlagStr(row.Tax1);
				case "TAX2": return FlagStr(row.Tax2);
				case "TAX3": return FlagStr(row.Tax3);
				case "TAX4": return FlagStr(row.Tax4);
				case "PRICE_METHOD": return row.PriceMethod.ToString();
				case "DCS_METHOD": return row.DcsMethod.ToString();
				case "SALEPRICE2": return row.SalePrice2Cents.ToString();
			}

			return string.Empty; // unknown column - validated upstream, empty cell as before
		}

		/// <summary>
		/// Length of what RenderCell would produce for this row/column,
		/// computed without allocating the cell string (D57). Mirrors
		/// RenderCell case-for-case - keep the two in lockstep. DoSelect's
		/// width pass uses it to size columns before streaming rows, so a
		/// 600k-row SELECT no longer materializes every cell up front.
		/// </summary>
		private int CellLength(RecordData row, string column)
		{
			switch (column)
			{
				case "ITEM_CODE": return row.Code.Length;
				case "ITEM_NAME": return row.ItemName.Length;
				case "DEPT": return DeptLength(row.Dept);
				case "SALEPRICECENTS": return Digits(row.SalePriceCents);
				case "QTY": return Digits(row.Qty);
				case "MVMT": return 1; // FlagStr: always one char (Y/N)
				case "REQ_QTY": return 1;
				case "REQ_PRICE": return 1;
				case "ALL_HOURS": return 1;
				case "RESTRICTED_HOURS": return 1;
				case "NO_RETURN": return 1;
				case "GIVEAWAY": return 1;
				case "TAX1": return 1;
				case "TAX2": return 1;
				case "TAX3": return 1;
				case "TAX4": return 1;
				case "PRICE_METHOD": return Digits(row.PriceMethod);
				case "DCS_METHOD": return Digits(row.DcsMethod);
				case "SALEPRICE2": return Digits(row.SalePrice2Cents);
			}

			return 0; // unknown column - RenderCell returns string.Empty
		}

		/// <summary>
		/// Allocation-free mirror of RenderDept's length: same parse, same
		/// name lookup, but measures instead of building "n (name)".
		/// </summary>
		private int DeptLength(string dept)
		{
			if (dept.Length == 0)
			{
				return 0;
			}

			int n;
			if (!int.TryParse(dept, out n))
			{
				return dept.Length; // non-numeric department: as stored
			}

			string name = DeptName(n.ToString());
			if (name != null)
			{
				return Digits(n) + 3 + name.Length; // "n" + " (" + name + ")"
			}

			return Digits(n);
		}

		/// <summary>Exact length of v.ToString() for any int, no allocation.</summary>
		private static int Digits(int v)
		{
			bool neg = v < 0;
			if (v == int.MinValue)
			{
				return 11; // "-2147483648"
			}

			if (neg)
			{
				v = -v;
			}

			int d = 1;
			while (v >= 10)
			{
				v /= 10;
				d++;
			}

			return neg ? d + 1 : d;
		}

		private string RenderDept(string dept)
		{
			if (dept.Length == 0)
			{
				return string.Empty;
			}

			int n;
			if (!int.TryParse(dept, out n))
			{
				return dept; // non-numeric department: as stored
			}

			string name = DeptName(n.ToString());
			if (name != null)
			{
				return n.ToString() + " (" + name + ")";
			}

			return n.ToString();
		}

		private string DeptName(string number)
		{
			List<KeyValuePair<string, string>> depts = this.settings.EnumerateDepartments();
			for (int i = 0; i < depts.Count; i++)
			{
				if (depts[i].Key == number)
				{
					return depts[i].Value;
				}
			}
			return null;
		}

		/// <summary>
		/// One aligned table row: each cell padded to its column width,
		/// columns separated by two spaces. The last column is not padded
		/// (no trailing whitespace). Widths are the widest entry per column
		/// across header and data rows, so every row starts each column at
		/// the same position.
		/// </summary>
		private static string RenderAlignedRow(string[] cells, int[] widths)
		{
			string s = string.Empty;
			for (int i = 0; i < cells.Length; i++)
			{
				if (i > 0)
				{
					s += "  ";
				}
				if (i + 1 < cells.Length)
				{
					s += cells[i].PadRight(widths[i]);
				}
				else
				{
					s += cells[i];
				}
			}
			return s;
		}

		/// <summary>
		/// Saves the settings with the D31 IO-failure answer; callers
		/// abort on false (the change was not applied).
		/// </summary>
		private bool SaveSettings()
		{
			try
			{
				this.settings.Save();
				return true;
			}
			catch (Exception e)
			{
				Console.WriteLine("Settings Error: could not save Settings.xml (" + e.Message + ").");
				return false;
			}
		}

		/// <summary>
		/// Releases the held lock file (D14: UNLOAD / clean exit) and
		/// returns the session to config mode.
		/// </summary>
		private void ReleaseDatabase()
		{
			try
			{
				this.engine.Close();
			}
			catch (Exception)
			{
				// Unloading must not be blocked by a failing close; the lock
				// release below is what matters for D14.
			}

			if (this.databaseLoaded && this.dbAsGiven != null)
			{
				DatabaseLock.Release(this.dbAsGiven);
			}

			this.databaseLoaded = false;
			this.dbAsGiven = null;
			this.stagedChanges = 0;
		}

		/// <summary>
		/// Session teardown: drop any held lock. BYE and the EOF path
		/// both funnel through here so a killed session at the prompt
		/// never leaves a mystery lock file behind.
		/// </summary>
		public void Close()
		{
			ReleaseDatabase();
		}

	}
}
