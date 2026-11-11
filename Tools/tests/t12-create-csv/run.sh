#!/bin/bash
# CREATE CSV (D59): config mode no-path, explicit path in DB mode, refusal to
# overwrite. Byte-compare against the workspace template.csv.
set -u
cd "$SCRATCH"

"$GSA_EXE" < "$TESTDIR/input1.txt" > actual.out 2>&1
echo "EXIT=$?" >> exit.code

if cmp -s template.csv "$WS_ROOT/template.csv"; then echo "BYTE-COMPARE OK (no-path)" >> actual.out; else echo "BYTE-COMPARE FAIL (no-path)" >> actual.out; fi

cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input2.txt" >> actual.out 2>&1

if cmp -s mycsv.csv "$WS_ROOT/template.csv"; then echo "BYTE-COMPARE OK (db-mode)" >> actual.out; else echo "BYTE-COMPARE FAIL (db-mode)" >> actual.out; fi
