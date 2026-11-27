#!/bin/bash
# usage: exitdump.sh <out-dir> <gigabytes> <program> <args...>
# Runs the program under gdb, stops it at exit_group, dumps the first <gigabytes> GiB of the
# reference-counted region into <out-dir>/region.bin, and kills it. The dump is written one GiB
# at a time, since gdb holds each dumped range in its own memory.
# Before the region it dumps the roots rcmark reads into <out-dir>/roots: the resident pages of
# every writable anonymous mapping and of the executable's own writable mappings (rootranges),
# and every thread's registers. The root bytes are reported in <out-dir>/roots.log.
T=$(dirname "$(realpath "$0")")
out=$(realpath -m "$1"); gb=$2; shift 2
[ -x "$T/rootranges" ] || gcc -O2 -o "$T/rootranges" "$T/rootranges.c" || exit 1
mkdir -p "$out"; rm -rf "$out/region.bin" "$out/roots"; mkdir -p "$out/roots"
base=$((0x100000000000))
{ echo "set pagination off"; echo "catch syscall exit_group"; echo "run"
  echo "pipe info proc | awk '/^process/ {print \$2}' > $out/pid"
  echo "shell $T/rootranges \$(cat $out/pid) $out > $out/roots.gdb 2> $out/roots.log"
  echo "source $out/roots.gdb"
  echo "pipe thread apply all info registers | cat > $out/roots/registers.txt"
  for ((i = 0; i < gb; i++)); do
      echo "dump binary memory $out/chunk.bin $((base + i * 1073741824)) $((base + (i + 1) * 1073741824))"
      echo "shell cat $out/chunk.bin >> $out/region.bin && rm $out/chunk.bin"
  done
  echo "kill"; } > "$out/dump.gdb"
gdb -q -batch -x "$out/dump.gdb" --args "$@" > "$out/gdb.log" 2>&1
cat "$out/roots.log"; du -sb "$out/roots"
ls -la "$out/region.bin"
