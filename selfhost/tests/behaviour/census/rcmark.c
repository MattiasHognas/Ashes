// usage: rcmark <dump> <roots-dir> [base-hex]
// rcwalk with a reachability column: prints one line per live RC cell,
// "<header-addr> <total-size> <shape> <site-hex> <R|U>", where R means some root word reaches the
// cell, directly or through the payload words of other reached cells, and U means nothing does.
// The roots are the files exitdump.sh writes into <roots-dir>: every "*.bin" (the program's
// writable memory outside the reference-counted range) scanned as aligned 8-byte words, and
// "registers.txt", whose every 0x token is a word; a stack's words below its thread's stack pointer,
// are dead and skipped (RCMARK_RED_ZONE=<bytes> keeps that many below it, for a thread stopped in a
// leaf function; the exit_group caller is the entry function or a function that calls, so 0 by
// default). A word reaches a cell when, with its top 16 bits
// cleared, it equals the cell's payload address or its RC header address.
// The mark is conservative: a stale word (a dead stack slot, arena garbage, a freed libc chunk)
// also reaches a cell, and payload words are scanned whatever their type. So U is a sure leak and
// R an upper bound on what the program still held at exit. Pointers into the middle of a payload
// are not followed.
#include <dirent.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

// A released block keeps its RC header: the release writes only the free-list link (the next
// free block, or 0 at the list's end) over the prefix's second word. In a program built with
// ASHES_RC_SITES=1 that word is the allocation site, a code address, in every block in use, so a
// block whose word is 0 or a region address is free. sitesBuild says whether the dump is of such
// a program: some block among the first million carries a code address.
static int sitesBuild = -1;
static int detectSites(const uint8_t *d, uint64_t len, uint64_t base) {
    uint64_t o = 0, seen = 0;
    while (o + 32 <= len && seen < 1000000) {
        const uint64_t *w = (const uint64_t *)(d + o);
        uint64_t bytes = w[0];
        if (bytes < 32 || (bytes & 15) != 0) { o += 16; continue; }
        if (w[1] != 0 && w[1] < base) return 1;
        seen++; o += bytes;
    }
    return 0;
}
static int releasedBlock(uint64_t site, uint64_t base) {
    return sitesBuild == 1 && (site == 0 || (site >= base && site < base + 0x40000000000ULL));
}

static const uint8_t *d;
static uint64_t len, base, slots;
static uint8_t *live, *marked, *inner;
static uint64_t *stack, depth, capacity, rootWords, rootBytes, directMarks;

static int bit(const uint8_t *map, uint64_t i) { return (map[i >> 3] >> (i & 7)) & 1; }
static void set(uint8_t *map, uint64_t i) { map[i >> 3] |= (uint8_t)(1 << (i & 7)); }

// The slot of a payload address: one per 16 bytes of the dump.
static const char *rootName; static uint64_t rootOffset; static int verbose;
static void reach(uint64_t word, int fromRoot) {
    uint64_t w = word & 0xffffffffffffULL;
    if (w < base + 32 || w >= base + len || (w & 15) != 0) return;
    uint64_t i = (w - base) / 16;
    if (!bit(live, i)) { if (i + 1 < slots && bit(live, i + 1)) i++; else return; }
    if (bit(marked, i)) return;
    set(marked, i);
    if (fromRoot) { directMarks++; if (verbose) fprintf(stderr, "rcmark: root %s+%llx reaches %llx\n", rootName, (unsigned long long)rootOffset, (unsigned long long)(base + i * 16)); }
    if (depth == capacity) { capacity = capacity ? capacity * 2 : 1 << 20; stack = realloc(stack, capacity * 8); if (!stack) { perror("rcmark: stack"); exit(1); } }
    stack[depth++] = i;
}

static void drain(void) {
    while (depth > 0) {
        uint64_t i = stack[--depth];
        const uint64_t *header = (const uint64_t *)(d + i * 16 - 16);
        uint64_t size = header[1];
        if (i * 16 + size > len) size = len - i * 16;
        const uint64_t *payload = header + 2;
        for (uint64_t k = 0; k < size / 8; k++) reach(payload[k], 0);
    }
}

