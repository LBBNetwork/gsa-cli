// DatabaseLock - single-operator lock-file contract (Decisions 14, 30).
//
// Contract:
//   - the lock file lives in the .DAT's own directory, named from the
//     lowercased full normalized path (System.IO.Path.GetFullPath,
//     lowercased) with ".lock" appended (Decision 30)
//   - its content is two lines: the owning process id, and the owner's
//     raw-typed path spelling (Decision 30: the operator's spelling is
//     preserved in the lock file content and any in-use message)
//   - plain LOAD fails against ANY existing lock file: "file is already
//     in use by process <pid>."; a crashed owner is not auto-detected
//     (Decision 14: no stale-lock auto-detection)
//   - LOAD OVERRIDE re-checks the owner: it proceeds only when the
//     recorded pid is no longer alive, or is now a non-gsa-cli process;
//     a live gsa-cli holder refuses the override (Decision 14)
//   - the lock is released by deleting the file on UNLOAD and on clean
//     exit (BYE/EOF), best-effort
//
// This file is file-system plumbing only: it never prints. All operator
// messages are composed by the Session layer with its own prefixes.

using System;
using System.Diagnostics;
using System.IO;

namespace GsACli
{
	/// <summary>Outcome of an acquire attempt.</summary>
	public enum LockOutcome
	{
		/// <summary>Lock file is ours now.</summary>
		Acquired,

		/// <summary>Refused: a lock file exists (plain LOAD) or the
		/// owner is unknown and overwrite was not allowed.</summary>
		InUse,

		/// <summary>Refused: OVERRIDE presented, but the holder is a
		/// live gsa-cli process (Decision 14).</summary>
		OverrideRefused
	}

	/// <summary>Structured result of <see cref="DatabaseLock.TryAcquire"/>.</summary>
	public struct LockResult
	{
		public LockOutcome Outcome;
		public int HolderPid;
	}

	public static class DatabaseLock
	{
		/// <summary>
		/// Lock file location for a database path, per Decision 30:
		/// lowercased GetFullPath, same directory, ".lock" appended.
		/// </summary>
		public static string LockPathFor(string asGiven)
		{
			string full = Path.GetFullPath(asGiven).ToLowerInvariant();
			string dir = Path.GetDirectoryName(full);
			if (dir.Length == 0)
			{
				dir = ".";
			}
			return Path.Combine(dir, Path.GetFileName(full) + ".lock");
		}

		/// <summary>
		/// Acquires the lock for asGiven. See class remarks for the
		/// full decision matrix. Throws IOException only for genuine
		/// write failures (the caller composes the Database Exception
		/// message around it); unreadable lock content is treated as an
		/// unknown owner, not an exception.
		/// </summary>
		public static LockResult TryAcquire(string asGiven, bool allowOverride)
		{
			LockResult result = new LockResult();
			result.Outcome = LockOutcome.Acquired;
			result.HolderPid = Process.GetCurrentProcess().Id;

			string lockPath = LockPathFor(asGiven);

			if (File.Exists(lockPath))
			{
				int holder = ReadHolderPid(lockPath);

				if (!allowOverride)
				{
					result.Outcome = LockOutcome.InUse;
					result.HolderPid = holder;
					return result;
				}

				// OVERRIDE (Decision 14): allowed only when the holder is
				// dead, or alive but no longer running gsa-cli.
				if (IsLiveGsaCli(holder))
				{
					result.Outcome = LockOutcome.OverrideRefused;
					result.HolderPid = holder;
					return result;
				}

				// Dead / unknown / non-gsa-cli holder: proceed below and
				// replace the marker.
			}

			// Acquire: pid line + raw operator spelling line (D30).
			string content = Process.GetCurrentProcess().Id + "\n" + asGiven + "\n";
			File.WriteAllText(lockPath, content);

			return result;
		}

		/// <summary>
		/// Releases the lock (file deletion), best-effort. Failures are
		/// swallowed: a stale lock is recoverable via LOAD OVERRIDE, and
		/// the session is ending anyway.
		/// </summary>
		public static void Release(string asGiven)
		{
			try
			{
				File.Delete(LockPathFor(asGiven));
			}
			catch (IOException)
			{
				// best-effort, see remarks
			}
		}

		/// <summary>
		/// Parses the owning pid from line 1 of the lock file. Returns
		/// -1 when the file is unreadable or the line is not a positive
		/// integer - "unknown owner", which is the conservative reading
		/// (plain LOAD refuses; OVERRIDE proceeds).
		/// </summary>
		private static int ReadHolderPid(string lockPath)
		{
			string content = null;
			try
			{
				content = File.ReadAllText(lockPath);
			}
			catch (IOException)
			{
				return -1;
			}

			string first = content;
			int nl = first.IndexOf('\n');
			if (nl >= 0)
			{
				first = first.Substring(0, nl);
			}
			first = first.Trim();

			int pid;
			if (int.TryParse(first, out pid) && pid > 0)
			{
				return pid;
			}

			return -1;
		}

		/// <summary>
		/// True iff the given pid is BOTH a live process AND gsa-cli.
		/// Dead pids and any identification failure read false (the
		/// caller's conservative direction: fail toward allowing override
		/// only behind the OVERRIDE token, fail toward refusing plain LOAD).
		/// </summary>
		private static bool IsLiveGsaCli(int pid)
		{
			if (pid <= 0)
			{
				return false;
			}

			Process p;
			try
			{
				p = Process.GetProcessById(pid);
			}
			catch (ArgumentException)
			{
				return false; // process no longer exists
			}

			try
			{
				string name = p.ProcessName.ToLowerInvariant();
				if (name == "gsa-cli" || name == "gsa-cli.exe")
				{
					return true;
				}
			}
			catch (Exception)
			{
				// keep trying below
			}

			try
			{
				// Module path catches "mono gsa-cli.exe" style hosts where
				// ProcessName reports the host ("mono") instead.
				string module = p.MainModule.ModuleName.ToLowerInvariant();
				if (module.EndsWith("gsa-cli.exe"))
				{
					return true;
				}
			}
			catch (Exception)
			{
				// not identifiable - treat as non-gsa-cli
			}

			return false;
		}
	}
}
