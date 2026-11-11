#!/bin/bash
# D28 corrupt-settings recovery: rename-aside with date stamp (normalized to
# .corrupt-DATE by the driver) + fresh defaults file, session continues.
set -u
cd "$SCRATCH"
cp "$WS_ROOT/bin/gsa-cli.exe" "$WS_ROOT/bin/EalItemr.dll" "$WS_ROOT/bin/Xml2.dll" "$WS_ROOT/bin/CSVToolkit.dll" .

# first run creates settings.xml from the embedded template (D40)
printf 'BYE\n' | ./gsa-cli.exe > /dev/null 2>&1

# corrupt it, then re-run: recovery notice + defaults restored
echo "this is not xml <<<" > settings.xml
printf 'SHOW CONFIG\nBYE\n' | ./gsa-cli.exe > actual.out 2>&1
echo "EXIT=$?" >> exit.code
