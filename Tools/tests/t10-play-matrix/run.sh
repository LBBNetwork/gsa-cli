#!/bin/bash
# PLAY matrix (D16/D42, D61): LOUD success + durability, pre-parse failures
# (missing terminal COMMIT, unknown verb, BYE in script), execution failure with
# all-or-nothing rollback, and one-shot -play both ways.
set -u
cd "$SCRATCH"
for f in s_ok.scr s_nocommit.scr s_unknown.scr s_bye.scr s_fail.scr; do cp "$TESTDIR/$f" .; done
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT

"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" >> exit.code

# one-shot: silent success (fresh copy so the script's INSERT is a new code)
cp "$WS_ROOT/GSA EALITEMR.DAT" t3.DAT
"$GSA_EXE" t3.DAT -play s_ok.scr >> actual.out 2>&1
echo "ONE-SHOT-OK-EXIT=$?" >> exit.code

# one-shot: execution failure -> non-zero exit, nothing durable
cp "$WS_ROOT/GSA EALITEMR.DAT" t4.DAT
"$GSA_EXE" t4.DAT -play s_fail.scr >> actual.out 2>&1
echo "ONE-SHOT-FAIL-EXIT=$?" >> exit.code

# durability: 9600 must exist (committed via PLAY), 9602 must not (rolled back)
printf 'SELECT 9600\nSELECT 9602\nBYE\n' | "$GSA_EXE" t.DAT >> actual.out 2>&1
