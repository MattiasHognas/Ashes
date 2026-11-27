#!/bin/bash
# usage: exitdump.sh <out-dir> <gigabytes> <program> <args...>
# Runs the program under gdb, stops it at exit_group, dumps the first <gigabytes> GiB of the
# reference-counted region into <out-dir>/region.bin, and kills it. The dump is written one GiB
# at a time, since gdb holds each dumped range in its own memory.
out=$1; gb=$2; shift 2
mkdir -p "$out"; rm -f "$out/region.bin"
base=$((0x100000000000))
{ echo "set pagination off"; echo "catch syscall exit_group"; echo "run"
  for ((i = 0; i < gb; i++)); do
      echo "dump binary memory $out/chunk.bin $((base + i * 1073741824)) $((base + (i + 1) * 1073741824))"
      echo "shell cat $out/chunk.bin >> $out/region.bin && rm $out/chunk.bin"
  done
  echo "kill"; } > "$out/dump.gdb"
gdb -q -batch -x "$out/dump.gdb" --args "$@" > "$out/gdb.log" 2>&1
ls -la "$out/region.bin"
