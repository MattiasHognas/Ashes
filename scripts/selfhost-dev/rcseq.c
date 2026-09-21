// usage: rcseq <dump> <base-hex> <skip-cells> <n> ; synchronizes like rccensus2, skips <skip-cells> cells,
// then prints <n> consecutive cells: address, count, size, payload words and printable bytes.
#include <ctype.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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
        if (!plausible(data, len, offset)) return 0;
        uint64_t size; memcpy(&size, data + offset + 8, 8);
        offset += size;
    }
    return 1;
}

int main(int argc, char **argv) {
    FILE *in = fopen(argv[1], "rb"); if (!in) return 1;
    uint64_t base = strtoull(argv[2], 0, 16);
    long skip = atol(argv[3]), n = atol(argv[4]);
    fseek(in, 0, SEEK_END); long len = ftell(in); fseek(in, 0, SEEK_SET);
    uint8_t *data = malloc(len); if (fread(data, 1, len, in) != (size_t)len) return 1; fclose(in);
    long offset = 0;
    while (offset + 16 <= len && !chain(data, len, offset)) offset += 8;
    for (long i = 0; i < skip + n && offset + 16 <= len && plausible(data, len, offset); i++) {
        uint64_t count, size;
        memcpy(&count, data + offset, 8); memcpy(&size, data + offset + 8, 8);
        if (i >= skip) {
            printf("%llx c=%llu s=%3llu |", (unsigned long long)(base + offset + 16), (unsigned long long)count, (unsigned long long)size);
            for (uint64_t w = 16; w < size && w < 16 + 6 * 8; w += 8) {
                uint64_t v; memcpy(&v, data + offset + w, 8);
                printf(" %llx", (unsigned long long)v);
            }
            printf(" | ");
            for (uint64_t b = 24; b < size && b < 24 + 24; b++) putchar(isprint(data[offset + b]) ? data[offset + b] : '.');
            putchar('\n');
        }
        offset += size;
    }
    return 0;
}
