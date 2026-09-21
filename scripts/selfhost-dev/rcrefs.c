// usage: rcrefs <size> <word> <dump>... ; dumps are rcdump-<k>.bin, sampled at 0x100000000000 + k GB.
// Finds live RC cells of the given total size whose first payload word is <word> ("ptr" for any
// pointer), then buckets every live cell that points at one (at its header or payload) by the
// referrer's size, first word and the offset of the pointing word. Also reports how many targets
// no sampled cell points at.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint64_t addr; uint64_t size; uint64_t word; } Cell;
typedef struct { uint64_t size; uint64_t word; uint64_t offset; uint64_t count; } Bucket;

static Cell *cells; static size_t ncells, capcells;
static Bucket buckets[1 << 16]; static size_t used;

static void addcell(uint64_t addr, uint64_t size, uint64_t word) {
    if (ncells == capcells) { capcells = capcells ? capcells * 2 : 1 << 20; cells = realloc(cells, capcells * sizeof(Cell)); }
    cells[ncells++] = (Cell){ addr, size, word };
}

static int cmpaddr(const void *a, const void *b) {
    uint64_t x = ((const uint64_t *)a)[0], y = ((const uint64_t *)b)[0];
    return x < y ? -1 : x > y;
}

static int cmpcount(const void *a, const void *b) {
    const Bucket *x = a, *y = b;
    return x->count < y->count ? 1 : x->count > y->count ? -1 : 0;
}

static int kind(uint64_t count) {
    if (count == 0) return 2;
    if (count < (1ULL << 32)) return 1;
    if (count == (1ULL << 62)) return 3;
    if (count >= 0x100000000000ULL && count < 0x140000000000ULL) return 2;
    return 0;
}

static int plausible(const uint8_t *data, long len, long offset) {
    if (offset + 16 > len) return 0;
    uint64_t count, size;
    memcpy(&count, data + offset, 8); memcpy(&size, data + offset + 8, 8);
    return kind(count) != 0 && size >= 24 && size <= (1u << 20) && (size & 7) == 0;
}

static int chain(const uint8_t *data, long len, long offset) {
    for (int k = 0; k < 16; k++) {
        if (offset + 16 > len) return k > 0;
        if (!plausible(data, len, offset)) return 0;
        uint64_t size; memcpy(&size, data + offset + 8, 8);
        offset += size;
    }
    return 1;
}

static uint64_t *targets; static size_t ntargets; static uint8_t *hit;

static long find(uint64_t p) {
    size_t lo = 0, hi = ntargets;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (targets[mid] < p) lo = mid + 1; else hi = mid; }
    if (lo < ntargets && targets[lo] == p) return (long)lo;
    return -1;
}

int main(int argc, char **argv) {
    uint64_t tsize = strtoull(argv[1], 0, 10);
    int anyptr = strcmp(argv[2], "ptr") == 0;
    uint64_t tword = anyptr ? 0 : strtoull(argv[2], 0, 10);
    uint8_t **datas = calloc(argc, sizeof(uint8_t *)); uint64_t *bases = calloc(argc, 8); long *lens = calloc(argc, sizeof(long));
    for (int a = 3; a < argc; a++) {
        const char *name = strrchr(argv[a], '-');
        int k = atoi(name + 1);
        bases[a] = 0x100000000000ULL + (uint64_t)k * 1073741824ULL;
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); lens[a] = ftell(in); fseek(in, 0, SEEK_SET);
        datas[a] = malloc(lens[a]); if (fread(datas[a], 1, lens[a], in) != (size_t)lens[a]) return 1; fclose(in);
        long offset = 0;
        while (offset + 24 <= lens[a]) {
            if (!chain(datas[a], lens[a], offset)) { offset += 8; continue; }
            while (offset + 24 <= lens[a] && plausible(datas[a], lens[a], offset)) {
                uint64_t count, size, word;
                memcpy(&count, datas[a] + offset, 8); memcpy(&size, datas[a] + offset + 8, 8); memcpy(&word, datas[a] + offset + 16, 8);
                if (count > 0 && count < (1ULL << 32) && offset + (long)size <= lens[a]) addcell(bases[a] + offset, size, word);
                offset += size;
            }
        }
    }
    targets = malloc(ncells * 2 * 8);
    for (size_t i = 0; i < ncells; i++) {
        int isptr = cells[i].word > 0xffff;
        if (cells[i].size == tsize && (anyptr ? isptr : (!isptr && cells[i].word == tword))) {
            targets[ntargets++] = cells[i].addr; targets[ntargets++] = cells[i].addr + 16;
        }
    }
    qsort(targets, ntargets, 8, cmpaddr);
    hit = calloc(ntargets, 1);
    for (int a = 3; a < argc; a++) {
        if (!datas[a]) continue;
        for (size_t i = 0; i < ncells; i++) {
            if (cells[i].addr < bases[a] || cells[i].addr >= bases[a] + (uint64_t)lens[a]) continue;
            uint8_t *p = datas[a] + (cells[i].addr - bases[a]);
            for (uint64_t off = 16; off < cells[i].size; off += 8) {
                uint64_t v; memcpy(&v, p + off, 8);
                long t = find(v); if (t < 0) continue;
                hit[t & ~1L] = 1;
                uint64_t w = cells[i].word > 0xffff ? 0xffffffffffffffffULL : cells[i].word;
                size_t b = 0;
                for (; b < used; b++) if (buckets[b].size == cells[i].size && buckets[b].word == w && buckets[b].offset == off - 16) break;
                if (b == used && used < (1 << 16)) buckets[used++] = (Bucket){ cells[i].size, w, off - 16, 0 };
                if (b < used) buckets[b].count++;
            }
        }
    }
    size_t unref = 0;
    for (size_t t = 0; t < ntargets; t += 2) if (!hit[t]) unref++;
    printf("targets %zu, pointed at by no sampled cell %zu\n", ntargets / 2, unref);
    qsort(buckets, used, sizeof(Bucket), cmpcount);
    for (size_t b = 0; b < used && b < 25; b++) {
        if (buckets[b].word == 0xffffffffffffffffULL)
            printf("referrer size %4llu word ptr    field+%-4llu %9llu\n", (unsigned long long)buckets[b].size, (unsigned long long)buckets[b].offset, (unsigned long long)buckets[b].count);
        else
            printf("referrer size %4llu word %-6llu field+%-4llu %9llu\n", (unsigned long long)buckets[b].size, (unsigned long long)buckets[b].word, (unsigned long long)buckets[b].offset, (unsigned long long)buckets[b].count);
    }
    return 0;
}
