// usage: rcstrings <dump>... ; walks cells like rccensus2 and tallies live cells that look like strings
// ([length][bytes], length 1..40, all bytes printable, size matching the length), printing the most
// common contents and how many 24-byte cells hold a zero first word.
#include <ctype.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { char text[48]; uint64_t count; } Entry;
static Entry entries[1 << 16]; static size_t used;

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

static int cmp(const void *a, const void *b) {
    const Entry *x = a, *y = b;
    return x->count < y->count ? 1 : x->count > y->count ? -1 : 0;
}

int main(int argc, char **argv) {
    uint64_t strings = 0, zero24 = 0;
    for (int a = 1; a < argc; a++) {
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); long len = ftell(in); fseek(in, 0, SEEK_SET);
        uint8_t *data = malloc(len); if (fread(data, 1, len, in) != (size_t)len) return 1; fclose(in);
        long offset = 0;
        while (offset + 16 <= len) {
            if (!chain(data, len, offset)) { offset += 8; continue; }
            while (offset + 16 <= len && plausible(data, len, offset)) {
                uint64_t count, size, word = 0;
                memcpy(&count, data + offset, 8); memcpy(&size, data + offset + 8, 8);
                if (offset + 24 <= len) memcpy(&word, data + offset + 16, 8);
                if (kind(count) == 1 && offset + (long)size <= len) {
                    if (size == 24 && word == 0) zero24++;
                    if (word >= 1 && word <= 40 && 16 + 8 + word <= size && size <= 16 + 8 + word + 16) {
                        int printable = 1;
                        for (uint64_t b = 0; b < word; b++) if (!isprint(data[offset + 24 + b])) { printable = 0; break; }
                        if (printable) {
                            char text[48] = { 0 };
                            memcpy(text, data + offset + 24, word);
                            strings++;
                            size_t i = 0;
                            for (; i < used; i++) if (strcmp(entries[i].text, text) == 0) { entries[i].count++; break; }
                            if (i == used && used < (1 << 16)) { strcpy(entries[used].text, text); entries[used++].count = 1; }
                        }
                    }
                }
                offset += size;
            }
        }
        free(data);
    }
    printf("string-like live cells %llu, 24-byte zero cells %llu, distinct %zu\n",
        (unsigned long long)strings, (unsigned long long)zero24, used);
    qsort(entries, used, sizeof(Entry), cmp);
    for (size_t i = 0; i < used && i < 30; i++) printf("%9llu  \"%s\"\n", (unsigned long long)entries[i].count, entries[i].text);
    return 0;
}
