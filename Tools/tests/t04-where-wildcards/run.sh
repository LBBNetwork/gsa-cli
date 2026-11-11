#!/bin/bash
# D7 wildcard contract (D63 regression pin): leading/middle '*', '?' = one char,
# case-insensitive values, multi-word quoted values (D64), and a
# guaranteed-negative pattern.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
