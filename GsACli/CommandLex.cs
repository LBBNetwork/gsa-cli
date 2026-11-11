// CommandLex - command-grammar lexing (Phase 2, Decisions 11/12).
//
// Rules implemented here:
//   - a line is one command (D11a); nothing in this layer splits lines
//   - a single trailing ';' is accepted, optional (D11c): one is
//     stripped after trailing whitespace; a second one is *not* a
//     command terminator - it becomes a token and the grammar layer
//     rejects it (e.g. "BYE;;" -> BYE with a stray argument)
//   - whitespace separates tokens
//   - a value may be wrapped in single OR double quotes (either kind,
//     D12); quotes are peeled off and the content is the token value
//   - quoting is never mixed inside one value (D12): a different quote
//     character inside a quoted value is a syntax error
//   - lexing preserves case: keywords are compared case-insensitively
//     by the dispatcher (D12); operator spellings survive for messages
//
// Empty token ("''" or '\"\"'') is a legal token: it is the empty
// value, and the grammar layer decides what an empty value means for
// each command.

using System;
using System.Collections.Generic;

namespace GsACli
{
	/// <summary>
	/// Thrown by <see cref="CommandLex.Lex"/> when the line violates the
	/// quoting rules. Carries the operator-facing message verbatim
	/// (Validation Error prefix per SPECIFICATION.md).
	/// </summary>
	public class CommandSyntaxException : Exception
	{
		public CommandSyntaxException(string message)
			: base(message)
		{
		}
	}

	public static class CommandLex
	{
		/// <summary>
		/// Splits a raw input line into token values. Never returns null
		/// (an empty line yields zero tokens).
		/// </summary>
		/// <exception cref="CommandSyntaxException">
		/// Mixed or unterminated quoting in a value.
		/// </exception>
		public static string[] Lex(string line)
		{
			int[] starts;
			return Lex(line, out starts);
		}

		/// <summary>
		/// Same as <see cref="Lex(string)"/> but also reports each token's start
		/// offset in the ORIGINAL line (trailing whitespace and a trailing ';'
		/// excluded). Phase 4 uses this to re-scan raw clause text - e.g. the SET
		/// or VALUES region of an UPDATE/INSERT - where quoted values may contain
		/// spaces that the whitespace tokenizer cannot see.
		/// </summary>
		public static string[] Lex(string line, out int[] tokenStarts)
		{
			if (line == null)
			{
				line = string.Empty;
			}

			// One optional trailing command terminator (D11c). Work on an effective
			// length instead of a trimmed copy so token offsets stay valid in the
			// original line.
			int length = line.Length;
			while (length > 0 && char.IsWhiteSpace(line[length - 1]))
			{
				length--;
			}
			if (length > 0 && line[length - 1] == ';')
			{
				length--;
				while (length > 0 && char.IsWhiteSpace(line[length - 1]))
				{
					length--;
				}
			}

			List<string> tokens = new List<string>();
			List<int> starts = new List<int>();
			int i = 0;

			while (i < length)
			{
				char c = line[i];

				if (char.IsWhiteSpace(c))
				{
					i++;
					continue;
				}

				starts.Add(i);

				if (c == '\'' || c == '"')
				{
					char quote = c;
					int start = i + 1;
					int j = start;
					bool closed = false;

					while (j < length)
					{
						char d = line[j];
						if (d == quote)
						{
							closed = true;
							break;
						}
						if (d == OtherQuote(quote))
						{
							throw new CommandSyntaxException(
								"Validation Error: mixed quoting in value '" +
								line.Substring(start, j - start + 1) + "'.");
						}
						j++;
					}

					if (!closed)
					{
						throw new CommandSyntaxException(
							"Validation Error: unterminated quote in value '" +
							line.Substring(start) + "'.");
					}

					tokens.Add(line.Substring(start, j - start));
					i = j + 1;
					continue;
				}

				int p = i;

				// D64: a quote in the MIDDLE of an unquoted token starts a quoted
				// value that may contain spaces (e.g. FIELD='VALUE WITH SPACE').
				// If the quoted region is well-formed (matching close, no mixed
				// quotes), merge it into this one token with the raw text kept -
				// grammar layers such as WHERE's UnquoteValue then peel the quotes.
				// If it is NOT well-formed, fall through to the plain scan below:
				// every input that lexed before still lexes exactly as before.
				int q = -1;
				while (i < length && !char.IsWhiteSpace(line[i]))
				{
					char d = line[i];
					if ((d == '\'' || d == '"') && i > p)
					{
						q = i;
						break;
					}
					i++;
				}

				if (q >= 0)
				{
					char quote = line[q];
					int j = q + 1;
					bool closed = false;
					while (j < length)
					{
						char e2 = line[j];
						if (e2 == quote)
						{
							closed = true;
							break;
						}
						if (e2 == OtherQuote(quote))
						{
							q = -1; // mixed quoting: keep the old token shape
							break;
						}
						j++;
					}
					if (!closed)
					{
						q = -1; // unterminated: keep the old token shape
					}
				}

				if (q >= 0)
				{
					// Re-scan from q to find the closing quote again (the scan
					// above stopped at it; j is one past... no - j IS the close).
					int j = q + 1;
					while (line[j] != line[q])
					{
						j++;
					}
					tokens.Add(line.Substring(p, j - p + 1));
					i = j + 1;
					continue;
				}

				// Plain unquoted token: either no mid-token quote was found at
				// all, or the quoted region was malformed and we keep the old
				// token shape. i is already positioned correctly in both cases.
				while (i < length && !char.IsWhiteSpace(line[i]))
				{
					i++;
				}
				tokens.Add(line.Substring(p, i - p));
			}

			tokenStarts = starts.ToArray();
			return tokens.ToArray();
		}

