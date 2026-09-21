// usage: rclist <text> <dump>... ; dumps are rcdump-<k>.bin at 0x100000000000 + k GB. For every 32-byte
// cell (a cons: [head][tail]) whose head is a string equal to <text>, renders the list from that
// cell (strings as "..", zero-payload cells as <0>, anything else as <sizeN wordW>, end as []),
// and tallies the renderings. A pointer outside the samples prints as ?.
#include <ctype.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint8_t *datas[64]; static uint64_t bases[64]; static long lens[64]; static int ndumps;

static const uint8_t *at(uint64_t addr, long need) {
    for (int i = 0; i < ndumps; i++) {
        if (addr >= bases[i] && addr + need <= bases[i] + (uint64_t)lens[i]) return datas[i] + (addr - bases[i]);
    }
    return 0;
}

static void render(uint64_t payload, char *out, size_t cap) {
    const uint8_t *h = at(payload - 16, 32);
    if (!h) { snprintf(out, cap, "?"); return; }
    uint64_t size, word; memcpy(&size, h + 8, 8); memcpy(&word, h + 16, 8);
    if (size == 24 && word == 0) { snprintf(out, cap, "<0>"); return; }
    if (word >= 1 && word <= 40 && size >= 24 + word && at(payload, 8 + word)) {
        const uint8_t *s = at(payload + 8, word);
        int printable = 1;
        for (uint64_t b = 0; b < word; b++) if (!isprint(s[b])) printable = 0;
        if (printable) { snprintf(out, cap, "\"%.*s\"", (int)word, (const char *)s); return; }
    }
    snprintf(out, cap, "<s%llu w%llu>", (unsigned long long)size, (unsigned long long)(word > 0xffff ? 0xffff : word));
}

typedef struct { char text[256]; uint64_t count; } Entry;
static Entry entries[4096]; static size_t used;

static int cmp(const void *a, const void *b) {
    const Entry *x = a, *y = b;
    return x->count < y->count ? 1 : x->count > y->count ? -1 : 0;
}

int main(int argc, char **argv) {
    const char *want = argv[1];
    size_t wantLen = strlen(want);
    for (int a = 2; a < argc && ndumps < 64; a++) {
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); lens[ndumps] = ftell(in); fseek(in, 0, SEEK_SET);
        datas[ndumps] = malloc(lens[ndumps]);
        if (fread(datas[ndumps], 1, lens[ndumps], in) != (size_t)lens[ndumps]) return 1;
        fclose(in);
        bases[ndumps] = 0x100000000000ULL + (uint64_t)atoi(strrchr(argv[a], '-') + 1) * 1073741824ULL;
        ndumps++;
    }
    for (int d = 0; d < ndumps; d++) {
        for (long off = 0; off + 32 <= lens[d]; off += 8) {
            uint64_t count, size, head;
            memcpy(&count, datas[d] + off, 8); memcpy(&size, datas[d] + off + 8, 8); memcpy(&head, datas[d] + off + 16, 8);
            if (count == 0 || count > 1000 || size != 32 || head < 0x100000000000ULL || head >= 0x140000000000ULL) continue;
            const uint8_t *s = at(head - 16, 24 + wantLen);
            if (!s) continue;
            uint64_t slen; memcpy(&slen, s + 16, 8);
            if (slen != wantLen || memcmp(s + 24, want, wantLen) != 0) continue;
            char line[256] = "["; size_t pos = 1;
            uint64_t cell = bases[d] + off + 16;
            for (int k = 0; k < 6; k++) {
                const uint8_t *c = at(cell - 16, 32);
                if (!c) { pos += snprintf(line + pos, sizeof line - pos, " ?"); break; }
                uint64_t h, t; memcpy(&h, c + 16, 8); memcpy(&t, c + 24, 8);
                char item[64]; render(h, item, sizeof item);
                pos += snprintf(line + pos, sizeof line - pos, "%s%s", k ? ", " : "", item);
                if (t == 0) { pos += snprintf(line + pos, sizeof line - pos, "]"); break; }
                cell = t;
                if (k == 5) pos += snprintf(line + pos, sizeof line - pos, ", ...");
                if (pos > 200) break;
            }
            size_t i = 0;
            for (; i < used; i++) if (strcmp(entries[i].text, line) == 0) { entries[i].count++; break; }
            if (i == used && used < 4096) { strcpy(entries[used].text, line); entries[used++].count = 1; }
        }
    }
    qsort(entries, used, sizeof(Entry), cmp);
    for (size_t i = 0; i < used && i < 20; i++) printf("%8llu %s\n", (unsigned long long)entries[i].count, entries[i].text);
    return 0;
}
