// Derive the self-hosted lexer's fixed Unicode predicates from the bundled PCRE2 bitcode.
// Uses the same PCRE2_UTF | PCRE2_UCP flags and patterns as the existing lexer.
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// The narrow public PCRE2-8 ABI already used by the compiler; no system PCRE2 headers or library.
typedef struct pcre2_real_code_8 PcreCode;
typedef struct pcre2_real_match_data_8 PcreMatchData;
extern PcreCode *pcre2_compile_8(const unsigned char *, size_t, uint32_t, int *, size_t *, void *);
extern PcreMatchData *pcre2_match_data_create_from_pattern_8(const PcreCode *, void *);
extern int pcre2_match_8(const PcreCode *, const unsigned char *, size_t, size_t, uint32_t, PcreMatchData *, void *);

typedef struct { uint32_t first, last; } Range;
typedef struct { Range *items; size_t count, capacity; } Ranges;

static void add_range(Ranges *ranges, uint32_t first, uint32_t last) {
    if (ranges->count == ranges->capacity) {
        size_t capacity = ranges->capacity ? ranges->capacity * 2 : 64;
        Range *items = realloc(ranges->items, capacity * sizeof(*items));
        if (!items) { fputs("Out of memory collecting Unicode ranges.\n", stderr); exit(1); }
        ranges->items = items;
        ranges->capacity = capacity;
    }
    ranges->items[ranges->count++] = (Range){first, last};
}

static size_t encode(uint32_t n, unsigned char *s) {
    if (n < 0x80) { s[0] = n; return 1; }
    if (n < 0x800) { s[0] = 0xc0 | (n >> 6); s[1] = 0x80 | (n & 63); return 2; }
    if (n < 0x10000) { s[0] = 0xe0 | (n >> 12); s[1] = 0x80 | ((n >> 6) & 63); s[2] = 0x80 | (n & 63); return 3; }
    s[0] = 0xf0 | (n >> 18); s[1] = 0x80 | ((n >> 12) & 63); s[2] = 0x80 | ((n >> 6) & 63); s[3] = 0x80 | (n & 63); return 4;
}

static void indent(unsigned level) {
    for (unsigned i = 0; i < level; ++i) fputs("    ", stdout);
}

// Balanced decisions avoid allocating classification tables in the compiler process.
static void emit_tree(const Ranges *ranges, size_t first, size_t end, unsigned level) {
    if (first == end) { indent(level); puts("false"); return; }
    size_t middle = first + (end - first - 1) / 2;
    Range range = ranges->items[middle];
    indent(level); printf("if code < %u\n", range.first);
    indent(level); puts(first == middle ? "then false" : "then");
    if (first != middle) emit_tree(ranges, first, middle, level + 1);
    indent(level); puts("else");
    indent(level + 1); printf("if code <= %u\n", range.last);
    indent(level + 1); puts("then true");
    indent(level + 1); puts(middle + 1 == end ? "else false" : "else");
    if (middle + 1 != end) emit_tree(ranges, middle + 1, end, level + 2);
}

int main(int argc, char **argv) {
    if (argc != 3) { fputs("usage: generate-lexer-unicode <reference-bytes> <pcre2-version>\n", stderr); return 2; }
    const char *patterns[] = {"^\\p{L}$", "^[\\p{L}\\p{Nd}]$", "^\\s$"};
    PcreCode *code[3];
    PcreMatchData *data[3];
    Ranges ranges[3] = {{0}};
    uint32_t starts[3] = {0};
    int previous[3] = {0};
    FILE *reference = fopen(argv[1], "wb");
    if (!reference) { perror(argv[1]); return 2; }
    for (int i = 0; i < 3; ++i) {
        int error;
        size_t offset;
        code[i] = pcre2_compile_8((const unsigned char *)patterns[i], strlen(patterns[i]), 0x00080000u | 0x00020000u, &error, &offset, NULL);
        if (!code[i]) { fprintf(stderr, "PCRE2 compile error %d at %zu\n", error, offset); return 1; }
        data[i] = pcre2_match_data_create_from_pattern_8(code[i], NULL);
        if (!data[i]) { fputs("Could not allocate PCRE2 match data.\n", stderr); return 1; }
    }
    for (uint32_t n = 0; n <= 0x110000; ++n) {
        unsigned char text[4];
        unsigned char flags = 0;
        int matched[3] = {0};
        int valid = n < 0x110000 && !(n >= 0xd800 && n <= 0xdfff);
        size_t length = valid ? encode(n, text) : 0;
        for (int i = 0; i < 3; ++i) {
            if (valid) {
                int result = pcre2_match_8(code[i], text, length, 0, 0, data[i], NULL);
                if (result < 0 && result != -1) { fprintf(stderr, "PCRE2 match error %d at %u\n", result, n); return 1; }
                matched[i] = result >= 0;
            }
            if (matched[i]) flags |= 1 << i;
        }
        // The reference retains the exact original predicates; source shares letters with identifiers.
        matched[1] = matched[1] && !matched[0];
        for (int i = 0; i < 3; ++i) {
            if (matched[i] && !previous[i]) starts[i] = n;
            if (!matched[i] && previous[i]) add_range(&ranges[i], starts[i], n - 1);
            previous[i] = matched[i];
        }
        if (n < 0x110000 && fwrite(&flags, 1, 1, reference) != 1) { perror("reference write"); return 1; }
    }
    if (fclose(reference) != 0) { perror("reference close"); return 1; }
    printf("// Generated from bundled PCRE2 %s by scripts/selfhost-dev/generate-lexer-unicode.sh.\n", argv[2]);
    puts("// Preserves the lexer's fixed Unicode letter, letter-or-decimal-digit, and whitespace checks.");
    puts("// Balanced integer decisions allocate no runtime tables. Regenerate when Pcre2Version changes.");
    puts("export (\n    value isUnicodeLetter,\n    value isUnicodeIdentifierCharacter,\n    value isUnicodeWhiteSpace,\n)");
    const char *names[] = {"isUnicodeLetter", "isUnicodeDecimalDigit", "isUnicodeWhiteSpace"};
    for (int i = 0; i < 3; ++i) {
        printf("\nlet %s (code: Int) =\n", names[i]);
        emit_tree(&ranges[i], 0, ranges[i].count, 1);
        fprintf(stderr, "%s: %zu ranges\n", names[i], ranges[i].count);
        free(ranges[i].items);
    }
    puts("\nlet isUnicodeIdentifierCharacter (code: Int) = isUnicodeLetter(code) || isUnicodeDecimalDigit(code)");
    return ferror(stdout) ? 1 : 0;
}
