#!/bin/bash
# D58 no-gate pin (SLOW): SELECT renders ALL matched rows unconditionally.
# A full `SELECT *` on p1-600k.DAT must emit every one of its 600001 records -
# no overflow gate, no prompt, no truncation. Skipped unless -IncludeSlow.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/p1-600k.DAT" t.DAT
printf 'SELECT *\nBYE\n' | "$GSA_EXE" t.DAT > actual.out 2>&1
echo "EXIT=$?" > exit.code
ROWS=$(grep -c '^[0-9]' actual.out)
echo "DATA-ROWS=$ROWS (expect 600001)" >> actual.out
