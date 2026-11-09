// findptr <dump.bin> <base-hex> <value-hex> : file offsets (as addresses) of every aligned word equal to value
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
int main(int c, char **v) { FILE *f = fopen(v[1], "rb"); uint64_t base = strtoull(v[2], 0, 16), val = strtoull(v[3], 0, 16), w; long i = 0;
  while (fread(&w, 8, 1, f) == 1) { if (w == val) printf("%llx\n", (unsigned long long)(base + i * 8)); i++; } return 0; }