static int plausible(const uint64_t *w, uint64_t o, uint64_t *bytesOut) {
    uint64_t bytes = w[0];
    *bytesOut = bytes;
    if (bytes == 0) return 0;
    if (bytes < 32 || (bytes & 15) != 0 || o + bytes > len + 4096) return -1;
    uint64_t count = w[2], size = w[3];
    if (releasedBlock(w[1], base)) return 3;
    int liveCount = (count >= 1 && count < (1ULL << 32)) || count == (1ULL << 62);
    return liveCount && size <= bytes - 32 && size + 15 >= bytes - 32 ? 1 : 2;
}

static uint64_t sps[4096], redZone; static int nsps; static uint64_t deadStackBytes;

// Registers first: every 0x token is a root word, and each thread's stack pointer ("rsp" on x64,
// "sp" on arm64) bounds its stack, whose words below it (less the red zone) are dead.
static void scanRegisters(const char *dir) {
    char path[8192], token[256], previous[256] = "";
    snprintf(path, sizeof path, "%s/registers.txt", dir);
    FILE *f = fopen(path, "r");
    if (!f) return;
    rootName = "registers"; rootOffset = 0;
    while (fscanf(f, "%255s", token) == 1) {
        if (token[0] == '0' && token[1] == 'x') {
            uint64_t v = strtoull(token, 0, 16);
            if ((strcmp(previous, "rsp") == 0 || strcmp(previous, "sp") == 0) && nsps < 4096) sps[nsps++] = v;
            reach(v, 1); rootWords++;
        }
        snprintf(previous, sizeof previous, "%s", token);
    }
    fclose(f);
    drain();
}

