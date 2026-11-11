#!/bin/bash
# Parser/grammar error strings, byte-exact (D63: these are the locked contract).
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
