// LineEditor - REPL input line (Phase 2, spec "REPL Editing (Decision 33)").
//
// Deliberate scope per the spec's editing section:
//   - single-line input only
//   - NO command history, NO Up-arrow recall, NO multi-line input
//   - minimal caret editing: left/right arrows (D48) plus Home/End and
//     the Ctrl-A/E, Ctrl-B/Ctrl-F equivalents, Backspace, Delete; Enter
//     submits; pasted text arrives as a fast run of character events,
//     which the insert path handles naturally
//
// Two input paths, chosen once per ReadLine call:
//   - redirected stdin (pipes, one-shot scripts, the automated harness):
//     plain Console.ReadLine. No cursor editing is possible through a
//     pipe anyway; EOF reads as null, which the caller treats as a clean
//     exit.
//   - interactive tty: a small caret editor over Console.ReadKey(true).
//     On every content change the whole line is rewritten from its start
//     cell and conhost does the wrapping/scrolling; immediately after the
//     write we read back Console.CursorTop/CursorLeft and derive all
//     further coordinates (line start, caret position) from that measured
//     ground truth. A pure caret move writes nothing - just a reposition
//     inside the region we last wrote. Every SetCursorPosition target is
//     therefore a cell conhost itself reported to us, which can never be
//     outside the screen buffer: this is what makes long lines safe on
//     finite buffers (Windows XP's 80x300 default), where predicting rows
//     from a frozen baseRow ran off the bottom of the buffer and threw
//     ArgumentOutOfRangeException. Terminals with a broken CursorTop fall
//     back to the plain path so a REPL never wedges.
//
// Control keys (interactive path):
//   Ctrl-C ('0x03')  aborts: clears the current line, stays at the prompt,
//                    and consumes any Abort flag raised by the runtime's
//                    CancelKeyPress handler for the same keystroke
//   Ctrl-D ('0x04')  or Ctrl-Z ('0x1A') drops the char under the caret;
//                    on an empty (or caret-at-end) line it reads as EOF
//                    and returns null
//   Left/Right arrows  caret one left / one right (D48); Up/Down are
//                      ignored - history stays out of scope
//   BEL ('0x07')       sent to the console when Right-arrow is pressed at
//                      end of line, or Backspace with nothing before the
//                      caret (D49); how it rings/flashes is up to the host
//   Ctrl-A / Ctrl-E  caret to line start / end (D33 convenience)
//   Ctrl-B / Ctrl-F  caret one left / one right (arrow equivalents)

using System;
using System.Reflection;
using System.Text;

namespace GsACli
{
	public static class LineEditor
	{
		/// <summary>
		/// Reads one REPL line. Returns null on EOF (Ctrl-D/Z at an empty
		/// line, or pipe end) - the caller exits.
		/// </summary>
		public static string ReadLine(string prompt)
		{
			if (IsRedirected())
			{
				return ReadPlain(prompt);
			}

			return ReadInteractive(prompt);
		}

		/// <summary>
		/// .NET-2.0-safe stdin redirect probe: the 2.0 API has no
		/// Console.IsInputRedirected, so try the later-version member via
		/// reflection and fall back to Environment.UserInteractive
		/// (which is in the 2.0 API and is false for piped input).
		/// Shared with Session's D35 overflow gate (non-interactive
		/// contexts skip it).
		/// </summary>
		public static bool IsRedirected()
		{
			MethodInfo probe = typeof(Console).GetMethod(
				"IsInputRedirected", BindingFlags.Public | BindingFlags.Static);

			if (probe != null)
			{
				object result = probe.Invoke(null, null);
				if (result is bool)
				{
					return (bool)result;
				}
			}

			return !Environment.UserInteractive;
		}

		/// <summary>Pipe/redirected path: write prompt, read full line, EOF -> null.</summary>
		private static string ReadPlain(string prompt)
		{
			Console.Write(prompt);
			Console.Out.Flush();
			return Console.ReadLine();
		}

