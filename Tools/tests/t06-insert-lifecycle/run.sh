#!/bin/bash
# INSERT validation matrix + staged-change rollback on EOF (no COMMIT).
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
"$GSA_EXE" t.DAT < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
# second process: the staged INSERT must NOT be durable (EOF rolled it back)
printf 'SELECT 9777\nBYE\n' | "$GSA_EXE" t.DAT >> actual.out 2>&1
