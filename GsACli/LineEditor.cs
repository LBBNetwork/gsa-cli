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
//     Each keystroke re-renders only what changed since the last draw:
//     the common prefix is left untouched, the cursor is positioned
//     explicitly at the first differing column, the new suffix is written
//     there and any leftover old tail beyond it erased - so typing touches
//     a single small region (and edits anywhere in the line land on the
//     right columns) while a pure caret move writes nothing at all, which
//     keeps the console from flickering. The buffer is addressed
//     relative to the screen row where the prompt first appeared
//     (Console.CursorTop); wrapping rides the linear cell stream plus
//     width-based cursor math. Terminals with a broken CursorTop fall
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
			if (!ProbeTerminal(out terminalWidth, out baseRow))
			{
				// CursorTop/Width broken (or a terminal we did not
				// expect): degrade to plain line reads rather than risk
				// scribbling over the banner above us.
				return ReadPlain(prompt);
			}

			StringBuilder text = new StringBuilder();
			int caret = 0;
			string lastRendered = string.Empty;

			// Draw the empty line up front so the prompt is visible before
			// any keypress (Redraw otherwise first runs after input).
			Redraw(text, caret, prompt, baseRow, terminalWidth, ref lastRendered);

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
						Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
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
					// Ctrl-C: abort - clear the line, keep the session. Consume
					// any flag the runtime's handler raised for this same keystroke
					// so it cannot leak into the next command.
					Abort.Consume();
					text.Length = 0;
					caret = 0;
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
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
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				if (ctrl == '\u0001')
				{
					caret = 0;
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				if (ctrl == '\u0005')
				{
					caret = text.Length;
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				if (ctrl == '\u0002')
				{
					if (caret > 0)
					{
						caret--;
					}
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				if (ctrl == '\u0006')
				{
					if (caret < text.Length)
					{
						caret++;
					}
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				switch (key.Key)
				{
					case ConsoleKey.LeftArrow:
						if (caret > 0)
						{
							caret--;
						}
						Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
						continue;

					case ConsoleKey.RightArrow:
						if (caret < text.Length)
						{
							caret++;
							Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
						}
						else
						{
							Bell(); // end of line - ring the bell (D49)
						}
						continue;

					case ConsoleKey.Home:
						caret = 0;
						Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
						continue;

					case ConsoleKey.End:
						caret = text.Length;
						Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
						continue;

					case ConsoleKey.Backspace:
						if (caret > 0)
						{
							text.Remove(caret - 1, 1);
							caret--;
							Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
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
						Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
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
					Redraw(text, caret, prompt, baseRow, width, ref lastRendered);
					continue;
				}

				// Anything else (arrows already handled, F-keys, etc.):
				// ignore and redraw nothing (state unchanged).
			}
		}

		/// <summary>
		/// Re-renders prompt + text with minimal writes: the common prefix
		/// is kept; the cursor is positioned explicitly at the first
		/// differing column (it may be parked mid-line from an earlier caret
		/// move); the new suffix is written there first, then any leftover
		/// old tail beyond it is erased. A pure caret move issues no writes
		/// at all - just a cursor position. That single-region update keeps
		/// typing flicker-free and mid-line edits on the right columns.
		/// </summary>
		private static void Redraw(
			StringBuilder text,
			int caret,
			string prompt,
			int baseRow,
			int width,
			ref string lastRendered)
		{
			string rendered = prompt + text.ToString();

			int common = 0;
			while (common < lastRendered.Length && common < rendered.Length
				&& lastRendered[common] == rendered[common])
			{
				common++;
			}

			bool changed = (common < lastRendered.Length)
				|| (common < rendered.Length);

			if (changed)
			{
				Console.SetCursorPosition(common % width, baseRow + (common / width));

				if (rendered.Length > common)
				{
					Console.Write(rendered.Substring(common));
				}
				if (lastRendered.Length > rendered.Length)
				{
					// Leftover old tail beyond the new suffix: erase it. The
					// cursor sits exactly where that leftover starts.
					Console.Write(new string(' ', lastRendered.Length - rendered.Length));
				}
				Console.Out.Flush(); // prompt must not sit in a buffered stream
			}

			int col = prompt.Length + caret;
			Console.SetCursorPosition(col % width, baseRow + (col / width));

			lastRendered = rendered;
		}

		/// <summary>
		/// Rings the terminal bell by sending ASCII BEL (0x07) to the
		/// console stream and flushing it; whether that becomes an audible
		/// beep or a visual flash is up to the host terminal's settings.
		/// </summary>
		private static void Bell()
		{
			Console.Out.Write('\u0007');
			Console.Out.Flush();
		}

		/// <summary>
		/// Terminal probe: width + the row where this line's prompt will
		/// start. Returns false when either is untrusted, in which case
		/// the caller drops to the plain path.
		/// </summary>
		private static bool ProbeTerminal(out int width, out int baseRow)
		{
			width = 0;
			baseRow = 0;

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
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
