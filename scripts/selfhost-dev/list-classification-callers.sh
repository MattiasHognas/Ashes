#!/usr/bin/env bash
# List classification references with their enclosing source function.
set -euo pipefail
cd "$(dirname "$0")/../.."
pattern='\b(classifyHeap[A-Za-z]*|heapFactsOf|heapShallowCopyOf|heapResourceOf|heapAdmissibilityOf|layoutFactsOf|heapChildrenOfNamed|heapRuntime[A-Za-z]*Layout)\b'
while IFS= read -r source_file; do
    awk -v source_file="$source_file" '
        /^(let|and) / {
            function_name = $2
            if (function_name == "recursive") function_name = $3
        }
        /classifyHeap[A-Za-z]*|heapFactsOf|heapShallowCopyOf|heapResourceOf|heapAdmissibilityOf|layoutFactsOf|heapChildrenOfNamed|heapRuntime[A-Za-z]*Layout/ {
            if ($0 ~ /^[[:space:]]*value /) next
            printf "%s:%d [%s] %s\n", source_file, FNR, function_name, $0
        }
    ' "$source_file"
done < <(rg -l --glob '*.ash' "$pattern" selfhost/packages | sort)