static void scanRoots(const char *dir) {
    scanRegisters(dir);
    DIR *dp = opendir(dir);
    if (!dp) { perror("rcmark: roots"); exit(1); }
    struct dirent *e;
    char path[8192];
    while ((e = readdir(dp))) {
        size_t n = strlen(e->d_name);
        if (n <= 4 || strcmp(e->d_name + n - 4, ".bin") != 0) continue;
        snprintf(path, sizeof path, "%s/%s", dir, e->d_name);
        int fd = open(path, O_RDONLY);
        struct stat st;
        if (fd < 0 || fstat(fd, &st) != 0 || st.st_size < 8) { if (fd >= 0) close(fd); continue; }
        const uint64_t *w = mmap(0, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
        if (w == MAP_FAILED) { perror("rcmark: mmap root"); exit(1); }
        uint64_t start = strtoull(e->d_name, 0, 16), end = start + (uint64_t)st.st_size, first = 0;
        for (int s = 0; s < nsps; s++)
            if (sps[s] > start && sps[s] <= end) { uint64_t live = sps[s] - redZone > start ? sps[s] - redZone : start; first = (live - start) / 8; }
        deadStackBytes += first * 8;
        rootName = e->d_name;
        for (uint64_t k = first; k < (uint64_t)st.st_size / 8; k++) { rootOffset = k * 8; reach(w[k], 1); if (depth > (1 << 20)) drain(); }
        rootWords += (uint64_t)st.st_size / 8 - first; rootBytes += (uint64_t)st.st_size;
        drain();
        munmap((void *)w, (size_t)st.st_size); close(fd);
    }
    closedir(dp);
}

// Every unreachable cell another unreachable cell's payload names: what is left unflagged is
// the top of a leaked structure, the cell whose missing release leaked everything below it.
static void flagInner(void) {
    uint64_t o = 0, bytes;
    while (o + 32 <= len) {
        const uint64_t *w = (const uint64_t *)(d + o);
        int kind = plausible(w, o, &bytes);
        if (kind <= 0) { o += 16; continue; }
        if (kind == 1 && !bit(marked, (o + 32) / 16)) {
            uint64_t size = w[3];
            if (o + 32 + size > len) size = len - o - 32;
            for (uint64_t k = 0; k < size / 8; k++) {
                uint64_t t = w[4 + k] & 0xffffffffffffULL;
                if (t < base + 32 || t >= base + len || (t & 15) != 0) continue;
                uint64_t i = (t - base) / 16;
                if (!bit(live, i)) { if (i + 1 < slots && bit(live, i + 1)) i++; else continue; }
                if (i != (o + 32) / 16) set(inner, i);
            }
        }
        o += bytes;
    }
}

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: rcmark <dump> <roots-dir> [base-hex]\n"); return 2; }
    base = argc > 3 ? strtoull(argv[3], 0, 16) : 0x100000000000ULL;
    verbose = getenv("RCMARK_VERBOSE") != 0;
    redZone = getenv("RCMARK_RED_ZONE") ? strtoull(getenv("RCMARK_RED_ZONE"), 0, 10) & ~7ULL : 0;
    int fd = open(argv[1], O_RDONLY);
    if (fd < 0) { perror("open"); return 1; }
    struct stat st; fstat(fd, &st);
    len = (uint64_t)st.st_size;
    d = mmap(0, len, PROT_READ, MAP_PRIVATE, fd, 0);
    if (d == MAP_FAILED) { perror("mmap"); return 1; }
    slots = len / 16 + 1;
    live = calloc(slots / 8 + 1, 1); marked = calloc(slots / 8 + 1, 1); inner = calloc(slots / 8 + 1, 1);
    if (!live || !marked || !inner) { perror("rcmark: bitmaps"); return 1; }
    sitesBuild = detectSites(d, len, base);
    uint64_t o = 0, bytes, cells = 0, released = 0;
    while (o + 32 <= len) {
        int kind = plausible((const uint64_t *)(d + o), o, &bytes);
        if (kind <= 0) { o += 16; continue; }
        if (kind == 1) { set(live, (o + 32) / 16); cells++; }
        if (kind == 3) released++;
        o += bytes;
    }
    scanRoots(argv[2]);
    flagInner();
    uint64_t reached = 0, reachedBytes = 0, lostBytes = 0, leakRoots = 0;
    o = 0;
    while (o + 32 <= len) {
        const uint64_t *w = (const uint64_t *)(d + o);
        int kind = plausible(w, o, &bytes);
        if (kind <= 0) { o += 16; continue; }
        if (kind == 1) {
            uint64_t size = w[3], first = size >= 8 ? w[4] : 0, i = (o + 32) / 16;
            char shape[32];
            if (first >= base && first < base + 0x40000000000ULL) snprintf(shape, sizeof shape, "ptr");
            else if (first < 100000) snprintf(shape, sizeof shape, "%llu", (unsigned long long)first);
            else snprintf(shape, sizeof shape, "big");
            int r = bit(marked, i);
            char reach = r ? 'R' : bit(inner, i) ? 'U' : 'L';
            if (r) { reached++; reachedBytes += size + 16; } else { lostBytes += size + 16; if (reach == 'L') leakRoots++; }
            printf("%llx %llu %s %llx %c\n", (unsigned long long)(base + o + 16), (unsigned long long)(size + 16), shape, (unsigned long long)w[1], reach);
        }
        o += bytes;
    }
    fprintf(stderr, "rcmark: %llu live cells, %llu reachable (%llu bytes), %llu unreachable (%llu bytes) of which %llu leak roots; %llu root bytes (%llu of them dead stack below a stack pointer, skipped), %llu root words, %llu cells reached directly from a root\n",
        (unsigned long long)cells, (unsigned long long)reached, (unsigned long long)reachedBytes, (unsigned long long)(cells - reached), (unsigned long long)lostBytes, (unsigned long long)leakRoots,
        (unsigned long long)rootBytes, (unsigned long long)deadStackBytes, (unsigned long long)rootWords, (unsigned long long)directMarks);
    fprintf(stderr, "rcmark: %s; %llu released blocks skipped\n", sitesBuild ? "allocation sites recorded" : "no allocation sites, released blocks cannot be told from live ones", (unsigned long long)released);
    fprintf(stderr, "rcmark: U is a sure leak; R is an upper bound on what was still held at exit (the mark is conservative)\n");
    return 0;
}
