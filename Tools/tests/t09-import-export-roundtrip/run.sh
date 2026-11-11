#!/bin/bash
# EXPORT (fresh, refusal, OVERWRITE) + IMPORT round-trip restoring a dropped record.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT

# phase 1: export matrix on the full DB
"$GSA_EXE" t.DAT < "$TESTDIR/input1.txt" > actual.out 2>&1
echo "EXIT=$?" >> exit.code

# phase 2: round-trip - drop record 1009 from a fresh copy, import it back
cp "$WS_ROOT/GSA EALITEMR.DAT" t2.DAT
"$GSA_EXE" t2.DAT < "$TESTDIR/input2.txt" >> actual.out 2>&1

# phase 3: the dropped record must be back with its original values
printf 'SELECT 1009\nBYE\n' | "$GSA_EXE" t2.DAT >> actual.out 2>&1
