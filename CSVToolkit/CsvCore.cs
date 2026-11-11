using System;
using System.Collections.Generic;
using System.Text;

namespace C3.CSVToolkit
{
    /// <summary>Shared parse/format core (internal). Pure functions of their
    /// inputs - no shared state, so parallel workers over disjoint slices are
    /// safe and output is deterministic (CSV-11).</summary>
    internal static class CsvCore
    {
        // ---------- parsing (read side) ----------

        /// <summary>Parse one physical line into fields per CSV-4:
        /// comma-separated; double-quoted fields with "" escape; a quote is
        /// special only at the start of a field. Hard errors (line-numbered):
        /// unterminated quoted field, data after a closing quote, and a field
        /// count that differs from expectedCount.</summary>
        internal static string[] ParseLine(string line, int expectedCount, int lineNumber)
        {
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuotes = false;
            bool fieldStarted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cur.Append('"');
                            i++;
                            continue;
                        }
                        // closing quote: next char must be ',' or end-of-line
                        inQuotes = false;
                        if (i + 1 < line.Length && line[i + 1] != ',')
                            throw new CsvException("unexpected data after closing quote", lineNumber);
                        continue;
                    }
                    cur.Append(c);
                    continue;
                }
                if (!fieldStarted && c == '"')
                {
                    inQuotes = true;
                    fieldStarted = true;
                    continue;
                }
                if (c == ',')
                {
                    fields.Add(cur.ToString());
                    cur.Length = 0;
                    fieldStarted = false;
                    continue;
                }
                cur.Append(c);
                fieldStarted = true;
            }

            if (inQuotes)
                throw new CsvException("unterminated quoted field", lineNumber);

            fields.Add(cur.ToString());
            if (fields.Count != expectedCount)
                throw new CsvException(
                    string.Format("expected {0} fields, found {1}", expectedCount, fields.Count),
                    lineNumber);
            return fields.ToArray();
        }

        /// <summary>Parse the header line: no count check (it defines the count).</summary>
        internal static string[] ParseHeader(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuotes = false;
            bool fieldStarted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cur.Append('"');
                            i++;
                            continue;
                        }
                        inQuotes = false;
                        if (i + 1 < line.Length && line[i + 1] != ',')
                            throw new CsvException("unexpected data after closing quote", 1);
                        continue;
                    }
                    cur.Append(c);
                    continue;
                }
                if (!fieldStarted && c == '"')
                {
                    inQuotes = true;
                    fieldStarted = true;
                    continue;
                }
                if (c == ',')
                {
                    fields.Add(cur.ToString());
                    cur.Length = 0;
                    fieldStarted = false;
                    continue;
                }
                cur.Append(c);
                fieldStarted = true;
            }

            if (inQuotes)
                throw new CsvException("unterminated quoted field", 1);
            fields.Add(cur.ToString());
            return fields.ToArray();
        }

        // ---------- formatting (write side) ----------

        /// <summary>Format one value per CSV-4: quote only when the value
        /// contains a comma or a double quote; escape " as "". Values with CR
        /// or LF are rejected - one record per row is the contract, and a
        /// multi-line record would break line numbering on re-read.</summary>
        internal static string FormatValue(string value)
        {
            if (value == null) throw new ArgumentNullException("value");

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n')
                    throw new CsvException(
                        "field value contains a line terminator; one record per row is required", 0);
            }

            bool needQuote = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ',' || c == '"') { needQuote = true; break; }
            }
            if (!needQuote) return value;

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') sb.Append("\"\"");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Format a full row: values joined with commas, each quoted
        /// only when necessary.</summary>
        internal static string FormatRow(string[] values)
        {
            if (values == null) throw new ArgumentNullException("values");
            StringBuilder sb = new StringBuilder(64 + values.Length * 8);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FormatValue(values[i]));
            }
            return sb.ToString();
        }
    }
}
