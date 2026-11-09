#!/bin/bash
# usage: reprogrowth.sh <laneB log> ; prints each repro/stage/mode whose rc-live differs between r=1 and r=2, and every non-zero exit
awk '$2 ~ /^r=/ { key=$1" "$3" "$4; live=""; for (i=1;i<=NF;i++) if ($i=="ashes-rc-live") live=$(i+1);
  if ($2=="r=1") a[key]=live; if ($2=="r=2") b[key]=live; if ($5!="exit=0") bad[key]=$5 }
  END { for (k in b) if (a[k]!=b[k]) print "GROW", k, a[k], "->", b[k]; for (k in bad) print "EXIT", k, bad[k] }' "$1" | sort
