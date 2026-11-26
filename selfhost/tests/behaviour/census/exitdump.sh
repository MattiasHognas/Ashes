#!/bin/bash
# usage: exitdump.sh <out-dir> <gigabytes> <program> <args...>
# Runs the program under gdb, stops it at exit_group, dumps the first <gigabytes> GiB of the
# reference-counted region into <out-dir>/region.bin, and kills it.
out=$1; gb=$2; shift 2
mkdir -p "$out"
base=$((0x100000000000)); end=$((base + gb * 1073741824))
{ echo "set pagination off"; echo "catch syscall exit_group"; echo "run"
  echo "dump binary memory $out/region.bin $base $end"; echo "kill"; } > "$out/dump.gdb"
gdb -q -batch -x "$out/dump.gdb" --args "$@" > "$out/gdb.log" 2>&1
ls -la "$out/region.bin"
