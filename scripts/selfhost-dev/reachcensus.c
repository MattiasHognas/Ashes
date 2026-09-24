// usage: reachcensus <core> [stack-start-hex stack-end-hex] ; reads an ELF core file of a running Ashes program
// and answers how much of the reference-counted heap is leaked. Every loadable segment inside the
// reference-counted region (from 0x100000000000) is walked cell by cell ([count:8][size:8][payload]); every
// other segment (the stack, the arenas, static data) is a root area, scanned word by word for pointers into
// the heap, conservatively. Cells live by count but reachable from no root area are leaked for certain: a
// lower bound, since a stale word in unused arena memory still counts as a pointer. With the stack's range
// given, it also reports what is reachable from the stack and static data without going through anonymous
// mappings (the arenas), which bounds what only arena memory holds.
#include <elf.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#define BASE 0x100000000000ULL
#define LIMIT 0x140000000000ULL

typedef struct { uint64_t vaddr, size; const uint8_t *data; } Segment;
typedef struct { uint64_t addr; uint32_t size; uint8_t live, mark; } Cell;

static Segment *heapsegs, *rootsegs; static size_t nheap, nroot;
static Cell *cells; static size_t ncells, capcells;

static void addcell(uint64_t addr, uint32_t size, int live) {
    if (ncells == capcells) { capcells = capcells ? capcells * 2 : 1 << 24; cells = realloc(cells, capcells * sizeof(Cell)); }
    cells[ncells].addr = addr; cells[ncells].size = size; cells[ncells].live = (uint8_t)live; cells[ncells].mark = 0; ncells++;
}

// The cell a pointer names: its header address or its payload address.
static long findcell(uint64_t v) {
    if (v < BASE || v >= LIMIT) return -1;
    size_t lo = 0, hi = ncells;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (cells[mid].addr <= v) lo = mid + 1; else hi = mid; }
    if (lo == 0) return -1;
    size_t i = lo - 1;
    if (v != cells[i].addr && v != cells[i].addr + 16) return -1;
    return (long)i;
}

static const uint8_t *heapbytes(uint64_t addr, uint64_t size) {
    for (size_t s = 0; s < nheap; s++)
        if (addr >= heapsegs[s].vaddr && addr + size <= heapsegs[s].vaddr + heapsegs[s].size)
            return heapsegs[s].data + (addr - heapsegs[s].vaddr);
    return NULL;
}

static int plausible(const uint8_t *p, uint64_t remaining) {
    if (remaining < 24) return 0;
    uint64_t count, size; memcpy(&count, p, 8); memcpy(&size, p + 8, 8);
    if (size < 24 || (size & 7) || size > (64ULL << 20) || size > remaining) return 0;
    int live = count > 0 && count < (1ULL << 40);
    int freed = count == 0 || (count >= BASE && count < LIMIT);
    return live || freed || count == (1ULL << 62);
}

static size_t *stack; static size_t sp, capstack;
static void push(size_t i) {
    if (sp == capstack) { capstack = capstack ? capstack * 2 : 1 << 20; stack = realloc(stack, capstack * sizeof(size_t)); }
    stack[sp++] = i;
}

// Marks everything reachable from the pushed cells with the given bit.
static void propagate(uint8_t bit) {
    while (sp > 0) {
        size_t c = stack[--sp];
        const uint8_t *p = heapbytes(cells[c].addr, cells[c].size);
        if (!p) continue;
        for (uint64_t w = 16; w + 8 <= cells[c].size; w += 8) {
            uint64_t v; memcpy(&v, p + w, 8);
            long t = findcell(v);
            if (t >= 0 && cells[t].live && !(cells[t].mark & bit)) { cells[t].mark |= bit; push((size_t)t); }
        }
    }
}

