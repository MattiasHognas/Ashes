// usage: rcsites <linkmap>
// Reads rcwalk's lines ("<addr> <size> <shape> <site-hex>") or rcmark's (the same with an R or U
// column) on standard input and replaces the site address with the function of the link map
// (lines "<start-hex> <size-hex> <name>") that contains it, or "?" when none does: prints
// "<size> <shape> <function> <addr-hex> <R|U|L|-> <via>", "-" when the input had no reachability
// column. A cell a copy routine (a normalizer, a copy-out helper) allocated in a program built
// with ASHES_RC_SITES=1 is named by the function that called the routine, and <via> names the
// routine; <via> is "-" for every other cell.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint64_t start, size; char *name; } Fn;
static Fn *fns; static size_t nfns, capfns;

static int cmpfn(const void *a, const void *b) {
    const Fn *x = a, *y = b;
    return x->start < y->start ? -1 : x->start > y->start;
}

static const char *resolve(uint64_t site) {
    size_t lo = 0, hi = nfns;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (fns[mid].start <= site) lo = mid + 1; else hi = mid; }
    return lo > 0 && site < fns[lo - 1].start + fns[lo - 1].size ? fns[lo - 1].name : "?";
}

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: rcsites <linkmap> < cells\n"); return 2; }
    FILE *m = fopen(argv[1], "r");
    if (!m) { perror("map"); return 1; }
    char line[4096];
    while (fgets(line, sizeof line, m)) {
        char name[4000]; unsigned long long s, z;
        if (sscanf(line, "%llx %llx %3999s", &s, &z, name) != 3) continue;
        if (nfns == capfns) { capfns = capfns ? capfns * 2 : 4096; fns = realloc(fns, capfns * sizeof(Fn)); }
        fns[nfns++] = (Fn){ s, z, strdup(name) };
    }
    qsort(fns, nfns, sizeof(Fn), cmpfn);
    while (fgets(line, sizeof line, stdin)) {
        unsigned long long addr, size, site; char shape[64], reach[4] = "-";
        if (sscanf(line, "%llx %llu %63s %llx %3s", &addr, &size, shape, &site, reach) < 4) continue;
        // A copy routine's allocation packs the routine's own site above the calling function.
        const char *name, *via = "-";
        if ((site >> 48) != 0) { name = resolve(site & 0xffffffffULL); via = resolve(site >> 32); }
        else name = resolve(site);
        printf("%llu %s %s %llx %s %s\n", size, shape, name, addr, reach, via);
    }
    return 0;
}
