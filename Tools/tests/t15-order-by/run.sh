#!/bin/bash
# ORDER BY clause (Decision 67): numeric vs ordinal code order, DESC + ASC mix,
# WHERE + VALUES + ORDER BY combined, stable ties, presentation-only guarantee
# (scan order intact after a sorted query), and the four locked error strings.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
