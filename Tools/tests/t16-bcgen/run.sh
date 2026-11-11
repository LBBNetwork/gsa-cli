#!/bin/bash
# bcgen (D68): CLI contract, cell rules, settings semantics, error discipline.
# All generation runs use --seed so the transcript is byte-stable across
# machines (SplitMix64 derivation is machine-independent).
set -u
cd "$SCRATCH"

BCGEN="$WS_ROOT/bcgen/bin/bcgen.exe"
if [ ! -f "$BCGEN" ]; then echo "bcgen not built: $BCGEN missing. Run bcgen/build.bat first." > actual.out; exit 1; fi

# --- CLI contract ----------------------------------------------------------

printf 'ITEM_CODE,NAME\n0,x\n' > base.csv

$BCGEN -h > actual.out 2>&1
echo "EXIT=$?" >> actual.out

$BCGEN >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

$BCGEN base.csv >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

$BCGEN base.csv base.csv --seed 1 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

$BCGEN base.csv out.csv --seed -5 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

$BCGEN base.csv out.csv --frobnicate >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

# --- cell rules (default method: no settings.xml beside the exe) ----------

printf 'ITEM_CODE,ITEM_NAME,SALEPRICECENTS\n,Alpha,100\n0,Beta,200\nrnd4,Gamma,300\nupc-a,Delta,400\nupc-a-nc,Eta,500\n12345,Epsilon,600\n00,Zeta,700\n' > mixed.csv
$BCGEN mixed.csv out-mixed.csv --seed 42 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out

# Output byte contract (D68): UTF-8 BOM, CRLF between lines, no trailing newline.
# Checked at the byte level; the transcript itself stays pure ASCII so the
# harness's text decoding can never mangle it.
printf '\xef\xbb\xbf' > .bom3
cmp -s <(head -c 3 out-mixed.csv) .bom3 && echo "BOM OK" >> actual.out || echo "BOM FAIL" >> actual.out
lf=$(($(tr -cd '\n' < out-mixed.csv | wc -c)))
cr=$(($(tr -cd '\r' < out-mixed.csv | wc -c)))
[ "$lf" = "$cr" ] && [ "$lf" = "7" ] && echo "CRLF OK (lines=8)" >> actual.out || echo "CRLF FAIL (lf=$lf cr=$cr)" >> actual.out
lastbyte=$(tail -c 1 out-mixed.csv | od -An -tu1 | tr -d '[:space:]')
[ "$lastbyte" = "10" ] && echo "TRAILING-NEWLINE FAIL" >> actual.out || echo "NO-TRAILING-NEWLINE OK" >> actual.out

# Pinned content: BOM stripped, CR stripped (codes are deterministic under --seed).
tail -c +4 out-mixed.csv | tr -d '\r' >> actual.out
echo >> actual.out

# determinism: same seed -> byte-identical output
$BCGEN mixed.csv out-mixed2.csv --seed 42 > /dev/null 2>&1
if cmp -s out-mixed.csv out-mixed2.csv; then echo "DETERMINISM OK" >> actual.out; else echo "DETERMINISM FAIL" >> actual.out; fi

# different seed -> different codes (same shape)
$BCGEN mixed.csv out-mixed3.csv --seed 43 > /dev/null 2>&1
if cmp -s out-mixed.csv out-mixed3.csv; then echo "SEED VARIATION FAIL" >> actual.out; else echo "SEED VARIATION OK" >> actual.out; fi

# --- input validation ------------------------------------------------------

$BCGEN nosuchfile.csv out-nosuch.csv --seed 1 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out
if [ -e out-nosuch.csv ]; then echo "NO-PARTIAL FAIL (output exists)" >> actual.out; else echo "NO-PARTIAL OK (missing input)" >> actual.out; fi

printf 'CODE,NAME\n1,X\n' > badhdr.csv
$BCGEN badhdr.csv out-badhdr.csv --seed 1 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out
if [ -e out-badhdr.csv ]; then echo "NO-PARTIAL FAIL (bad header)" >> actual.out; else echo "NO-PARTIAL OK (bad header)" >> actual.out; fi

printf 'item_code,name\n0,x\n' > lowerhdr.csv
$BCGEN lowerhdr.csv out-lowerhdr.csv --seed 1 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out
tail -c +4 out-lowerhdr.csv | tr -d '\r' >> actual.out; echo >> actual.out

printf 'ITEM_CODE,NAME\n0,x,y\n' > ragged.csv
$BCGEN ragged.csv out-ragged.csv --seed 1 >> actual.out 2>&1
echo "EXIT=$?" >> actual.out
if [ -e out-ragged.csv ]; then echo "NO-PARTIAL FAIL (ragged)" >> actual.out; else echo "NO-PARTIAL OK (ragged)" >> actual.out; fi

# --- settings semantics (isolated deploy dir beside the exe) --------------

mkdir bcdeploy
cp "$WS_ROOT/bcgen/bin/"* bcdeploy/
cat > bcdeploy/settings.xml <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<Settings>
  <Defaults>
    <Default id="CODE_METHOD">rnd4</Default>
  </Defaults>
</Settings>
EOF
printf 'ITEM_CODE,NAME\n,Blank\n0,Zero\nupc-a,ExplicitUpca\n12345,Keep\n' > setin.csv
cp setin.csv bcdeploy/in.csv
cd bcdeploy
./bcgen.exe in.csv out-set.csv --seed 7 >> ../actual.out 2>&1
echo "EXIT=$?" >> ../actual.out
tail -c +4 out-set.csv | tr -d '\r' >> ../actual.out; echo >> ../actual.out
cd ..

# --- D70: upc-a-nc as the default method ----------------------------------

mkdir bcdeploy-nc
cp "$WS_ROOT/bcgen/bin/"* bcdeploy-nc/
cat > bcdeploy-nc/settings.xml <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<Settings>
  <Defaults>
    <Default id="CODE_METHOD">upc-a-nc</Default>
  </Defaults>
</Settings>
EOF
printf 'ITEM_CODE,NAME\n,Blank\n0,Zero\nupc-a,ExplicitUpca\n' > setin-nc.csv
cp setin-nc.csv bcdeploy-nc/in.csv
cd bcdeploy-nc
./bcgen.exe in.csv out-nc.csv --seed 7 >> ../actual.out 2>&1
echo "EXIT=$?" >> ../actual.out
tail -c +4 out-nc.csv | tr -d '\r' >> ../actual.out; echo >> ../actual.out
cd ..

# corrupt settings -> hard error, no output
mkdir bcdeploy-corrupt
cp "$WS_ROOT/bcgen/bin/"* bcdeploy-corrupt/
echo "this is not xml <<<" > bcdeploy-corrupt/settings.xml
cp setin.csv bcdeploy-corrupt/in.csv
cd bcdeploy-corrupt
./bcgen.exe in.csv out-corrupt.csv --seed 7 >> ../actual.out 2>&1
echo "EXIT=$?" >> ../actual.out
if [ -e out-corrupt.csv ]; then echo "NO-PARTIAL FAIL (corrupt settings)" >> ../actual.out; else echo "NO-PARTIAL OK (corrupt settings)" >> ../actual.out; fi
cd ..
