// usage: rcnbrs <size> <word> <dump>... ; for live RC cells of the given total size and first
// payload word that no sampled cell points at (orphans), prints their reference-count histogram
// and buckets the cells allocated right before and right after them (by size and first word).
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint64_t addr; uint64_t size; uint64_t word; uint64_t count; int live; } Cell;
typedef struct { uint64_t size; uint64_t word; uint64_t count; } Bucket;

static Cell *cells; static size_t ncells, capcells;

static void addcell(Cell c) {
    if (ncells == capcells) { capcells = capcells ? capcells * 2 : 1 << 20; cells = realloc(cells, capcells * sizeof(Cell)); }
    cells[ncells++] = c;
}

static int cmpu64(const void *a, const void *b) {
    uint64_t x = *(const uint64_t *)a, y = *(const uint64_t *)b;
    return x < y ? -1 : x > y;
}

static int cmpcount(const void *a, const void *b) {
    const Bucket *x = a, *y = b;
    return x->count < y->count ? 1 : x->count > y->count ? -1 : 0;
}

static void bump(Bucket *table, size_t *used, uint64_t size, uint64_t word) {
    if (word > 0xffff) word = 0xffffffffffffffffULL;
    for (size_t b = 0; b < *used; b++) if (table[b].size == size && table[b].word == word) { table[b].count++; return; }
    if (*used < 4096) table[(*used)++] = (Bucket){ size, word, 1 };
}

static void show(const char *title, Bucket *table, size_t used) {
    qsort(table, used, sizeof(Bucket), cmpcount);
    printf("%s\n", title);
    for (size_t b = 0; b < used && b < 12; b++) {
        if (table[b].word == 0xffffffffffffffffULL) printf("  size %4llu word ptr    %9llu\n", (unsigned long long)table[b].size, (unsigned long long)table[b].count);
        else printf("  size %4llu word %-6llu %9llu\n", (unsigned long long)table[b].size, (unsigned long long)table[b].word, (unsigned long long)table[b].count);
    }
}

int main(int argc, char **argv) {
    uint64_t tsize = strtoull(argv[1], 0, 10); int anyptr = strcmp(argv[2], "ptr") == 0; uint64_t tword = anyptr ? 0 : strtoull(argv[2], 0, 10); int printed = 0;
    uint64_t *pointers = 0; size_t npointers = 0, cappointers = 0;
    for (int a = 3; a < argc; a++) {
        const char *name = strrchr(argv[a], '-');
        uint64_t base = 0x100000000000ULL + (uint64_t)atoi(name + 1) * 1073741824ULL;
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); long len = ftell(in); fseek(in, 0, SEEK_SET);
        uint8_t *data = malloc(len); if (fread(data, 1, len, in) != (size_t)len) return 1; fclose(in);
        long offset = 0;
        while (offset + 24 <= len) {
            uint64_t count, size, word;
            memcpy(&count, data + offset, 8); memcpy(&size, data + offset + 8, 8); memcpy(&word, data + offset + 16, 8);
            if (size == 0 || size > (64u << 20) || (size & 7) != 0 || offset + (long)size > len) {
                offset = (offset / (4 << 20) + 1) * (4 << 20);
                addcell((Cell){ 0, 0, 0, 0, 0 });
                continue;
            }
            int live = count > 0 && count < (1ULL << 40);
            addcell((Cell){ base + offset, size, word, count, live });
            if (live) {
                for (uint64_t off = 16; off < size; off += 8) {
                    uint64_t v; memcpy(&v, data + offset + off, 8);
                    if (v < 0x100000000000ULL || v >= 0x140000000000ULL) continue;
                    if (npointers == cappointers) { cappointers = cappointers ? cappointers * 2 : 1 << 20; pointers = realloc(pointers, cappointers * 8); }
                    pointers[npointers++] = v;
                }
            }
            offset += size;
        }
        free(data);
    }
    qsort(pointers, npointers, 8, cmpu64);
    static Bucket before[4096], after[4096]; size_t nbefore = 0, nafter = 0;
    uint64_t hist[8] = { 0 }; size_t orphans = 0;
    for (size_t i = 0; i < ncells; i++) {
        Cell c = cells[i];
        if (!c.live || c.size != tsize) continue; if (anyptr ? (c.word < 0x100000000000ULL || c.word >= 0x140000000000ULL) : c.word != tword) continue;
        uint64_t key = c.addr + 16;
        if (bsearch(&key, pointers, npointers, 8, cmpu64)) continue;
        key = c.addr;
        if (bsearch(&key, pointers, npointers, 8, cmpu64)) continue;
        orphans++; if (printed < 4000) { printf("orphan %llx count %llu\n", (unsigned long long)c.addr, (unsigned long long)c.count); printed++; }
        hist[c.count < 7 ? c.count : 7]++;
        if (i > 0 && cells[i - 1].size) bump(before, &nbefore, cells[i - 1].size, cells[i - 1].word);
        if (i + 1 < ncells && cells[i + 1].size) bump(after, &nafter, cells[i + 1].size, cells[i + 1].word);
    }
    printf("orphans %zu; count 1:%llu 2:%llu 3:%llu 4:%llu 5:%llu 6:%llu 7+:%llu\n", orphans,
        (unsigned long long)hist[1], (unsigned long long)hist[2], (unsigned long long)hist[3], (unsigned long long)hist[4],
        (unsigned long long)hist[5], (unsigned long long)hist[6], (unsigned long long)hist[7]);
    show("allocated before:", before, nbefore);
    show("allocated after:", after, nafter);
    return 0;
}
