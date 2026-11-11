// Abort - cooperative Ctrl-C signal (REPL only).
//
// ^C must interrupt the current operation and return to the prompt, not
// kill the process. The runtime's default CTRL_C/SIGINT handling terminates
// us, so Program installs a Console.CancelKeyPress handler that cancels the
// event and raises this one-shot flag instead; long-running loops (SELECT /
// COUNT row scans, grid build, print) poll it per record and bail early.
//
// One-shot semantics: each ^C cancels exactly one pending operation - the
// read at the prompt or the running command. Whoever observes the abort
// first acts on it and consumes the flag (Consume), so a stale request can
// never leak into the next line. Repl sweeps any residue after every
// Execute as a safety net.
//
// Contract for Phase 4 mutating commands: poll IsRequested at record
// boundaries, and on abort discard that command's own partial staging -
// D1/D3 batch atomicity means an interrupted UPDATE/INSERT leaves the
// session exactly as it was before the line ran.

namespace GsACli
{
	public static class Abort
	{
		// Single writer (the console-control thread, via Request) and a
		// single reader/consumer (the REPL main thread): the volatile
		// store/load pair is the whole synchronization story.
		private static volatile int s_requested;

		/// <summary>
		/// Raises the abort request. Called from the CancelKeyPress handler,
		/// which runs on a separate console-control thread - hence the
		/// volatile field (plain store; readers poll, writers never read).
		/// </summary>
		public static void Request()
		{
			s_requested = 1;
		}

		/// <summary>
		/// Reads and clears the flag; true if an abort was pending. Main-
		/// thread-only (editor / Repl null-read path), so a plain volatile
		/// read-then-clear is race-free. The party that handles the abort
		/// consumes it so the request cannot fire twice.
		/// </summary>
		public static bool Consume()
		{
			bool was = s_requested != 0;
			s_requested = 0;
			return was;
		}

		/// <summary>
		/// Poll-only check for hot loops (per-row scans). Reads without
		/// clearing - the flag is consumed once by whoever reports the abort.
		/// </summary>
		public static bool IsRequested
		{
			get { return s_requested != 0; }
		}
	}
}