		/// <summary>
		/// Splits a raw clause body on commas, respecting single/double quotes
		/// (a comma inside a quoted value is content, not a separator). Each piece
		/// is trimmed and one layer of surrounding parentheses is stripped - the
		/// VALUES (...) / INTO (...) shapes. Empty pieces are preserved: the
		/// grammar layer decides what an empty value means.
		/// </summary>
		public static List<string> ScanClause(string s)
		{
			List<string> out_ = new List<string>();
			if (s == null || s.Length == 0)
			{
				return out_;
			}

			char quote = '\0'; // active quote character, or none
			int start = 0;

			for (int i = 0; ; i++)
			{
				bool end = (i >= s.Length);

				if (!end)
				{
					char c = s[i];
					if (quote == '\0')
					{
						if (c == '\'' || c == '"')
						{
							quote = c; // enter a quoted region
						}
						else if (c == ',')
						{
							out_.Add(CleanPiece(s, start, i));
							start = i + 1;
						}
					}
					else if (c == quote)
					{
						quote = '\0'; // close the quoted region
					}
				}

				if (end)
				{
					out_.Add(CleanPiece(s, start, s.Length));
					break;
				}
			}

			return out_;
		}

		/// <summary>Trim a clause piece and strip one layer of surrounding parens.</summary>
		private static string CleanPiece(string s, int from, int to)
		{
			while (from < to && char.IsWhiteSpace(s[from]))
			{
				from++;
			}
			while (to > from && char.IsWhiteSpace(s[to - 1]))
			{
				to--;
			}
			if (from < to && s[from] == '(')
			{
				from++;
				while (from < to && char.IsWhiteSpace(s[from]))
				{
					from++;
				}
			}
			if (to > from && s[to - 1] == ')')
			{
				to--;
				while (to > from && char.IsWhiteSpace(s[to - 1]))
				{
					to--;
				}
			}

			return s.Substring(from, to - from);
		}

		/// <summary>The other quote character, for mixed-quote detection.</summary>
		private static char OtherQuote(char quote)
		{
			return (quote == '\'') ? '"' : '\'';
		}
	}
}
