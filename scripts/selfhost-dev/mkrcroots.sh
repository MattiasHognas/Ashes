#!/bin/bash
# Generates rcroots.c from rcstates.c (same loader and graph) with a different report: for every leaked
# root that is a 32-byte cell starting with a pointer (a list head), the list length, the element's cell
# size, the element's first payload word (a constructor tag when small) and a string found within two
# hops of the element. Prints a tally by (element size, tag, string) and the LAST <n> roots in address
# order, the most recently allocated.
. "$(dirname "$0")/env.sh"
{
  sed -n '1,/^    static uint64_t tally/p' "$HERE/rcstates.c" | sed '$d'
  cat <<'EOF'
    int lastn = argc > 3 ? atoi(argv[3]) : 40;
    typedef struct { uint64_t addr; long len; uint32_t esize; uint64_t etag; char text[40]; } Row;
    Row *rows = calloc(ncells, sizeof(Row)); size_t nrows = 0;
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i] || csize[i] != 32 || indeg[i] != 0) continue;
        long head = findcell(word(i, 0));
        if (head < 0) continue;
        Row *r = &rows[nrows++];
        r->addr = BASE + coff[i]; r->esize = csize[head]; r->etag = word((size_t)head, 0);
        long cur = (long)i; r->len = 0;
        while (cur >= 0 && csize[cur] == 32 && r->len < 1000000) { r->len++; cur = findcell(word((size_t)cur, 1)); }
        r->text[0] = 0;
        // A string cell: first payload word is a byte length, followed by that many printable bytes.
        for (int hop = 0; hop < 2 && !r->text[0]; hop++) {
            for (uint64_t w = 0; w * 8 + 16 < csize[head] && !r->text[0]; w++) {
                long s = findcell(word((size_t)head, (int)w));
                if (s < 0) continue;
                if (hop == 1) { long s2 = -1; for (uint64_t w2 = 0; w2 * 8 + 16 < csize[s] && s2 < 0; w2++) s2 = findcell(word((size_t)s, (int)w2)); s = s2; if (s < 0) continue; }
                uint64_t n = word((size_t)s, 0);
                if (n == 0 || n > 38 || 16 + 8 + n > csize[s]) continue;
                const uint8_t *p = heap + coff[s] + 24; int ok = 1;
                for (uint64_t c = 0; c < n; c++) if (p[c] < 32 || p[c] > 126) ok = 0;
                if (ok) { memcpy(r->text, p, n); r->text[n] = 0; }
            }
        }
    }
    if (getenv("RCROOTS_REACH")) {
        // Every leaked root at or above the given address, ranked by how many cells it reaches.
        uint64_t from = strtoull(getenv("RCROOTS_REACH"), NULL, 16);
        uint32_t *stamp = calloc(ncells, 4); size_t *stack = malloc(ncells * sizeof(size_t));
        typedef struct { uint64_t addr; uint32_t size; long reach; uint64_t bytes; } Reach;
        Reach *out = calloc(ncells, sizeof(Reach)); size_t nout = 0; uint32_t generation = 0;
        for (size_t i = 0; i < ncells; i++) {
            if (!clive[i] || indeg[i] != 0 || BASE + coff[i] < from) continue;
            generation++; size_t sp = 0; stack[sp++] = i; stamp[i] = generation; long reach = 0; uint64_t bytes = 0;
            while (sp > 0) {
                size_t c = stack[--sp]; reach++; bytes += csize[c];
                for (uint64_t w = 16; w + 8 <= csize[c]; w += 8) {
                    uint64_t v; memcpy(&v, heap + coff[c] + w, 8);
                    long t = findcell(v);
                    if (t >= 0 && clive[t] && stamp[t] != generation) { stamp[t] = generation; stack[sp++] = (size_t)t; }
                }
            }
            out[nout].addr = BASE + coff[i]; out[nout].size = csize[i]; out[nout].reach = reach; out[nout].bytes = bytes; nout++;
        }
        for (int round = 0; round < lastn; round++) {
            long best = -1;
            for (size_t k = 0; k < nout; k++) if (out[k].reach > 0 && (best < 0 || out[k].bytes > out[best].bytes)) best = (long)k;
            if (best < 0) break;
            printf("  %llx size=%-4u reaches %ld cells, %llu bytes\n", (unsigned long long)out[best].addr, out[best].size, out[best].reach, (unsigned long long)out[best].bytes);
            out[best].reach = 0;
        }
        return 0;
    }
    printf("leaked list heads: %zu\n", nrows);
    if (getenv("RCROOTS_TALLY")) {
        for (size_t k = 0; k < nrows; k++)
            printf("T len=%ld elem=%u tag=%llu cells=%ld\n", rows[k].len, rows[k].esize,
                (unsigned long long)(rows[k].etag < 100000 ? rows[k].etag : 99999), rows[k].len);
        return 0;
    }
    size_t from = nrows > (size_t)lastn ? nrows - (size_t)lastn : 0;
    for (size_t k = from; k < nrows; k++)
        printf("  %llx len=%-5ld elem=%-4u tag=%-6llu %s\n", (unsigned long long)rows[k].addr, rows[k].len, rows[k].esize,
            (unsigned long long)(rows[k].etag < 100000 ? rows[k].etag : 99999), rows[k].text);
    return 0;
}
EOF
} > "$HERE/rcroots.c"
clang -O2 -o "$T/rcroots" "$HERE/rcroots.c" && echo built
