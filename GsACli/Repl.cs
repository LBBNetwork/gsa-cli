// Repl - the prompt loop (Phase 2).
//
// Loop shape (spec "Re-entrancy (Decision 31)" + prompts):
//   - config mode prompts "config> "
//   - database mode prompts "gsa [(basename)]> ", the basename being the
//     loaded .DAT's name exactly as the operator gave it (D30/D34)
//   - the prompt is the only place mode is announced (D34: LOAD and
//     UNLOAD do not print a "Entering/Leaving database mode" line)
//
//   - input goes through LineEditor (pipe -> plain reads, tty -> caret
//     editor with the D33 scope)
//   - a null read (EOF) exits the session cleanly, no extra output -
//     the automated harness relies on EOF ending the loop; unless ^C just
//     unblocked that read as an abort (Abort.Consume true), in which case
//     we re-prompt instead of exiting
//   - an empty line re-prompts silently (spec's example shows the
//     config> blank-line re-prompt)
//   - BYE/QUIT end the loop via Session.Execute returning false

using System;
using System.IO;

namespace GsACli
{
	public static class Repl
	{
		/// <summary>
		/// Runs the interactive session to completion and returns the
		/// process exit code (0 - clean exit via BYE/QUIT/EOF; the error
		/// path that exits nonzero is handled before reaching the loop).
		/// </summary>
		public static int Run(Session session)
		{
			while (true)
			{
				string prompt;
				if (session.DatabaseLoaded)
				{
					prompt = "gsa [(" + Path.GetFileName(session.DatabaseAsGiven) + ")]> ";
				}
				else
				{
					prompt = "config> ";
				}

				string line = LineEditor.ReadLine(prompt);

				if (line == null)
				{
					if (Abort.Consume())
					{
						// ^C at the prompt unblocked the read as an EOF-ish
						// result: that is an abort, not a stream end - re-prompt.
						continue;
					}

					// True EOF: clean exit, silent (harness contract).
					break;
				}

				if (line.Length == 0)
				{
					continue;
				}

				if (!session.Execute(line))
				{
					break; // BYE / QUIT
				}

				// Safety sweep: Session's hot loops only poll the abort flag;
				// whoever reports "Aborted." leaves it set, and this clears any
				// residue (including a ^C that landed after the command already
				// finished) so the next line starts clean.
				Abort.Consume();
			}

			session.Close();
			return 0;
		}
	}
}
