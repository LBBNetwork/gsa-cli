#!/bin/bash
# One-shot -verify (D27/D31): success block byte-exact, plus two deterministic
# corruption failures (header smashed; data-region byte flipped). Exit codes pinned.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT

"$GSA_EXE" -verify t.DAT > actual.out 2>&1
echo "VERIFY-OK-EXIT=$?" >> exit.code

# corruption A: header bytes smashed -> open failure, non-zero exit
{ head -c 8 t.DAT; printf '\xff\xff\xff\xff'; tail -c +9 t.DAT; } > badA.DAT
"$GSA_EXE" -verify badA.DAT >> actual.out 2>&1
echo "VERIFY-BADA-EXIT=$?" >> exit.code

# corruption B: two data-region bytes flipped (size preserved) -> record-level failure
OFF=4096
{ head -c $OFF t.DAT; printf '\xff\xff'; tail -c +$((OFF+3)) t.DAT; } > badB.DAT
"$GSA_EXE" -verify badB.DAT >> actual.out 2>&1
echo "VERIFY-BADB-EXIT=$?" >> exit.code
