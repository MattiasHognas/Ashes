// usage: rcpeek <dir> <depth> <address-hex>... ; reads full-<k>.bin chunks of the RC region and prints the cell at
// each address as a tree: its reference count, size and payload words, following pointer words into other cells
// down to <depth>. A cell that reads as a string ([length][printable bytes]) prints as its text; a small word
// prints as a number, which for an ADT cell's first word is its constructor tag.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define BASE 0x100000000000ULL
#define CHUNK (256ULL << 20)

static uint8_t *heap; static uint64_t heaplen;

static int cellat(uint64_t v, uint64_t *off) {
    if (v < BASE + 16 || v >= BASE + heaplen) return 0;
    uint64_t o = v - BASE;
    uint64_t count, size;
    // A pointer addresses the payload; the header sits 16 bytes before it.
    memcpy(&count, heap + o - 16, 8); memcpy(&size, heap + o - 8, 8);
    if (size < 24 || (size & 7) || size > (64ULL << 20) || o - 16 + size > heaplen) return 0;
    if (count == 0 || (count >= (1ULL << 40) && count != (1ULL << 62))) return 0;
    *off = o - 16;
    return 1;
}

static int astext(uint64_t off, char *out) {
    uint64_t size, n; memcpy(&size, heap + off + 8, 8); memcpy(&n, heap + off + 16, 8);
    if (n == 0 || n > 60 || 24 + n > size) return 0;
    const uint8_t *p = heap + off + 24;
    for (uint64_t c = 0; c < n; c++) if (p[c] < 32 || p[c] > 126) return 0;
    memcpy(out, p, n); out[n] = 0;
    return 1;
}

static void show(uint64_t off, int depth, int indent) {
    uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
    char text[64];
    if (astext(off, text)) { printf("%*s\"%s\" (count %llu)\n", indent, "", text, (unsigned long long)count); return; }
    printf("%*scell %llx count=%llu size=%llu\n", indent, "", (unsigned long long)(BASE + off + 16), (unsigned long long)count, (unsigned long long)size);
    for (uint64_t w = 16; w + 8 <= size && w < 16 + 8 * 24; w += 8) {
        uint64_t v, target; memcpy(&v, heap + off + w, 8);
        if (cellat(v, &target)) {
            if (depth > 0) show(target, depth - 1, indent + 4);
            else printf("%*s-> %llx\n", indent + 4, "", (unsigned long long)v);
        } else {
            printf("%*s%lld\n", indent + 4, "", (long long)v);
        }
    }
}

int main(int argc, char **argv) {
    if (argc < 4) { fprintf(stderr, "usage: rcpeek <dir> <depth> <address-hex>...\n"); return 1; }
    char path[4096]; int maxk = -1;
    for (int k = 0; k < 4096; k++) { snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (f) { fclose(f); maxk = k; } }
    if (maxk < 0) { fprintf(stderr, "no chunks\n"); return 1; }
    heaplen = (uint64_t)(maxk + 1) * CHUNK;
    heap = calloc(heaplen, 1);
    for (int k = 0; k <= maxk; k++) {
        snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (!f) continue;
        size_t got = fread(heap + (uint64_t)k * CHUNK, 1, CHUNK, f); (void)got; fclose(f);
    }
    int depth = atoi(argv[2]);
    for (int a = 3; a < argc; a++) {
        uint64_t v = strtoull(argv[a], NULL, 16), off;
        // A census prints a cell's header address; a pointer word holds its payload address. The header
        // reading is tried first, since that is what the census tools print.
        if (!cellat(v + 16, &off) && !cellat(v, &off)) { printf("%s: not a live cell\n", argv[a]); continue; }
        show(off, depth, 0);
    }
    return 0;
}
