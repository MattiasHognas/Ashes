// usage: rcrootsof <dir> <size> <count> ; reads full-<k>.bin chunks of the RC region and prints the header
// addresses of <count> live cells of the given total size that no live cell points at, spread evenly over the
// address range: samples of one root class to look into with rcpeek.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define BASE 0x100000000000ULL
#define CHUNK (256ULL << 20)

static uint8_t *heap; static uint64_t heaplen;
static uint64_t *coff; static uint32_t *csize; static uint8_t *clive; static size_t ncells, capcells;

static void addcell(uint64_t off, uint32_t size, int live) {
    if (ncells == capcells) {
        capcells = capcells ? capcells * 2 : 1 << 24;
        coff = realloc(coff, capcells * 8); csize = realloc(csize, capcells * 4); clive = realloc(clive, capcells);
    }
    coff[ncells] = off; csize[ncells] = size; clive[ncells] = (uint8_t)live; ncells++;
}

static long findcell(uint64_t v) {
    if (v < BASE || v >= BASE + heaplen) return -1;
    uint64_t o = v - BASE;
    size_t lo = 0, hi = ncells;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (coff[mid] <= o) lo = mid + 1; else hi = mid; }
    if (lo == 0) return -1;
    size_t i = lo - 1;
    if (o != coff[i] && o != coff[i] + 16) return -1;
    return (long)i;
}

static int plausible(uint64_t off) {
    if (off + 24 > heaplen) return 0;
    uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
    if (size < 24 || (size & 7) || size > (64ULL << 20) || off + size > heaplen) return 0;
    int live = count > 0 && count < (1ULL << 40);
    int freed = count == 0 || (count >= BASE && count < BASE + (4ULL << 40));
    return live || freed || count == (1ULL << 62);
}

int main(int argc, char **argv) {
    if (argc < 4) { fprintf(stderr, "usage: rcrootsof <dir> <size> <count>\n"); return 1; }
    char path[4096]; int maxk = -1;
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
        addcell(off, (uint32_t)size, count > 0 && count < (1ULL << 40));
        off += size;
    }
    uint8_t *pointed = calloc(ncells, 1);
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i]) continue;
        for (uint64_t w = 16; w + 8 <= csize[i]; w += 8) {
            uint64_t v; memcpy(&v, heap + coff[i] + w, 8);
            long t = findcell(v);
            if (t >= 0 && (size_t)t != i) pointed[t] = 1;
        }
    }
    uint32_t wanted = (uint32_t)atoi(argv[2]); long count = atol(argv[3]);
    size_t total = 0;
    for (size_t i = 0; i < ncells; i++) if (clive[i] && !pointed[i] && csize[i] == wanted) total++;
    fprintf(stderr, "roots of size %u: %zu\n", wanted, total);
    size_t step = total > (size_t)count ? total / (size_t)count : 1, seen = 0;
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i] || pointed[i] || csize[i] != wanted) continue;
        if (seen % step == 0) printf("%llx\n", (unsigned long long)(BASE + coff[i]));
        seen++;
    }
    return 0;
}