		/// <summary>Interactive caret-editor path. See class remarks.</summary>
		private static string ReadInteractive(string prompt)
		{
			int terminalWidth;
			int baseRow;
			int baseCol;
			if (!ProbeTerminal(out terminalWidth, out baseRow, out baseCol))
			{
				// CursorTop/Width broken (or a terminal we did not
				// expect): degrade to plain line reads rather than risk
				// scribbling over the banner above us.
				return ReadPlain(prompt);
			}

			StringBuilder text = new StringBuilder();
			int caret = 0;

			DrawState st = new DrawState();
			st.StartRow = baseRow; // line begins where the cursor is now (measured)
			st.StartCol = baseCol;
			st.EndRow = baseRow;   // nothing written yet: end coincides with start
			st.EndCol = baseCol;

			// Draw the empty line up front so the prompt is visible before
			// any keypress (Redraw otherwise first runs after input).
			Redraw(text, caret, prompt, terminalWidth, st);

			while (true)
			{
				int width = terminalWidth;
				ConsoleKeyInfo key;

				try
				{
					key = Console.ReadKey(true);
				}
				catch (InvalidOperationException)
				{
					if (Abort.Consume())
					{
						// ^C unblocked the read mid-edit: that is an abort, not
						// a stream end - clear the line and re-prompt.
						Console.WriteLine();
						text.Length = 0;
						caret = 0;
						ReAnchor(st);
						Redraw(text, caret, prompt, width, st);
						continue;
					}

					// Input ended mid-edit (e.g. stdin hit EOF under a pty):
					// submit what is there - a partial line is the honest
					// reading of "the stream closed".
					Console.WriteLine();
					return text.ToString();
				}

				char ctrl = key.KeyChar;

				if (ctrl == '\u0003')
				{
					// Ctrl-C: abort - clear the line in place (prompt rewritten,
					// leftover tail blanked), keep the session. Consume any flag
					// the runtime's handler raised for this same keystroke so it
					// cannot leak into the next command.
					Abort.Consume();
					text.Length = 0;
					caret = 0;
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				if (ctrl == '\u0004' || ctrl == '\u001A')
				{
					// Ctrl-D / Ctrl-Z: drop char under caret; at end of an
					// exhausted line, EOF.
					if (caret < text.Length)
					{
						text.Remove(caret, 1);
					}
					else
					{
						Console.WriteLine();
						return null;
					}
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				if (ctrl == '\u0001')
				{
					caret = 0;
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				if (ctrl == '\u0005')
				{
					caret = text.Length;
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				if (ctrl == '\u0002')
				{
					if (caret > 0)
					{
						caret--;
					}
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				if (ctrl == '\u0006')
				{
					if (caret < text.Length)
					{
						caret++;
					}
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				switch (key.Key)
				{
					case ConsoleKey.LeftArrow:
						if (caret > 0)
						{
							caret--;
						}
						Redraw(text, caret, prompt, width, st);
						continue;

					case ConsoleKey.RightArrow:
						if (caret < text.Length)
						{
							caret++;
							Redraw(text, caret, prompt, width, st);
						}
						else
						{
							Bell(); // end of line - ring the bell (D49)
						}
						continue;

					case ConsoleKey.Home:
						caret = 0;
						Redraw(text, caret, prompt, width, st);
						continue;

					case ConsoleKey.End:
						caret = text.Length;
						Redraw(text, caret, prompt, width, st);
						continue;

					case ConsoleKey.Backspace:
						if (caret > 0)
						{
							text.Remove(caret - 1, 1);
							caret--;
							Redraw(text, caret, prompt, width, st);
						}
						else
						{
							Bell(); // nothing before the caret - ring the bell (D49)
						}
						continue;

					case ConsoleKey.Delete:
						if (caret < text.Length)
						{
							text.Remove(caret, 1);
						}
						Redraw(text, caret, prompt, width, st);
						continue;

					case ConsoleKey.Enter:
						Console.WriteLine();
						return text.ToString();
				}

				char printable = key.KeyChar;
				if (printable >= 0x20 && printable != 0x7F)
				{
					// Plain character (typed or pasted): insert at caret.
					text.Insert(caret, printable);
					caret++;
					Redraw(text, caret, prompt, width, st);
					continue;
				}

				// Anything else (arrows already handled, F-keys, etc.):
				// ignore and redraw nothing (state unchanged).
			}
		}

		/// <summary>
		/// Measured screen geometry of the line being edited. Start/End are
		/// cells conhost reported to us right after our last write, so they
		/// are always inside the current screen buffer; Written is how many
		/// cells from the start we have written (it only grows within a
		/// line's life - shrunken tails stay blanked, never unwritten). All
		/// cursor math in Redraw derives from these measurements instead of
		/// predicting rows off a frozen baseRow.
		/// </summary>
		private sealed class DrawState
		{
			public string LastRendered = string.Empty; // last prompt+text put on screen
			public int StartRow, StartCol;             // measured cell of the line's first char
			public int EndRow, EndCol;                 // measured cell one past the last written char
			public int Written;                        // cells written from the start (>= LastRendered.Length)
		}

		/// <summary>
		/// Re-anchors the draw state at the current cursor position (measured),
		/// used after a WriteLine that moved us to a fresh row: the old line's
		/// cells are abandoned and the next Redraw starts a new line here.
		/// </summary>
		private static void ReAnchor(DrawState st)
		{
			st.StartRow = Console.CursorTop;
			st.StartCol = Console.CursorLeft;
			st.EndRow = st.StartRow;
			st.EndCol = st.StartCol;
			st.Written = 0;
			st.LastRendered = string.Empty; // force the prompt to be (re)written here
		}

		/// <summary>
		/// Re-renders prompt + text and parks the caret, using only measured
		/// geometry. On a content change: position at the line's measured
		/// start cell (a real on-screen cell - positioning there can never
		/// leave the buffer), rewrite the whole line letting conhost wrap
		/// and scroll naturally, blank any leftover tail, then read back
		/// where the cursor actually landed and re-derive the start from
		/// that ground truth. A pure caret move issues no writes at all -
		/// just a reposition inside the region we last wrote. Every target
		/// cell therefore lies between two cells conhost itself reported,
		/// so SetCursorPosition cannot throw even on XP's finite 80x300
		/// screen buffer where long lines scroll the window.
		/// </summary>
		private static void Redraw(
			StringBuilder text,
			int caret,
			string prompt,
			int width,
			DrawState st)
		{
			string rendered = prompt + text.ToString();

			if (rendered != st.LastRendered)
			{
				Console.SetCursorPosition(st.StartCol, st.StartRow);

				if (rendered.Length > 0)
				{
					Console.Write(rendered);
				}
				if (st.Written > rendered.Length)
				{
					// Shrank: blank the leftover tail of what we wrote before.
					Console.Write(new string(' ', st.Written - rendered.Length));
				}
				Console.Out.Flush(); // prompt must not sit in a buffered stream

				int m = Math.Max(st.Written, rendered.Length); // cells now written from the start
				st.EndRow = Console.CursorTop;                 // measured: one past cell m-1
				st.EndCol = Console.CursorLeft;
				long endIdx = (long)st.EndRow * width + st.EndCol;
				long startIdx = endIdx - m;                     // linear cell stream, wrap included
				st.StartRow = (int)(startIdx / width);
				st.StartCol = (int)(startIdx % width);
				st.Written = m;

				st.LastRendered = rendered;
			}

			// Park the caret at offset i of the line. The measured end sits
			// one past cell Written-1, so offset i is (Written - i) cells
			// before it - always inside the region we just wrote, hence an
			// in-buffer cell SetCursorPosition will accept.
			int i = prompt.Length + caret;
			long endPos = (long)st.EndRow * width + st.EndCol;
			long target = endPos - (st.Written - i);
			Console.SetCursorPosition((int)(target % width), (int)(target / width));
		}

		/// <summary>
		/// Rings the terminal bell via Console.Beep (D49: how it rings or
		/// flashes is up to the host). Deliberately NOT a raw 0x07 byte on
		/// the output stream: that would be an unaccounted write into the
		/// editor's cell accounting, and whether XP's conhost consumes a
		/// cell for it is exactly the kind of assumption we no longer make.
		/// </summary>
		private static void Bell()
		{
			Console.Beep();
		}

		/// <summary>
		/// Terminal probe: width + the cell where this line's prompt will
		/// start. Returns false when any of it is untrusted, in which case
		/// the caller drops to the plain path.
		/// </summary>
		private static bool ProbeTerminal(out int width, out int baseRow, out int baseCol)
		{
			width = 0;
			baseRow = 0;
			baseCol = 0;

			try
			{
				int w = Console.WindowWidth;
				if (w < 1)
				{
					return false;
				}
				width = w;
				baseRow = Console.CursorTop;
				if (baseRow < 0)
				{
					return false;
				}
				baseCol = Console.CursorLeft;
				if (baseCol < 0)
				{
					return false;
				}
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
