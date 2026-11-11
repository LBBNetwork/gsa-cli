#!/bin/bash
# Banner + no-file screen + clean BYE exit (config mode, no database).
set -u
cd "$SCRATCH"
"$GSA_EXE" < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
