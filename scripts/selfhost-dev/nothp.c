#include <stdio.h>
#include <sys/prctl.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: nothp <program> [args...]\n");
        return 2;
    }
    prctl(PR_SET_THP_DISABLE, 1, 0, 0, 0);
    execv(argv[1], argv + 1);
    perror("execv");
    return 127;
}
