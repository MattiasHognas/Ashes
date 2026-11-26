// usage: rcsites <dump> <linkmap> [base-hex]
// Reads rcwalk's lines ("<addr> <size> <shape> <site-hex>") on standard input and replaces the site
// address with the function of the link map (lines "<start-hex> <size-hex> <name>") that contains
// it, or "?" when none does: prints "<size> <shape> <function>".
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
        unsigned long long addr, size, site; char shape[64];
        if (sscanf(line, "%llx %llu %63s %llx", &addr, &size, shape, &site) != 4) continue;
        const char *name = "?";
        size_t lo = 0, hi = nfns;
        while (lo < hi) { size_t mid = (lo + hi) / 2; if (fns[mid].start <= site) lo = mid + 1; else hi = mid; }
        if (lo > 0 && site < fns[lo - 1].start + fns[lo - 1].size) name = fns[lo - 1].name;
        printf("%llu %s %s\n", size, shape, name);
    }
    return 0;
}
