#!/bin/bash
# D70: upc-a-nc token - config acceptance, validation message, and the
# keyword / zero-substitution paths in database mode. Runs against an isolated
# deploy copy so CONFIG's settings.xml write never lands in bin/.
set -u
cd "$SCRATCH"

mkdir ncapp
cp "$WS_ROOT/bin/gsa-cli.exe" "$WS_ROOT/bin/EalItemr.dll" "$WS_ROOT/bin/Xml2.dll" "$WS_ROOT/bin/CSVToolkit.dll" ncapp/

# Phase 1: config mode - accept the token, show it back, reject a bogus value.
cd ncapp
printf 'CONFIG CODE_METHOD=upc-a-nc\nSHOW CONFIG\nCONFIG CODE_METHOD=bogus\nBYE\n' | ./gsa-cli.exe > ../actual.out 2>&1
echo "EXIT=$?" >> ../actual.out

# Phase 2: database mode under CODE_METHOD=upc-a-nc. Zero substitution and the
# explicit keyword both generate; codes are deterministic (scan from 10^10,
# skipping bases whose 11-digit or full 12-digit form is already in use).
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
printf 'INSERT VALUES (0, ZERO A, 1, 100)\nINSERT VALUES (0, ZERO B, 1, 200)\nINSERT VALUES (upc-a, FULL ONE, 1, 300)\nSELECT 10000000000\nBYE FORCE\n' | ./gsa-cli.exe t.DAT >> ../actual.out 2>&1
echo "EXIT=$?" >> ../actual.out
cd ..
