// usage: rcwalk <dump> [base-hex]
// Walks a dump of the reference-counted region, which starts at the region base (0x100000000000
// unless given), block by block: each block is a 16-byte allocator prefix (block bytes, free-list
// link or, with ASHES_RC_SITES, the allocation site), then an RC header (count, payload size), then
// the payload. Prints one line per live RC cell: "<header-addr> <total-size> <shape> <site-hex>".
// total-size is header plus payload (what the census classes call the size). shape is the first
// payload word as "ptr" (an address in the region), "0", or the decimal word when small.
// A zero prefix is a gap (a page rounding, a stretch remainder or a returned paged block) and is
// skipped 16 bytes at a time. A block whose header is not a plausible RC header (a scratch buffer
// sharing the allocator, or a freed cell) is skipped by its prefix size.
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: rcwalk <dump> [base-hex]\n"); return 2; }
    uint64_t base = argc > 2 ? strtoull(argv[2], 0, 16) : 0x100000000000ULL;
    int fd = open(argv[1], O_RDONLY);
    if (fd < 0) { perror("open"); return 1; }
    struct stat st; fstat(fd, &st);
    uint64_t len = (uint64_t)st.st_size;
    const uint8_t *d = mmap(0, len, PROT_READ, MAP_PRIVATE, fd, 0);
    if (d == MAP_FAILED) { perror("mmap"); return 1; }
    uint64_t o = 0, live = 0, skipped = 0, lastEnd = 0;
    while (o + 32 <= len) {
        const uint64_t *w = (const uint64_t *)(d + o);
        uint64_t bytes = w[0];
        if (bytes == 0) { o += 16; continue; }
        if (bytes < 32 || (bytes & 15) != 0 || o + bytes > len + 4096) { o += 16; skipped++; continue; }
        uint64_t site = w[1], count = w[2], size = w[3];
        int liveCount = (count >= 1 && count < (1ULL << 32)) || count == (1ULL << 62);
        uint64_t payload = bytes - 32;
        if (liveCount && size <= payload && size + 15 >= payload) {
            uint64_t first = size >= 8 ? w[4] : 0;
            char shape[32];
            if (first >= base && first < base + 0x40000000000ULL) snprintf(shape, sizeof shape, "ptr");
            else if (first < 100000) snprintf(shape, sizeof shape, "%llu", (unsigned long long)first);
            else snprintf(shape, sizeof shape, "big");
            printf("%llx %llu %s %llx\n", (unsigned long long)(base + o + 16), (unsigned long long)(size + 16), shape, (unsigned long long)site);
            live++;
        }
        o += bytes;
        lastEnd = o;
    }
    fprintf(stderr, "rcwalk: last block ends at %llu of %llu dumped bytes%s\n", (unsigned long long)lastEnd, (unsigned long long)len, len - lastEnd < (1ULL << 30) ? " (WARNING: within 1 GiB of the dump end, dump more)" : "");
    fprintf(stderr, "rcwalk: %llu live cells, %llu implausible prefixes, walked %llu bytes\n",
        (unsigned long long)live, (unsigned long long)skipped, (unsigned long long)o);
    return 0;
}
