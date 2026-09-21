// usage: rcgraph <dir> [root-hex] ; reads full-<k>.bin chunks (chunk k at 0x100000000000 + k * 256 MB),
// rebuilds the RC cell graph (a live cell's payload word pointing at a cell's header or payload is an
// edge), finds the roots (live cells no live cell points at: held only from the stack, globals or
// arena, or leaked), and attributes every reachable cell to the first root that reaches it, roots
// taken oldest first. Prints totals and the 40 roots holding the most bytes. With a root address,
// instead prints the bytes reachable through each payload field of that cell (fields in order,
// first come).
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define BASE 0x100000000000ULL
#define CHUNK (256ULL << 20)

static uint8_t *heap; static uint64_t heaplen;
static uint64_t *coff; static uint32_t *csize; static uint64_t *ccount; static uint8_t *clive; static size_t ncells, capcells;
static uint32_t *estart; static uint32_t *edges; static uint64_t nedges;

static void addcell(uint64_t off, uint32_t size, uint64_t count, int live) {
    if (ncells == capcells) {
        capcells = capcells ? capcells * 2 : 1 << 24;
        coff = realloc(coff, capcells * 8); csize = realloc(csize, capcells * 4);
        ccount = realloc(ccount, capcells * 8); clive = realloc(clive, capcells);
    }
    coff[ncells] = off; csize[ncells] = size; ccount[ncells] = count; clive[ncells] = (uint8_t)live; ncells++;
}

static long findcell(uint64_t v) {
    if (v < BASE || v >= BASE + heaplen) return -1;
    uint64_t o = v - BASE;
    size_t lo = 0, hi = ncells;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (coff[mid] <= o) lo = mid + 1; else hi = mid; }
    if (lo == 0) return -1;
    size_t i = lo - 1;
    if (o >= coff[i] + csize[i]) return -1;
    if (o != coff[i] && o != coff[i] + 16) return -1;
    return (long)i;
}

static int plausible(uint64_t off) {
    if (off + 24 > heaplen) return 0;
    uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
    if (size < 24 || (size & 7) || size > (64ULL << 20) || off + size > heaplen) return 0;
    int live = count > 0 && count < (1ULL << 40);
    int freed = count == 0 || (count >= BASE && count < BASE + (4ULL << 40));
    int immortal = count == (1ULL << 62);
    return live || freed || immortal;
}

