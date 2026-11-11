// InstanceGuard - cross-instance settings gate (Decision 29).
//
// Contract:
//   - at program start, if another gsa-cli is running, edits to
//     settings are disabled for this session (reads remain usable)
//   - the check is a best-effort process scan: own pid is always
//     excluded; a process counts as gsa-cli when its name is gsa-cli
//     (Windows: gsa-cli.exe; also "mono gsa-cli.exe" hosts, matched on
//     the module path)
//   - scan failure opens the gate: enumeration problems leave settings
//     editable (Decision 29's simplest safe default - a false read-only
//     would strand an operator on a machine where the scan simply does
//     not work)
//
// Plumbing only: this class never prints; the Session composes the
// read-only refusal and the startup note.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GsACli
{
	public static class InstanceGuard
	{
		/// <summary>
		/// Scans the process table for other gsa-cli instances. Returns
		/// null when this process is alone (or the scan failed - fail
		/// open, see remarks), otherwise a short description of the
		/// offenders used in the startup note, e.g. "pid 42831, pid 51002".
		/// </summary>
		public static string DescribeOtherInstances()
		{
			Process[] all;
			try
			{
				all = Process.GetProcesses();
			}
			catch (Exception)
			{
				return null; // fail open
			}

			int self;
			try
			{
				self = Process.GetCurrentProcess().Id;
			}
			catch (Exception)
			{
				return null; // fail open
			}

			List<string> offenders = new List<string>();

			for (int i = 0; i < all.Length; i++)
			{
				Process p = all[i];

				int pid;
				try
				{
					pid = p.Id;
				}
				catch (Exception)
				{
					continue;
				}

				if (pid == self)
				{
					continue;
				}

				string label = "pid " + pid;
				if (IsGsaCli(p))
				{
					offenders.Add(label);
				}
			}

			if (offenders.Count == 0)
			{
				return null;
			}

			return string.Join(", ", offenders.ToArray());
		}

		/// <summary>
		/// Best-effort identity test: name match (gsa-cli / gsa-cli.exe)
		/// or a module path ending in gsa-cli.exe. Identification errors
		/// read "not gsa-cli" (never crash the startup scan).
		/// </summary>
		private static bool IsGsaCli(Process p)
		{
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
				// keep trying
			}

			try
			{
				string module = p.MainModule.ModuleName.ToLowerInvariant();
				if (module.EndsWith("gsa-cli.exe"))
				{
					return true;
				}
			}
			catch (Exception)
			{
				return false;
			}

			return false;
		}
	}
}
