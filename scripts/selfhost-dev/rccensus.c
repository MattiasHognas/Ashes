// usage: rccensus <dump>... ; walks RC cells ([count][size][payload]) in raw dumps of the RC region
// and prints the most common (size, first payload word) pairs among cells with a positive count.
// A zero size ends a chunk's used part; the walk resumes at the next 4 MB boundary.
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint64_t size; uint64_t word; uint64_t count; uint64_t bytes; } Bucket;
static Bucket buckets[1 << 16];
static int used;

static void add(uint64_t size, uint64_t word) {
    // Pointers are folded together; small words (tags, lengths) are kept distinct.
    if (word > 0xffff) word = 0xffffffffffffffffULL;
    for (int i = 0; i < used; i++) {
        if (buckets[i].size == size && buckets[i].word == word) { buckets[i].count++; buckets[i].bytes += size; return; }
    }
    if (used < (1 << 16)) { buckets[used++] = (Bucket){ size, word, 1, size }; }
}

static int cmp(const void *a, const void *b) {
    const Bucket *x = a, *y = b;
    return x->bytes < y->bytes ? 1 : x->bytes > y->bytes ? -1 : 0;
}

int main(int argc, char **argv) {
    uint64_t live = 0, dead = 0;
    for (int f = 1; f < argc; f++) {
        FILE *in = fopen(argv[f], "rb");
        if (!in) { perror(argv[f]); return 1; }
        fseek(in, 0, SEEK_END);
        long length = ftell(in);
        fseek(in, 0, SEEK_SET);
        uint8_t *data = malloc(length);
        if (fread(data, 1, length, in) != (size_t)length) { return 1; }
        fclose(in);
        long offset = 0;
        while (offset + 24 <= length) {
            uint64_t count, size, word;
            memcpy(&count, data + offset, 8);
            memcpy(&size, data + offset + 8, 8);
            memcpy(&word, data + offset + 16, 8);
            if (size == 0 || size > (64u << 20) || (size & 7) != 0) {
                offset = (offset / (4 << 20) + 1) * (4 << 20);
                continue;
            }
            if (count > 0 && count < (1ULL << 40)) { add(size, word); live += size; } else { dead += size; }
            offset += size;
        }
        free(data);
    }
    qsort(buckets, used, sizeof(Bucket), cmp);
    printf("live %.1f MB, not live %.1f MB\n", live / 1048576.0, dead / 1048576.0);
    for (int i = 0; i < used && i < 20; i++) {
        printf("size %4llu word %-6s %9llu cells %8.1f MB\n",
            (unsigned long long)buckets[i].size,
            buckets[i].word == 0xffffffffffffffffULL ? "ptr" : (char[16]){0},
            (unsigned long long)buckets[i].count, buckets[i].bytes / 1048576.0);
        if (buckets[i].word != 0xffffffffffffffffULL) printf("    (word = %llu)\n", (unsigned long long)buckets[i].word);
    }
    return 0;
}
