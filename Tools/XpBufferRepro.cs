// XpBufferRepro - A/B proof of the XP long-line crash and its fix.
//
// Runs in its own console window (launch with: start /WAIT XpBufferRepro.exe).
// Shrinks the screen buffer to an 80x30 XP-like finite size, scrolls the
// prompt down to the last row of that buffer, then types a long line two
// ways:
//   Phase A - the OLD LineEditor.Redraw math (frozen baseRow + predicted
//             rows). Expected: ArgumentOutOfRangeException with the exact
//             message seen on XP.
//   Phase B - the NEW measured-geometry Redraw math. Expected: survives,
//             cursor always in-buffer.
// Findings are printed and also written to repro-result.txt next to the EXE.

using System;
using System.IO;
using System.Text;

public static class XpBufferRepro
{
	private static string notes = string.Empty;

	/// <summary>
	/// Shrink this console to an XP-like finite screen buffer (80x30) and
	/// scroll the cursor down to its last row - the position where the old
	/// Redraw math runs off the bottom of the buffer. Returns the buffer
	/// height actually in effect.
	/// </summary>
	private static int SetupBuffer()
	{
		try
		{
			Console.SetWindowSize(80, 10); // window first so it fits the new buffer
		}
		catch (Exception e)
		{
			notes += "SetWindowSize failed: " + e.Message + "\n";
		}
		try
		{
			Console.SetBufferSize(80, 30); // XP-like finite buffer; cursor resets to (0,0)
		}
		catch (Exception e)
		{
			notes += "SetBufferSize failed: " + e.Message + "\n";
		}

		int h = Console.BufferHeight;
		// Scroll until the cursor sits on the last row of the buffer.
		while (Console.CursorTop < h - 1)
		{
			Console.WriteLine("filler line");
		}
		return h;
	}

	/// <summary>
	/// Phase A: types a long line with the PRE-FIX LineEditor.Redraw math -
	/// frozen baseRow, diff against lastRendered, cursor positioned at
	/// (common % width, baseRow + common / width). With the prompt on the
	/// last row of a finite buffer this must throw the exact XP exception.
	/// </summary>
	private static string PhaseOld(int width, int baseRow)
	{
		string prompt = "config> ";
		StringBuilder text = new StringBuilder();
		string lastRendered = string.Empty;

		for (int n = 1; ; n++)
		{
			text.Append('a');
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
					Console.Write(new string(' ', lastRendered.Length - rendered.Length));
				}
				Console.Out.Flush();
			}

			int col = prompt.Length + n; // caret at end of line
			Console.SetCursorPosition(col % width, baseRow + (col / width));

			lastRendered = rendered;

			if (n >= 500)
			{
				return "survived 500 chars";
			}
		}
	}

	/// <summary>
	/// Phase B: types the same long line with the FIXED measured-geometry
	/// math - rewrite from the measured start cell, let conhost wrap/scroll,
	/// read back where the cursor landed, derive every coordinate from that.
	/// Must survive 500 chars at the bottom of a finite buffer.
	/// </summary>
	private static string PhaseNew(int width)
	{
		string prompt = "config> ";
		StringBuilder text = new StringBuilder();

		int startRow = Console.CursorTop; // measured: line begins where we are
		int startCol = Console.CursorLeft;
		int endRow = startRow;            // nothing written yet
		int endCol = startCol;
		int written = 0;
		string lastRendered = string.Empty;

		for (int n = 1; n <= 500; n++)
		{
			text.Append('a');
			string rendered = prompt + text.ToString();

			if (rendered != lastRendered)
			{
				Console.SetCursorPosition(startCol, startRow);

				if (rendered.Length > 0)
				{
					Console.Write(rendered);
				}
				if (written > rendered.Length)
				{
					Console.Write(new string(' ', written - rendered.Length));
				}
				Console.Out.Flush();

				int m = Math.Max(written, rendered.Length);
				endRow = Console.CursorTop; // measured: one past cell m-1
				endCol = Console.CursorLeft;
				long endIdx = (long)endRow * width + endCol;
				long startIdx = endIdx - m;  // linear cell stream, wrap included
				startRow = (int)(startIdx / width);
				startCol = (int)(startIdx % width);
				written = m;

				lastRendered = rendered;
			}

			// Caret at end of line: offset i is (written - i) cells before the measured end.
			int i = prompt.Length + n;
			long endPos = (long)endRow * width + endCol;
			long target = endPos - (written - i);
			Console.SetCursorPosition((int)(target % width), (int)(target / width));
		}

		return "survived 500 chars, final cursor at (" + Console.CursorLeft + "," + Console.CursorTop + ")";
	}

	/// <summary>
	/// Runs Phase A (expect the XP crash) then Phase B (expect survival),
	/// printing and saving the verdict. Exit code 0 = expected outcome.
	/// </summary>
	public static int Main()
	{
		int h = SetupBuffer();
		int width = Console.WindowWidth;
		string result = "buffer height in effect: " + h
			+ ", window width: " + width + "\n" + notes;

		bool oldCrashed = false;
		try
		{
			result += "Phase A (old math): UNEXPECTEDLY SURVIVED - "
				+ PhaseOld(width, Console.CursorTop) + "\n";
		}
		catch (ArgumentOutOfRangeException e)
		{
			oldCrashed = true;
			result += "Phase A (old math): REPRODUCED -> "
				+ e.GetType().Name + ": " + e.Message + "\n";
		}

		SetupBuffer(); // fresh bottom-of-buffer position for Phase B
		bool newSurvived = false;
		try
		{
			result += "Phase B (new math): PASS -> " + PhaseNew(width) + "\n";
			newSurvived = true;
		}
		catch (Exception e)
		{
			result += "Phase B (new math): FAIL -> "
				+ e.GetType().Name + ": " + e.Message + "\n";
		}

		Console.WriteLine();
		Console.Write(result);
		try
		{
			File.WriteAllText("repro-result.txt", result);
		}
		catch (Exception)
		{
			// best effort - the console output above is authoritative
		}

		return (oldCrashed && newSurvived) ? 0 : 1;
	}
}