static void scanroots(const Segment *seg, uint8_t bit) {
    for (uint64_t w = 0; w + 8 <= seg->size; w += 8) {
        uint64_t v; memcpy(&v, seg->data + w, 8);
        long t = findcell(v);
        if (t >= 0 && cells[t].live && !(cells[t].mark & bit)) { cells[t].mark |= bit; push((size_t)t); }
    }
    propagate(bit);
}

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: reachcensus <core> [stack-start-hex stack-end-hex]\n"); return 1; }
    uint64_t stacklo = argc > 3 ? strtoull(argv[2], NULL, 16) : 0, stackhi = argc > 3 ? strtoull(argv[3], NULL, 16) : 0;
    int fd = open(argv[1], O_RDONLY); if (fd < 0) { perror("open"); return 1; }
    struct stat st; fstat(fd, &st);
    const uint8_t *file = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (file == MAP_FAILED) { perror("mmap"); return 1; }
    const Elf64_Ehdr *eh = (const Elf64_Ehdr *)file;
    const Elf64_Phdr *ph = (const Elf64_Phdr *)(file + eh->e_phoff);
    heapsegs = calloc(eh->e_phnum, sizeof(Segment)); rootsegs = calloc(eh->e_phnum, sizeof(Segment));
    uint64_t heapbytes_total = 0, rootbytes_total = 0;
    for (int i = 0; i < eh->e_phnum; i++) {
        if (ph[i].p_type != PT_LOAD || ph[i].p_filesz == 0) continue;
        Segment seg = { ph[i].p_vaddr, ph[i].p_filesz, file + ph[i].p_offset };
        // The uncommitted part of the reservation is inaccessible and holds nothing.
        if (seg.vaddr >= BASE && seg.vaddr < LIMIT) { if (ph[i].p_flags & PF_W) { heapsegs[nheap++] = seg; heapbytes_total += seg.size; } }
        else if (ph[i].p_flags & PF_W) { rootsegs[nroot++] = seg; rootbytes_total += seg.size; }
    }
    for (size_t s = 0; s < nheap; s++) {
        uint64_t off = 0;
        while (off + 24 <= heapsegs[s].size) {
            const uint8_t *p = heapsegs[s].data + off;
            if (!plausible(p, heapsegs[s].size - off)) { off += 8; continue; }
            uint64_t count, size; memcpy(&count, p, 8); memcpy(&size, p + 8, 8);
            addcell(heapsegs[s].vaddr + off, (uint32_t)size, count > 0 && count < (1ULL << 40));
            off += size;
        }
    }
    uint64_t livebytes = 0; size_t livecells = 0;
    for (size_t i = 0; i < ncells; i++) if (cells[i].live) { livebytes += cells[i].size; livecells++; }
    // Bit 1: reachable from any root area. Bit 2: reachable from the stack and the small static areas only.
    for (size_t s = 0; s < nroot; s++) scanroots(&rootsegs[s], 1);
    for (size_t s = 0; s < nroot; s++) {
        int isstack = stackhi > stacklo && rootsegs[s].vaddr >= stacklo && rootsegs[s].vaddr < stackhi;
        int isstatic = rootsegs[s].vaddr < 0x10000000ULL;
        if (isstack || isstatic) scanroots(&rootsegs[s], 2);
    }
    uint64_t reach = 0, reachstack = 0, leaked = 0; size_t leakedcells = 0;
    static uint64_t leakbysize[4096]; static size_t leakcount[4096];
    for (size_t i = 0; i < ncells; i++) {
        if (!cells[i].live) continue;
        if (cells[i].mark & 1) reach += cells[i].size; else {
            leaked += cells[i].size; leakedcells++;
            uint32_t b = cells[i].size < 4096 * 8 ? cells[i].size / 8 : 4095;
            leakbysize[b] += cells[i].size; leakcount[b]++;
        }
        if (cells[i].mark & 2) reachstack += cells[i].size;
    }
    double mb = 1048576.0;
    printf("heap segments %zu (%.1f MB)  root areas %zu (%.1f MB)  cells %zu\n", nheap, heapbytes_total / mb, nroot, rootbytes_total / mb, ncells);
    printf("live by count      %10.1f MB  %zu cells\n", livebytes / mb, livecells);
    printf("reachable          %10.1f MB  (%.1f%%)  from the stack, static data and arena memory, conservatively\n", reach / mb, 100.0 * reach / (livebytes ? livebytes : 1));
    printf("leaked for certain %10.1f MB  (%.1f%%)  %zu cells no root area reaches\n", leaked / mb, 100.0 * leaked / (livebytes ? livebytes : 1), leakedcells);
    if (stackhi > stacklo)
        printf("from stack/static  %10.1f MB  (%.1f%%)  without going through arena memory; the rest of the reachable part is held by arena memory only\n", reachstack / mb, 100.0 * reachstack / (livebytes ? livebytes : 1));
    // REACH_SAMPLE=<size>: payload addresses of a few leaked cells of that size, spread over the heap, to
    // check by hand (gdb's `find` over the core) that nothing points at them.
    if (getenv("REACH_SAMPLE")) {
        uint32_t wanted = (uint32_t)atoi(getenv("REACH_SAMPLE")); size_t total = 0, seen = 0;
        for (size_t i = 0; i < ncells; i++) if (cells[i].live && !(cells[i].mark & 1) && cells[i].size == wanted) total++;
        size_t step = total > 6 ? total / 6 : 1;
        for (size_t i = 0; i < ncells; i++) {
            if (!cells[i].live || (cells[i].mark & 1) || cells[i].size != wanted) continue;
            if (seen % step == 0) printf("sample leaked payload %llx\n", (unsigned long long)(cells[i].addr + 16));
            seen++;
        }
    }
    // The leaked part as a forest: a leaked cell no other leaked cell points at is a root of garbage, the
    // value whose release is missing. Each root claims the leaked cells it reaches that no earlier root
    // claimed, and the claims are summed by the root's size and whether its first word is a pointer.
    {
        uint32_t *indegree = calloc(ncells, 4);
        for (size_t i = 0; i < ncells; i++) {
            if (!cells[i].live || (cells[i].mark & 1)) continue;
            const uint8_t *p = heapbytes(cells[i].addr, cells[i].size);
            for (uint64_t w = 16; p && w + 8 <= cells[i].size; w += 8) {
                uint64_t v; memcpy(&v, p + w, 8);
                long t = findcell(v);
                if (t >= 0 && (size_t)t != i) indegree[t]++;
            }
        }
        typedef struct { uint32_t size; int ptr; size_t roots; uint64_t bytes; uint64_t sample[2]; } Class;
        static Class classes[8192]; size_t nclasses = 0;
        // REACH_TOP=<size>: the roots of that size claiming the most, and how their claims are spread.
        uint32_t topsize = getenv("REACH_TOP") ? (uint32_t)atoi(getenv("REACH_TOP")) : 0;
        uint64_t topaddr[600] = { 0 }, topbytes[600] = { 0 }; size_t spread[5] = { 0 }; uint64_t spreadbytes[5] = { 0 };
        for (size_t i = 0; i < ncells; i++) {
            if (!cells[i].live || (cells[i].mark & 1) || indegree[i] != 0) continue;
            const uint8_t *p = heapbytes(cells[i].addr, cells[i].size);
            uint64_t first = 0; if (p) memcpy(&first, p + 16, 8);
            int isptr = findcell(first) >= 0;
            size_t k = 0;
            while (k < nclasses && !(classes[k].size == cells[i].size && classes[k].ptr == isptr)) k++;
            if (k == nclasses) { if (nclasses == 8192) continue; classes[nclasses].size = cells[i].size; classes[nclasses].ptr = isptr; nclasses++; }
            if (classes[k].roots < 2 || (classes[k].roots % 5003) == 0) classes[k].sample[classes[k].roots % 2] = cells[i].addr + 16;
            classes[k].roots++;
            uint64_t before = classes[k].bytes;
            cells[i].mark |= 4; push(i);
            while (sp > 0) {
                size_t c = stack[--sp]; classes[k].bytes += cells[c].size;
                const uint8_t *q = heapbytes(cells[c].addr, cells[c].size);
                for (uint64_t w = 16; q && w + 8 <= cells[c].size; w += 8) {
                    uint64_t v; memcpy(&v, q + w, 8);
                    long t = findcell(v);
                    if (t >= 0 && cells[t].live && !(cells[t].mark & 5)) { cells[t].mark |= 4; push((size_t)t); }
                }
            }
            // REACH_STRINGS=<size>: the first 40 characters of every leaked string root of that size, one per
            // line, for `sort | uniq -c` to tally which texts are copied and left behind.
            if (getenv("REACH_STRINGS") && cells[i].size == (uint32_t)atoi(getenv("REACH_STRINGS")) && !isptr && p) {
                uint64_t len; memcpy(&len, p + 16, 8);
                if (len > 0 && len + 24 <= cells[i].size) printf("string root %.*s\n", (int)(len < 40 ? len : 40), (const char *)p + 24);
            }
            // REACH_SHAPE=<size>: every root of that size, one line per root naming what its first two words hold:
            // the size of the cell a word points at, or `=` and the word itself when it is not a cell (for an ADT's
            // first word, its tag), for `sort | uniq -c` to tell a list cell from a pair and name what it holds.
            if (getenv("REACH_SHAPE") && cells[i].size == (uint32_t)atoi(getenv("REACH_SHAPE")) && p) {
                uint64_t second = 0; memcpy(&second, p + 24, 8);
                long a = findcell(first), b = findcell(second);
                char left[32], right[32];
                if (a >= 0) snprintf(left, sizeof left, "%u", cells[a].size); else snprintf(left, sizeof left, "=%lld", (long long)first);
                if (b >= 0) snprintf(right, sizeof right, "%u", cells[b].size); else snprintf(right, sizeof right, "=%lld", (long long)second);
                printf("shape root %s %s @ %llx\n", left, right, (unsigned long long)(cells[i].addr + 16));
            }
            if (topsize && cells[i].size == topsize && isptr) {
                uint64_t got = classes[k].bytes - before;
                int band = got < 128 ? 0 : got < 1024 ? 1 : got < 16384 ? 2 : got < 262144 ? 3 : 4;
                spread[band]++; spreadbytes[band] += got;
                int low = 0; for (int t = 1; t < 600; t++) if (topbytes[t] < topbytes[low]) low = t;
                if (got > topbytes[low]) { topbytes[low] = got; topaddr[low] = cells[i].addr + 16; }
            }
        }
        if (topsize) {
            const char *bands[5] = { "<128 B", "<1 KB", "<16 KB", "<256 KB", ">=256 KB" };
            printf("roots of size %u with a pointer first, by claim:\n", topsize);
            for (int b = 0; b < 5; b++) printf("  %-9s %9zu roots claim %8.1f MB\n", bands[b], spread[b], spreadbytes[b] / 1048576.0);
            for (int t = 0; t < 600; t++) if (topbytes[t]) printf("  top root payload %llx claims %.1f MB\n", (unsigned long long)topaddr[t], topbytes[t] / 1048576.0);
        }
        uint64_t claimed = 0; for (size_t k = 0; k < nclasses; k++) claimed += classes[k].bytes;
        printf("garbage roots by shape (each claims what it reaches first; %.1f MB of the leak is in cycles only):\n", (leaked - claimed) / mb);
        for (int round = 0; round < 12; round++) {
            long best = -1;
            for (size_t k = 0; k < nclasses; k++) if (classes[k].bytes > 0 && (best < 0 || classes[k].bytes > classes[best].bytes)) best = (long)k;
            if (best < 0) break;
            printf("  root size %4u first word %s  %9zu roots claim %8.1f MB   e.g. %llx %llx\n", classes[best].size, classes[best].ptr ? "ptr" : "int",
                classes[best].roots, classes[best].bytes / mb, (unsigned long long)classes[best].sample[0], (unsigned long long)classes[best].sample[1]);
            classes[best].bytes = 0;
        }
    }
    printf("leaked cells by size:\n");
    for (int round = 0; round < 8; round++) {
        int best = -1;
        for (int b = 0; b < 4096; b++) if (leakbysize[b] > 0 && (best < 0 || leakbysize[b] > leakbysize[best])) best = b;
        if (best < 0) break;
        printf("  size %5d  %9zu cells  %8.1f MB\n", best * 8, leakcount[best], leakbysize[best] / mb);
        leakbysize[best] = 0;
    }
    return 0;
}