int main(int argc, char **argv) {
    char path[4096]; int maxk = -1;
    for (int k = 0; k < 4096; k++) { snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (f) { fclose(f); maxk = k; } }
    if (maxk < 0) { fprintf(stderr, "no chunks\n"); return 1; }
    heaplen = (uint64_t)(maxk + 1) * CHUNK;
    heap = calloc(heaplen, 1);
    for (int k = 0; k <= maxk; k++) {
        snprintf(path, sizeof path, "%s/full-%d.bin", argv[1], k); FILE *f = fopen(path, "rb"); if (!f) continue;
        size_t got = fread(heap + (uint64_t)k * CHUNK, 1, CHUNK, f); (void)got; fclose(f);
    }
    uint64_t off = 0;
    while (off + 24 <= heaplen) {
        if (!plausible(off)) { off += 8; continue; }
        uint64_t count, size; memcpy(&count, heap + off, 8); memcpy(&size, heap + off + 8, 8);
        addcell(off, (uint32_t)size, count, count > 0 && count < (1ULL << 40));
        off += size;
    }
    estart = calloc(ncells + 1, 4);
    for (int pass = 0; pass < 2; pass++) {
        uint64_t e = 0;
        for (size_t i = 0; i < ncells; i++) {
            if (pass == 1) estart[i] = (uint32_t)e;
            if (!clive[i]) continue;
            for (uint64_t w = 16; w + 8 <= csize[i]; w += 8) {
                uint64_t v; memcpy(&v, heap + coff[i] + w, 8);
                long t = findcell(v);
                if (t < 0 || (size_t)t == i || !clive[t]) continue;
                if (pass == 1) edges[e] = (uint32_t)t;
                e++;
            }
        }
        if (pass == 0) { nedges = e; edges = malloc((nedges + 1) * 4); }
        else estart[ncells] = (uint32_t)e;
    }
    if (argc > 3 && !strcmp(argv[2], "class")) {
        // group the roots of one cell size by what their first field is: a short string payload is
        // printed as its contents (a record's own name field), anything else as a shape marker
        uint32_t want = (uint32_t)strtoul(argv[3], 0, 10);
        uint32_t *indeg2 = calloc(ncells, 4);
        for (uint64_t e = 0; e < nedges; e++) indeg2[edges[e]]++;
        typedef struct { char key[64]; uint64_t n; uint64_t sample[3]; } K; static K keys[4096]; size_t nk = 0;
        for (size_t i = 0; i < ncells; i++) {
            if (!clive[i] || indeg2[i] || csize[i] != want) continue;
            { uint64_t w0; memcpy(&w0, heap + coff[i] + 16, 8); if (findcell(w0) < 0) continue; }
            char key[64]; size_t kn = 0; char first[40]; first[0] = 0;
            for (uint64_t w = 16; w + 8 <= csize[i] && kn < 24; w += 8) {
                uint64_t v; memcpy(&v, heap + coff[i] + w, 8);
                long t = findcell(v);
                char c = v == 0 ? '0' : t >= 0 && clive[t] ? 'P' : v < 4096 ? 'i' : '?';
                if (t >= 0 && clive[t]) {
                    uint64_t len; memcpy(&len, heap + coff[t] + 16, 8);
                    if (len >= 3 && len <= 32 && csize[t] == ((24 + len + 7) & ~7ULL)) {
                        int ok = 1; for (uint64_t j = 0; j < len; j++) { int ch = heap[coff[t] + 24 + j]; if (ch < 32 || ch > 126) { ok = 0; break; } }
                        if (ok) { c = 'S'; if (!first[0]) snprintf(first, sizeof first, "%.*s", (int)len, heap + coff[t] + 24); }
                    }
                }
                key[kn++] = c;
            }
            key[kn] = 0;
            if (first[0]) { size_t at = strlen(key); snprintf(key + at, sizeof key - at, " \"%s\"", first); }
            size_t j; for (j = 0; j < nk; j++) if (!strcmp(keys[j].key, key)) break;
            if (j == nk && nk < 4096) { snprintf(keys[nk].key, sizeof keys[nk].key, "%s", key); nk++; }
            if (j < nk) { if (keys[j].n < 3) keys[j].sample[keys[j].n] = BASE + coff[i]; keys[j].n++; }
        }
        for (int n = 0; n < 30; n++) { size_t best = 0; for (size_t j = 1; j < nk; j++) if (keys[j].n > keys[best].n) best = j; if (!keys[best].n) break; printf("%9llu roots  %-28s e.g. %llx %llx %llx\n", (unsigned long long)keys[best].n, keys[best].key, (unsigned long long)keys[best].sample[0], (unsigned long long)keys[best].sample[1], (unsigned long long)keys[best].sample[2]); keys[best].n = 0; }
        return 0;
    }
    if (argc > 2) {
        uint64_t root = strtoull(argv[2], 0, 16);
        long r = findcell(root);
        if (r < 0) { fprintf(stderr, "no cell at %s\n", argv[2]); return 1; }
        uint8_t *seen = calloc(ncells, 1); uint32_t *stack = malloc(ncells * 4); seen[r] = 1;
        printf("cell %llx size %u count %llu\n", (unsigned long long)(BASE + coff[r]), csize[r], (unsigned long long)ccount[r]);
        for (uint64_t w = 16; w + 8 <= csize[r]; w += 8) {
            uint64_t v; memcpy(&v, heap + coff[r] + w, 8);
            long t = findcell(v); uint64_t bytes = 0, cells = 0;
            if (t >= 0 && clive[t] && !seen[t]) {
                size_t sp = 0; stack[sp++] = (uint32_t)t; seen[t] = 1;
                while (sp) { uint32_t c = stack[--sp]; bytes += csize[c]; cells++;
                    for (uint32_t e = estart[c]; e < estart[c + 1]; e++) if (!seen[edges[e]]) { seen[edges[e]] = 1; stack[sp++] = edges[e]; } }
            }
            printf("  field+%-4llu %18llx  %10.1f MB  %10llu cells\n", (unsigned long long)(w - 16), (unsigned long long)v, bytes / 1048576.0, (unsigned long long)cells);
            if (argc > 3 && t >= 0 && clive[t]) {
                // re-walk this field breadth-first and print the first string-looking payloads found,
                // which name the record the root belongs to
                uint8_t *s2 = calloc(ncells, 1); uint32_t *q = malloc(ncells * 4); size_t qh = 0, qt = 0;
                q[qt++] = (uint32_t)t; s2[t] = 1; int shown = 0;
                while (qh < qt && shown < 10) {
                    uint32_t c = q[qh++];
                    uint64_t len; memcpy(&len, heap + coff[c] + 16, 8);
                    if (len >= 3 && len <= 48 && csize[c] == ((24 + len + 7) & ~7ULL)) {
                        int ok = 1; for (uint64_t j = 0; j < len; j++) { int ch = heap[coff[c] + 24 + j]; if (ch < 32 || ch > 126) { ok = 0; break; } }
                        if (ok) { printf("      \"%.*s\"\n", (int)len, heap + coff[c] + 24); shown++; }
                    }
                    for (uint32_t e = estart[c]; e < estart[c + 1]; e++) if (!s2[edges[e]]) { s2[edges[e]] = 1; q[qt++] = edges[e]; }
                }
                free(s2); free(q);
            }
        }
        return 0;
    }
    uint32_t *indeg = calloc(ncells, 4);
    for (uint64_t e = 0; e < nedges; e++) indeg[edges[e]]++;
    uint32_t *owner = malloc(ncells * 4); memset(owner, 0xff, ncells * 4);
    uint64_t *rootbytes = calloc(ncells, 8); uint32_t *stack = malloc(ncells * 4);
    uint64_t live = 0, reached = 0, nroots = 0;
    for (size_t i = 0; i < ncells; i++) if (clive[i]) live += csize[i];
    for (size_t i = 0; i < ncells; i++) {
        if (!clive[i] || indeg[i] || owner[i] != 0xffffffffu) continue;
        nroots++;
        size_t sp = 0; stack[sp++] = (uint32_t)i; owner[i] = (uint32_t)i;
        while (sp) { uint32_t c = stack[--sp]; rootbytes[i] += csize[c];
            for (uint32_t e = estart[c]; e < estart[c + 1]; e++) if (owner[edges[e]] == 0xffffffffu) { owner[edges[e]] = (uint32_t)i; stack[sp++] = edges[e]; } }
        reached += rootbytes[i];
    }
    printf("cells %zu edges %llu live %.1f MB roots %llu reached-from-roots %.1f MB (rest only in cycles)\n",
        ncells, (unsigned long long)nedges, live / 1048576.0, (unsigned long long)nroots, reached / 1048576.0);
    // exclusive attribution: a cell belongs to a root only when every path into it comes from that
    // root, computed by propagating ownership along a topological order (the RC graph is acyclic).
    {
        uint32_t *left = malloc(ncells * 4); memcpy(left, indeg, ncells * 4);
        uint32_t *own = malloc(ncells * 4); memset(own, 0xff, ncells * 4);
        uint32_t *queue = malloc(ncells * 4); size_t qh = 0, qt = 0;
        const uint32_t MULTI = 0xfffffffeu, UNSET = 0xffffffffu;
        for (size_t i = 0; i < ncells; i++) if (clive[i] && !indeg[i]) { own[i] = (uint32_t)i; queue[qt++] = (uint32_t)i; }
        uint64_t ordered = 0;
        while (qh < qt) {
            uint32_t c = queue[qh++]; ordered++;
            for (uint32_t e = estart[c]; e < estart[c + 1]; e++) {
                uint32_t s = edges[e];
                if (own[s] == UNSET) own[s] = own[c]; else if (own[s] != own[c]) own[s] = MULTI;
                if (--left[s] == 0) queue[qt++] = s;
            }
        }
        uint64_t exclusive = 0, shared = 0, cyclic = 0;
        uint64_t *excbytes = calloc(ncells, 8);
        for (size_t i = 0; i < ncells; i++) {
            if (!clive[i]) continue;
            if (own[i] == UNSET) { cyclic += csize[i]; continue; }
            if (own[i] == MULTI) { shared += csize[i]; continue; }
            excbytes[own[i]] += csize[i]; exclusive += csize[i];
        }
        printf("exclusive %.1f MB  shared-by-several-roots %.1f MB  unordered/cyclic %.1f MB  (topo-ordered %llu of %zu cells)\n",
            exclusive / 1048576.0, shared / 1048576.0, cyclic / 1048576.0, (unsigned long long)ordered, ncells);
        typedef struct { uint32_t size; uint64_t word; uint64_t n, bytes, sample[3]; } B; static B b[65536]; size_t nb = 0;
        for (size_t i = 0; i < ncells; i++) { if (!excbytes[i]) continue; uint64_t w; memcpy(&w, heap + coff[i] + 16, 8); if (w >= BASE && w < BASE + (4ULL << 40)) w = 0xffffffffffffffffULL; size_t j; for (j = 0; j < nb; j++) if (b[j].size == csize[i] && b[j].word == w) break; if (j == nb && nb < 65536) { b[nb].size = csize[i]; b[nb].word = w; nb++; } if (j < nb) { if (b[j].n < 3) b[j].sample[b[j].n] = BASE + coff[i]; b[j].n++; b[j].bytes += excbytes[i]; } }
        for (int n = 0; n < 25; n++) { size_t best = 0; for (size_t j = 1; j < nb; j++) if (b[j].bytes > b[best].bytes) best = j; if (!b[best].bytes) break; if (b[best].word == 0xffffffffffffffffULL) printf("exclusive size %4u word ptr    %9llu roots keep %8.1f MB  e.g. %llx %llx\n", b[best].size, (unsigned long long)b[best].n, b[best].bytes / 1048576.0, (unsigned long long)b[best].sample[0], (unsigned long long)b[best].sample[1]); else printf("exclusive size %4u word %-6llu %9llu roots keep %8.1f MB  e.g. %llx\n", b[best].size, (unsigned long long)b[best].word, (unsigned long long)b[best].n, b[best].bytes / 1048576.0, (unsigned long long)b[best].sample[0]); b[best].bytes = 0; }
    }
    { typedef struct { uint32_t size; uint64_t word; uint64_t n, bytes, sample[3]; } B; static B b[65536]; size_t nb = 0;
      for (size_t i = 0; i < ncells; i++) { if (!rootbytes[i]) continue; uint64_t w; memcpy(&w, heap + coff[i] + 16, 8); if (w >= BASE && w < BASE + (4ULL << 40)) w = 0xffffffffffffffffULL; size_t j; for (j = 0; j < nb; j++) if (b[j].size == csize[i] && b[j].word == w) break; if (j == nb && nb < 65536) { b[nb].size = csize[i]; b[nb].word = w; nb++; } if (j < nb) { if (b[j].n < 3) b[j].sample[b[j].n] = BASE + coff[i]; b[j].n++; b[j].bytes += rootbytes[i]; } }
      for (int n = 0; n < 25; n++) { size_t best = 0; for (size_t j = 1; j < nb; j++) if (b[j].bytes > b[best].bytes) best = j; if (!b[best].bytes) break; if (b[best].word == 0xffffffffffffffffULL) printf("roots size %4u word ptr    %9llu roots hold %8.1f MB  e.g. %llx %llx\n", b[best].size, (unsigned long long)b[best].n, b[best].bytes / 1048576.0, (unsigned long long)b[best].sample[0], (unsigned long long)b[best].sample[1]); else printf("roots size %4u word %-6llu %9llu roots hold %8.1f MB\n", b[best].size, (unsigned long long)b[best].word, (unsigned long long)b[best].n, b[best].bytes / 1048576.0); b[best].bytes = 0; } }
    for (int n = 0; n < 0; n++) {
        size_t best = (size_t)-1; uint64_t bestb = 0;
        for (size_t i = 0; i < ncells; i++) if (rootbytes[i] > bestb) { bestb = rootbytes[i]; best = i; }
        if (best == (size_t)-1) break;
        uint64_t w; memcpy(&w, heap + coff[best] + 16, 8);
        printf("root %llx size %4u count %llu word %llx  holds %.1f MB\n", (unsigned long long)(BASE + coff[best]), csize[best],
            (unsigned long long)ccount[best], (unsigned long long)w, bestb / 1048576.0);
        rootbytes[best] = 0;
    }
    return 0;
}
