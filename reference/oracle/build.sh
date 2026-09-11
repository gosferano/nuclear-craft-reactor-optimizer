#!/bin/sh
# Builds the evaluation oracle used by FissionOpt.Tests. Only the two evaluator
# translation units are needed (no optimizer, no net), which keeps the build
# to a few seconds. Output: reference/oracle/build/oracle
set -e
cd "$(dirname "$0")"
mkdir -p build
: "${CXX:=c++}"
exec "$CXX" -std=c++17 -O2 -I../xtl/include -I../xtensor/include \
  Oracle.cpp ../FissionOpt/Fission.cpp ../FissionOpt/OverhaulFission.cpp \
  -o build/oracle
