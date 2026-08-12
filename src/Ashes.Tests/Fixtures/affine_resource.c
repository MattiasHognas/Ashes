#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct ashes_counted_resource {
    int64_t value;
} ashes_counted_resource;

static int64_t live_resources = 0;

ashes_counted_resource *ashes_resource_open(int64_t value) {
    ashes_counted_resource *resource = malloc(sizeof(ashes_counted_resource));
    if (resource != NULL) {
        resource->value = value;
        live_resources += 1;
    }
    return resource;
}

int64_t ashes_resource_read(const ashes_counted_resource *resource) {
    return resource == NULL ? -1 : resource->value;
}

void ashes_resource_close(ashes_counted_resource *resource) {
    if (resource != NULL) {
        if (resource->value >= 0) {
            printf("closed %lld\n", (long long)resource->value);
            fflush(stdout);
        }
        live_resources -= 1;
        free(resource);
    }
}

int64_t ashes_resource_live(void) {
    return live_resources;
}
