// usage: rcstates <dir> <names-file> ; reads full-<k>.bin chunks of the RC region, finds the 200-byte
// cells (CoreLoweringState) that no live cell points at and whose count is 1, and tallies the
// constructor tag of the head of field 0 (reversedInstructions): the instruction whose emit produced
// that state. Also reports how many of those heads are themselves shared (count > 1), which tells a
// state whose successor kept its list from one nothing else holds.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define BASE 0x100000000000ULL
#define CHUNK (256ULL << 20)

static uint8_t *heap; static uint64_t heaplen;
static uint64_t *coff; static uint32_t *csize; static uint64_t *ccount; static uint8_t *clive; static size_t ncells, capcells;

static void addcell(uint64_t off, uint32_t size, uint64_t count, int live) {
    if (ncells == capcells) {
        capcells = capcells ? capcells * 2 : 1 << 24;
        coff = realloc(coff, capcells * 8); csize = realloc(csize, capcells * 4);
        ccount = realloc(ccount, capcells * 8); clive = realloc(clive, capcells);
    }
    coff[ncells] = off; csize[ncells] = size; ccount[ncells] = count; clive[ncells] = (uint8_t)live; ncells++;
}

static long findcell(uint64_t v) {
    if (v < BASE || v >= BASE + heaplen) return -1;
    uint64_t o = v - BASE;
    size_t lo = 0, hi = ncells;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (coff[mid] <= o) lo = mid + 1; else hi = mid; }
    if (lo == 0) return -1;
    size_t i = lo - 1;
    if (o >= coff[i] + csize[i]) return -1;
    if (o != coff[i] && o != coff[i] + 16) return -1;
    return (long)i;
}

static int plausible(uint64_t off) {
    if (off + 24 > heaplen) return 0;
    uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
    if (size < 24 || (size & 7) || size > (64ULL << 20) || off + size > heaplen) return 0;
    int live = count > 0 && count < (1ULL << 40);
    int freed = count == 0 || (count >= BASE && count < BASE + (4ULL << 40));
    int immortal = count == (1ULL << 62);
    return live || freed || immortal;
}

static uint64_t word(size_t cell, int index) { uint64_t v; memcpy(&v, heap + coff[cell] + 16 + 8 * (uint64_t)index, 8); return v; }

int main(int argc, char **argv) {
    char path[4096]; int maxk = -1;
    static char names[512][64]; int nnames = 0;
    FILE *nf = fopen(argv[2], "r");
    if (nf) { while (nnames < 512 && fscanf(nf, "%63s", names[nnames]) == 1) nnames++; fclose(nf); }
    for (int k = 0; k < 4096; k++) { snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (f) { fclose(f); maxk = k; } }
    if (maxk < 0) { fprintf(stderr, "no chunks\n"); return 1; }
    heaplen = (uint64_t)(maxk + 1) * CHUNK;
    heap = calloc(heaplen, 1);
    for (int k = 0; k <= maxk; k++) {
        snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (!f) continue;
        size_t got = fread(heap + (uint64_t)k * CHUNK, 1, CHUNK, f); (void)got; fclose(f);
    }
    uint64_t off = 0;
    while (off + 24 <= heaplen) {
        if (!plausible(off)) { off += 8; continue; }
        uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
        addcell(off, (uint32_t)size, count, count > 0 && count < (1ULL << 40));
        off += size;
    }
    uint32_t *indeg = calloc(ncells, 4);
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i]) continue;
        for (uint64_t w = 16; w + 8 <= csize[i]; w += 8) {
            uint64_t v; memcpy(&v, heap + coff[i] + w, 8);
            long t = findcell(v);
            if (t >= 0 && (size_t)t != i && clive[t]) indeg[t]++;
        }
    }
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
