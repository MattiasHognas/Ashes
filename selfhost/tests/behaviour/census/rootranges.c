// usage: rootranges <pid> <out-dir>
// Run from gdb while the program is stopped: writes the gdb commands that dump the program's root
// memory into <out-dir>/roots, one file per resident run "<start-hex>-<end-hex>.bin". The roots
// are every writable private mapping that is anonymous (the stacks, the arena, the libc heap) or
// belongs to the program's own executable (its .data and .bss); the reference-counted range
// (0x100000000000 to 0x140000000000), read-only mappings and other files' mappings are skipped.
// Only resident or swapped pages are dumped (from /proc/<pid>/pagemap), so a large reservation
// that was barely touched costs only its touched pages; a run is split at 1 GiB. Prints the total
// root bytes to stderr.
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static const uint64_t rcStart = 0x100000000000ULL, rcEnd = 0x140000000000ULL, page = 4096, piece = 1ULL << 30;
static uint64_t total = 0;

static void emit(const char *out, uint64_t start, uint64_t end) {
    for (uint64_t s = start; s < end; s += piece) {
        uint64_t e = end - s > piece ? s + piece : end;
        printf("dump binary memory %s/roots/%llx-%llx.bin %llu %llu\n", out, (unsigned long long)s, (unsigned long long)e, (unsigned long long)s, (unsigned long long)e);
        total += e - s;
    }
}

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: rootranges <pid> <out-dir>\n"); return 2; }
    char path[256], exe[4096];
    snprintf(path, sizeof path, "/proc/%s/exe", argv[1]);
    ssize_t n = readlink(path, exe, sizeof exe - 1);
    exe[n > 0 ? n : 0] = 0;
    snprintf(path, sizeof path, "/proc/%s/maps", argv[1]);
    FILE *maps = fopen(path, "r");
    snprintf(path, sizeof path, "/proc/%s/pagemap", argv[1]);
    int pagemap = open(path, O_RDONLY);
    if (!maps || pagemap < 0) { perror("rootranges: /proc"); return 1; }
    char line[8192];
    while (fgets(line, sizeof line, maps)) {
        unsigned long long start, end, offset, inode; char perms[8], dev[16], file[4096] = "";
        if (sscanf(line, "%llx-%llx %7s %llx %15s %llu %4095[^\n]", &start, &end, perms, &offset, dev, &inode, file) < 6) continue;
        char *f = file; while (*f == ' ') f++;
        if (perms[0] != 'r' || perms[1] != 'w' || perms[3] != 'p') continue;
        if (start < rcEnd && end > rcStart) continue;
        if (inode != 0 && strcmp(f, exe) != 0) continue;
        uint64_t runStart = 0; int inRun = 0;
        static uint64_t entries[4096]; uint64_t batchStart = 0, batchCount = 0;
        for (uint64_t a = start; a < end; a += page) {
            if (a >= batchStart + batchCount * page) {
                uint64_t want = (end - a) / page < 4096 ? (end - a) / page : 4096;
                ssize_t got = pread(pagemap, entries, want * 8, (off_t)(a / page * 8));
                batchStart = a; batchCount = got > 0 ? (uint64_t)got / 8 : 0;
                if (batchCount == 0) { entries[0] = 1ULL << 63; batchCount = 1; }
            }
            uint64_t entry = entries[(a - batchStart) / page];
            int present = (entry >> 62) != 0;
            if (present && !inRun) { runStart = a; inRun = 1; }
            if (!present && inRun) { emit(argv[2], runStart, a); inRun = 0; }
        }
        if (inRun) emit(argv[2], runStart, end);
    }
    fprintf(stderr, "rootranges: %llu root bytes\n", (unsigned long long)total);
    return 0;
}
