// usage: rcfill <dump>... ; for each 16 MB sample prints how many 4 MB quarters hold any nonzero
// word, the offset of the last nonzero word, and the first four words (to see the chunk layout).
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char **argv) {
    for (int a = 1; a < argc; a++) {
        FILE *in = fopen(argv[a], "rb"); if (!in) continue;
        fseek(in, 0, SEEK_END); long len = ftell(in); fseek(in, 0, SEEK_SET);
        uint64_t *data = malloc(len); if (fread(data, 1, len, in) != (size_t)len) return 1; fclose(in);
        long words = len / 8, last = -1, nonzero = 0;
        int quarters[4] = { 0 };
        for (long i = 0; i < words; i++) if (data[i]) { last = i; nonzero++; quarters[(i * 8) / (4 << 20)] = 1; }
        printf("%s nonzero=%ld words, quarters=%d%d%d%d last=%ld  head=%llx %llx %llx %llx\n",
            strrchr(argv[a], '/') + 1, nonzero, quarters[0], quarters[1], quarters[2], quarters[3], last * 8,
            (unsigned long long)data[0], (unsigned long long)data[1], (unsigned long long)data[2], (unsigned long long)data[3]);
        free(data);
    }
    return 0;
}
