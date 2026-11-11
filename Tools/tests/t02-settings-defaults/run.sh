#!/bin/bash
# Config mode on a fresh workspace: the default settings block via SHOW
# CONFIG, plus all three accepted CONFIG pair forms - KEY=VALUE,
# "KEY= value", and "KEY = VALUE" (Phase 2 deferral, pinned here).
#
# Runs a LOCAL copy of the exe in $SCRATCH (t13 pattern): per D40 the
# settings file lives next to the program file, so writing CONFIG values
# through bin/gsa-cli.exe would leak into every other test that shares it.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/bin/gsa-cli.exe" "$WS_ROOT/bin/EalItemr.dll" "$WS_ROOT/bin/Xml2.dll" "$WS_ROOT/bin/CSVToolkit.dll" .

# first run bootstraps settings.xml from the embedded template (D40)
printf 'BYE\n' | ./gsa-cli.exe > /dev/null 2>&1

./gsa-cli.exe < "$TESTDIR/input.txt" > actual.out 2>&1
echo "EXIT=$?" > exit.code
