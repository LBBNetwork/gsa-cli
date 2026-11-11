#!/bin/bash
# UPDATE/SELECT/DROP/COMMIT lifecycle + durability across processes.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
# second process: DROP must be durable, price change gone with the record
printf 'SELECT 1009\nBYE\n' | "$GSA_EXE" t.DAT >> actual.out 2>&1
