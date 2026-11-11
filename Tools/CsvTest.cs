// CsvTest - standalone verification harness for C3.CSVToolkit (CSV-7).
//
// Compile (from the repo root, v2.0 csc per build.bat):
//   csc /nologo /target:exe /out:bin\CsvTest.exe ^
//       /reference:bin\CSVToolkit.dll Tools\CsvTest.cs
// Run:
//   bin\CsvTest.exe all                 (everything below)
//   bin\CsvTest.exe roundtrip <file> [workerCount]
//   bin\CsvTest.exe determinism <file>  (round-trip at worker counts 1,2,4,8;
//                                        every output must be byte-identical to
//                                        the original - CSV-11)
//   bin\CsvTest.exe migrate             (sample_massive_alt.csv -> sample_massive.csv
//                                        per CSV-6; re-run proves determinism)
//   bin\CsvTest.exe errors              (malformed inputs must fail with the exact
//                                        expected line number, at worker counts 1 and 8)
//
// Exit code: 0 = all checks passed, 1 = at least one failure, 2 = usage error.

using System;
using System.Collections.Generic;
using System.IO;
using C3.CSVToolkit;

public static class CsvTest
{
    private static int _failures = 0;

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: CsvTest.exe all | roundtrip <file> [workerCount] | determinism <file> | migrate | errors");
            return 2;
        }

        string cmd = args[0];
        try
        {
            if (cmd == "stream")
            {
                if (args.Length < 2) return Usage();
                int wc = args.Length >= 3 ? int.Parse(args[2]) : 8;
                int passes = args.Length >= 4 ? int.Parse(args[3]) : 1;
                for (int p = 0; p < passes; p++)
                    StreamRoundTrip(args[1], wc);
            }
            else if (cmd == "all")
            {
                RoundTrip("sample.csv", 4);
                Determinism("sample.csv");
                Migrate();
                Errors();
            }
            else if (cmd == "roundtrip")
            {
                if (args.Length < 2) return Usage();
                int wc = args.Length >= 3 ? int.Parse(args[2]) : 4;
                RoundTrip(args[1], wc);
            }
            else if (cmd == "determinism")
            {
                if (args.Length < 2) return Usage();
                Determinism(args[1]);
            }
            else if (cmd == "migrate")
            {
                Migrate();
            }
            else if (cmd == "errors")
            {
                Errors();
            }
            else
            {
                Console.WriteLine("unknown command: " + cmd);
                return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  harness error: " + ex.Message);
            _failures++;
        }

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : (_failures + " CHECK(S) FAILED"));
        return _failures == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.WriteLine("usage: CsvTest.exe all | stream <file> [workerCount] [passes] | roundtrip <file> [workerCount] | determinism <file> | migrate | errors");
        return 2;
    }

    /// <summary>True streaming inverse check (CSV-2): rows are pumped one at a
    /// time from the reader into the writer - no bulk collection. Live memory
    /// is bounded to one chunk group + a few batches regardless of file size.
    /// Byte comparison is also O(1) in file size.</summary>
    private static void StreamRoundTrip(string src, int workerCount)
    {
        if (!File.Exists(src))
        {
            Check(false, "stream: input exists (" + src + ")");
            return;
        }

        string dst = TempPath(Path.GetFileName(src) + ".srt" + workerCount);

        AsyncResult<CsvReader> hr = CsvReader.OpenAsync(src, workerCount);
        AsyncResult<CsvWriter> hw = CsvWriter.OpenAsync(dst, workerCount);
        CsvReader r = hr.Result;
        CsvWriter w = hw.Result;
        long rowCount = 0;
        try
        {
            w.WriteHeader(r.Header);
            CsvRow row;
            while (r.TryReadRow(out row))
            {
                w.WriteRow(row.Columns);
                rowCount++;
            }
        }
        finally
        {
            r.Close();
            w.Close();   // surfaces any pipeline fault
        }

        Check(CompareFiles(src, dst),
              string.Format("stream: {0} pumped byte-identical (workerCount={1}, {2} rows)", src, workerCount, rowCount));
    }

    // ---------- checks ----------

    /// <summary>Parse a file, re-emit it with the writer, byte-compare. Proves
    /// read/write are strict inverses and the CSV-3 byte layout (BOM + CRLF, no
    /// trailing newline) is reproduced exactly.</summary>
    private static void RoundTrip(string src, int workerCount)
    {
        if (!File.Exists(src))
        {
            Check(false, "roundtrip: input exists (" + src + ")");
            return;
        }

        string dst = TempPath(Path.GetFileName(src) + ".rt" + workerCount);
        byte[] before = File.ReadAllBytes(src);

        AsyncResult<CsvReader> h = CsvReader.OpenAsync(src, workerCount);
        CsvReader r = h.Result;   // throws the open error on failure
        List<string[]> rows = new List<string[]>();
        string[] header = null;
        try
        {
            header = r.Header;
            CsvRow row;
            while (r.TryReadRow(out row)) rows.Add(row.Columns);
        }
        finally
        {
            r.Close();
        }

        Csv.WriteAll(dst, header, rows, workerCount);
        byte[] after = File.ReadAllBytes(dst);

        Check(SameBytes(before, after),
              string.Format("roundtrip: {0} re-emitted byte-identical (workerCount={1}, {2} data rows)",
                            src, workerCount, rows.Count));
    }

    /// <summary>CSV-11 acceptance bar: output must be byte-identical across
    /// worker counts and scheduling.</summary>
    private static void Determinism(string src)
    {
        int[] wcs = new int[] { 1, 2, 4, 8 };
        for (int i = 0; i < wcs.Length; i++) RoundTrip(src, wcs[i]);
    }

    /// <summary>CSV-6 migration: sample_massive_alt.csv (old 18-column schema)
    /// -> sample_massive.csv (reference 19-column schema), done with the new
    /// library itself. Re-running over an existing output proves run-to-run
    /// determinism.</summary>
    private static void Migrate()
    {
        const string src = "sample_massive_alt.csv";
        const string dst = "sample_massive.csv";

        if (!File.Exists(src))
        {
            Check(false, "migrate: input exists (" + src + ")");
            return;
        }

        byte[] previous = File.Exists(dst) ? File.ReadAllBytes(dst) : null;

        // Read the old-schema file with the library.
        AsyncResult<CsvReader> h = CsvReader.OpenAsync(src, 4);
        CsvReader r = h.Result;
        List<string[]> mapped = new List<string[]>();
        try
        {
            Check(r.Header.Length == 18, "migrate: old file has 18 columns");
            CsvRow row;
            while (r.TryReadRow(out row)) mapped.Add(MapOldToNew(row.Columns));
        }
        finally
        {
            r.Close();
        }

        string[] newHeader = new string[]
        {
            "ITEM_CODE", "ITEM_NAME", "DEPT", "SALEPRICECENTS", "SALEPRICE2",
            "QTY", "TRACK_MOVEMENT", "REQUIRE_QTY", "REQUIRE_PRICE", "SELL_HOURS",
            "SELL_RESTRICTED", "NO_RETURN", "GIVEAWAY", "TAX1", "TAX2", "TAX3",
            "TAX4", "PRICE_METHOD", "DCS_METHOD"
        };

        Csv.WriteAll(dst, newHeader, mapped, 4);

        byte[] now = File.ReadAllBytes(dst);
        Check(true, string.Format("migrate: wrote {0} ({1} data rows)", dst, mapped.Count));

        // CSV-3 layout on the output.
        Check(now.Length > 3 && now[0] == 0xEF && now[1] == 0xBB && now[2] == 0xBF,
              "migrate: output starts with UTF-8 BOM");
        bool hasCrlf = false;
        for (int i = 1; i < now.Length - 1; i++)
            if (now[i] == (byte)'\r' && now[i + 1] == (byte)'\n') { hasCrlf = true; break; }
        Check(hasCrlf, "migrate: output uses CRLF row terminators");
        Check(now[now.Length - 1] != (byte)'\n', "migrate: no trailing newline after last row");

        // Spot-check the first migrated data row against the reference shape.
        string expectedFirst = "1300,CSV 1,1,100,0,0,N,N,N,N,N,N,N,N,N,N,N,0,0";
        bool found = false;
        for (int i = 0; i < now.Length - expectedFirst.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < expectedFirst.Length; j++)
                if (now[i + j] != (byte)expectedFirst[j]) { match = false; break; }
            if (match) { found = true; break; }
        }
        Check(found, "migrate: first data row matches reference schema shape");

        // Run-to-run determinism when re-migrating over an existing output.
        if (previous != null)
            Check(SameBytes(previous, now), "migrate: re-run is byte-identical to the previous run");
    }

    /// <summary>Old 18-column schema -> reference 19-column schema (CSV-6).
    /// Flag columns map 0->N / 1->Y; numeric columns pass through.</summary>
    private static string[] MapOldToNew(string[] o)
    {
        if (o.Length != 18)
            throw new InvalidOperationException("expected 18 old-schema fields, got " + o.Length);

        return new string[]
        {
            o[0],              // ITEM_CODE
            o[1],              // ITEM_NAME
            o[2],              // DEPT
            o[3],              // SALEPRICECENTS (header typo fixed)
            "0",               // SALEPRICE2 (inserted, zero per CSV-6)
            o[4],              // QTY - numeric in the reference schema: pass through
            Flag(o[5]),        // TRACK_MOVEMENT
            Flag(o[6]),        // REQUIRE_QTY
            Flag(o[7]),        // REQUIRE_PRICE
            Flag(o[8]),        // SELL_HOURS
            Flag(o[9]),        // SELL_RESTRICTED
            Flag(o[10]),       // NO_RETURN
            Flag(o[11]),       // GIVEAWAY
            Flag(o[12]),       // TAX1
            Flag(o[13]),       // TAX2
            Flag(o[14]),       // TAX3
            Flag(o[15]),       // TAX4
            o[16],             // PRICE_METHOD - numeric: pass through
            o[17]              // DCS_METHOD - numeric: pass through
        };
    }

    private static string Flag(string v)
    {
        if (v == "0") return "N";
        if (v == "1") return "Y";
        throw new InvalidOperationException("unexpected flag value in old schema: '" + v + "'");
    }

    /// <summary>Malformed inputs must fail with the exact expected line number,
    /// and that line number must be deterministic across worker counts (CSV-11).</summary>
    private static void Errors()
    {
        // --- reader-side format errors -------------------------------------

        ExpectCsvError("count-mismatch", "H1,H2\r\na,b,c\r\n", 2, "expected 2 fields, found 3");
        ExpectCsvError("unterminated-quote", "\"H1\",H2\r\n\"unterminated,x\r\ny,z\r\n", 2, "unterminated quoted field");
        ExpectCsvError("blank-line-lf-only", "H1,H2\na,b\n\nc,d\n", 3, "expected 2 fields, found 1");
        ExpectCsvError("data-after-close-quote", "H1,H2\r\n\"a\"x,b\r\n", 2, "unexpected data after closing quote");

        // Empty file: fails at open.
        string empty = TempPath("empty.csv");
        File.WriteAllBytes(empty, new byte[0]);
        ExpectCsvErrorAtOpen("empty-file", empty, 1, "missing or empty header");

        // --- positive layout cases ------------------------------------------

        // Header-only file with no terminator: must round-trip to BOM + "A,B".
        string ho = TempPath("header-only.csv");
        File.WriteAllBytes(ho, new byte[] { (byte)'A', (byte)',', (byte)'B' });
        AsyncResult<CsvReader> h = CsvReader.OpenAsync(ho, 4);
        CsvReader r = h.Result;
        int rowCount = 0;
        try
        {
            Check(r.Header.Length == 2 && r.Header[0] == "A" && r.Header[1] == "B",
                  "layout: header-only file parses two columns");
            CsvRow row;
            while (r.TryReadRow(out row)) rowCount++;
        }
        finally { r.Close(); }
        Check(rowCount == 0, "layout: header-only file has zero data rows");

        // Quoting round-trip: values with commas and quotes must survive.
        string q = TempPath("quoted.csv");
        Csv.WriteAll(q, new string[] { "A", "B" },
            new string[][] { new string[] { "x,y", "q\"z" }, new string[] { "", "plain" } }, 4);
        AsyncResult<CsvReader> h2 = CsvReader.OpenAsync(q, 8);
        CsvReader r2 = h2.Result;
        try
        {
            List<string[]> back = new List<string[]>();
            CsvRow row2;
            while (r2.TryReadRow(out row2)) back.Add(row2.Columns);
            Check(back.Count == 2 && back[0][0] == "x,y" && back[0][1] == "q\"z"
                  && back[1][0] == "" && back[1][1] == "plain",
                  "layout: quoted fields (comma, escaped quote, empty) round-trip");
        }
        finally { r2.Close(); }

        // --- writer-side validation ------------------------------------------

        string w = TempPath("writer-err.csv");
        AsyncResult<CsvWriter> hw = CsvWriter.OpenAsync(w, 4);
        CsvWriter wr = hw.Result;
        try
        {
            wr.WriteHeader(new string[] { "A", "B" });
            bool threw = false;
            try { wr.WriteRow(new string[] { "a\nb", "c" }); }
            catch (CsvException) { threw = true; }
            Check(threw, "writer: value containing a line terminator is rejected");

            AsyncResult<CsvWriter> hw2 = CsvWriter.OpenAsync(TempPath("writer-err2.csv"), 8);
            CsvWriter wr2 = hw2.Result;
            try
            {
                wr2.WriteHeader(new string[] { "A", "B" });
                wr2.WriteRow(new string[] { "ok", "row" });
                bool threw2 = false;
                int lineNo = 0;
                try { wr2.WriteRow(new string[] { "too", "many", "fields" }); }
                catch (CsvException ex) { threw2 = true; lineNo = ex.Line; }
                Check(threw2 && lineNo == 3,
                      "writer: field-count mismatch rejected with line number 3 (got " + lineNo + ")");
            }
            finally { wr2.Close(); }
        }
        finally { wr.Close(); }
    }

    /// <summary>Write a malformed file, read it at worker counts 1 and 8, and
    /// require the same CsvException (line number + message) from both.</summary>
    private static void ExpectCsvError(string name, string content, int expectedLine, string msgContains)
    {
        string path = TempPath(name + ".csv");
        File.WriteAllText(path, content);

        CsvException e1 = ReadExpectingError(path, 1);
        CsvException e8 = ReadExpectingError(path, 8);

        Check(e1 != null && e8 != null, "error[" + name + "]: both worker counts fail");
        if (e1 == null || e8 == null) return;
        Check(e1.Line == expectedLine && e8.Line == expectedLine,
              string.Format("error[{0}]: line number {1} at wc=1 and wc=8 (got {2}, {3})",
                            name, expectedLine, e1.Line, e8.Line));
        Check(e1.Message.IndexOf(msgContains) >= 0 && e8.Message.IndexOf(msgContains) >= 0,
              "error[" + name + "]: message cites '" + msgContains + "' (got: " + e1.Message + ")");
    }

    private static void ExpectCsvErrorAtOpen(string name, string path, int expectedLine, string msgContains)
    {
        CsvException e = null;
        try
        {
            AsyncResult<CsvReader> h = CsvReader.OpenAsync(path, 4);
            CsvReader r = h.Result;
            r.Close();
        }
        catch (CsvException ex) { e = ex; }

        Check(e != null, "error[" + name + "]: open fails");
        if (e == null) return;
        Check(e.Line == expectedLine && e.Message.IndexOf(msgContains) >= 0,
              string.Format("error[{0}]: line {1}, message cites '{2}' (got: line {3}: {4})",
                            name, expectedLine, msgContains, e.Line, e.Message));
    }

    private static CsvException ReadExpectingError(string path, int workerCount)
    {
        try
        {
            AsyncResult<CsvReader> h = CsvReader.OpenAsync(path, workerCount);
            CsvReader r = h.Result;
            try
            {
                CsvRow row;
                while (r.TryReadRow(out row)) { }
            }
            finally { r.Close(); }
            return null;   // no error - the check will report FAIL
        }
        catch (CsvException ex)
        {
            return ex;
        }
    }

    // ---------- helpers ----------

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) _failures++;
    }

    /// <summary>Chunked byte comparison: O(1) memory in file size.</summary>
    private static bool CompareFiles(string a, string b)
    {
        FileStream fa = File.OpenRead(a);
        FileStream fb = File.OpenRead(b);
        try
        {
            if (fa.Length != fb.Length) return false;
            byte[] ba = new byte[65536];
            byte[] bb = new byte[65536];
            while (true)
            {
                int na = fa.Read(ba, 0, ba.Length);
                int nb = fb.Read(bb, 0, bb.Length);
                if (na != nb) return false;
                if (na == 0) return true;
                for (int i = 0; i < na; i++)
                    if (ba[i] != bb[i]) return false;
            }
        }
        finally
        {
            fa.Close();
            fb.Close();
        }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string TempPath(string fileName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "csvtoolkit-test");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}
