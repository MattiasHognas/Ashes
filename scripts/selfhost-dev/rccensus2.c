// usage: rccensus2 <dump>... ; walks RC cells ([count][size][payload]) in raw dumps that may start
// mid-cell. It synchronizes on an offset where 16 consecutive headers parse (count live, free or
// immortal; size a multiple of 8 between 24 and 1 MB), walks until a header fails, then resyncs.
// Prints the bytes covered, live/free split and the most common (size, first word) pairs of live cells.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint64_t size; uint64_t word; uint64_t count; uint64_t bytes; } Bucket;
static Bucket buckets[1 << 16]; static size_t used;

static int cmp(const void *a, const void *b) {
    const Bucket *x = a, *y = b;
    return x->bytes < y->bytes ? 1 : x->bytes > y->bytes ? -1 : 0;
}

static void add(uint64_t size, uint64_t word) {
    if (word > 0xffff) word = 0xffffffffffffffffULL;
    for (size_t i = 0; i < used; i++) {
        if (buckets[i].size == size && buckets[i].word == word) { buckets[i].count++; buckets[i].bytes += size; return; }
    }
    if (used < (1 << 16)) buckets[used++] = (Bucket){ size, word, 1, size };
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

int main(int argc, char **argv) {
    uint64_t live = 0, freed = 0, immortal = 0, covered = 0, total = 0;
    for (int a = 1; a < argc; a++) {
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); long len = ftell(in); fseek(in, 0, SEEK_SET);
        uint8_t *data = malloc(len); if (fread(data, 1, len, in) != (size_t)len) return 1; fclose(in);
        total += len;
        long offset = 0;
        while (offset + 16 <= len) {
            if (!chain(data, len, offset)) { offset += 8; continue; }
            while (offset + 16 <= len && plausible(data, len, offset)) {
                uint64_t count, size, word = 0;
                memcpy(&count, data + offset, 8); memcpy(&size, data + offset + 8, 8);
                if (offset + 24 <= len) memcpy(&word, data + offset + 16, 8);
                int k = kind(count);
                if (k == 1) { live += size; add(size, word); } else if (k == 2) freed += size; else immortal += size;
                covered += size;
                offset += size;
            }
        }
        free(data);
    }
    printf("sampled %.1f MB, covered %.1f MB: live %.1f MB, free %.1f MB, immortal %.1f MB\n",
        total / 1048576.0, covered / 1048576.0, live / 1048576.0, freed / 1048576.0, immortal / 1048576.0);
    qsort(buckets, used, sizeof(Bucket), cmp);
    for (size_t i = 0; i < used && i < 25; i++) {
        if (buckets[i].word == 0xffffffffffffffffULL)
            printf("size %6llu word ptr    %9llu cells %8.1f MB\n", (unsigned long long)buckets[i].size, (unsigned long long)buckets[i].count, buckets[i].bytes / 1048576.0);
        else
            printf("size %6llu word %-6llu %9llu cells %8.1f MB\n", (unsigned long long)buckets[i].size, (unsigned long long)buckets[i].word, (unsigned long long)buckets[i].count, buckets[i].bytes / 1048576.0);
    }
    return 0;
}
