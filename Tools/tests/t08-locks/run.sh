#!/bin/bash
# Lock semantics (D14/D30): plain LOAD refusal, OVERRIDE refused against a live
# gsa-cli holder, no stale-lock auto-detection, OVERRIDE succeeds against a dead
# holder. Multi-process orchestration lives in lock.ps1 (bash process control on
# Windows is unreliable for this; see D63).
set -u
cd "$SCRATCH"
cp "$WS_ROOT/GSA EALITEMR.DAT" t.DAT
powershell -NoProfile -ExecutionPolicy Bypass -File "$TESTDIR/lock.ps1" > actual.out 2>&1
echo "EXIT=$?" > exit.code
