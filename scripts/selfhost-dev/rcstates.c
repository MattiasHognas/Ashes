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
    static uint64_t tally[1024]; uint64_t states = 0, roots = 0, rootsCount1 = 0, emptyList = 0, headShared = 0, headUnique = 0, badShape = 0;
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i] || csize[i] != 200) continue;
        states++;
        if (indeg[i] != 0) continue;
        roots++;
        if (ccount[i] != 1) continue;
        rootsCount1++;
        long list = findcell(word(i, 0));
        if (getenv("RCSTATES_LIST")) {
            long headKind = -1;
            if (list >= 0) { long in0 = findcell(word((size_t)list, 0)); if (in0 >= 0) { long k0 = findcell(word((size_t)in0, 0)); if (k0 >= 0) headKind = (long)word((size_t)k0, 0); } }
            printf("state %llx head=%s nextTemp=%lld nextLocal=%lld nextLambdaId=%lld nextLabelId=%lld\n",
                (unsigned long long)(BASE + coff[i]), headKind < 0 ? "-" : (headKind < nnames ? names[headKind] : "?"),
                (long long)word(i, 5), (long long)word(i, 6), (long long)word(i, 7), (long long)word(i, 8));
        }
        if (list < 0) { emptyList++; continue; }
        if (ccount[list] > 1) headShared++; else headUnique++;
        long inst = findcell(word((size_t)list, 0));
        if (inst < 0) { badShape++; continue; }
        long kind = findcell(word((size_t)inst, 0));
        if (kind < 0) { badShape++; continue; }
        uint64_t tag = word((size_t)kind, 0);
        tally[tag < 1023 ? tag : 1023]++;
    }
    printf("200-byte live cells=%llu roots=%llu roots-with-count-1=%llu\n", (unsigned long long)states, (unsigned long long)roots, (unsigned long long)rootsCount1);
    printf("  empty list=%llu first-cell-shared=%llu first-cell-unique=%llu unreadable=%llu\n", (unsigned long long)emptyList, (unsigned long long)headShared, (unsigned long long)headUnique, (unsigned long long)badShape);
    for (int round = 0; round < 25; round++) {
        int best = -1;
        for (int t = 0; t < 1024; t++) if (tally[t] && (best < 0 || tally[t] > tally[best])) best = t;
        if (best < 0) break;
        printf("  %8llu  tag=%d %s\n", (unsigned long long)tally[best], best, best < nnames ? names[best] : "?");
        tally[best] = 0;
    }
    return 0;
}
