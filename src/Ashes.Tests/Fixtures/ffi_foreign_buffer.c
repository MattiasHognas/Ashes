#include <stdint.h>
#include <stdlib.h>

typedef struct {
    uint8_t *data;
    uint64_t length;
} AshesMemoryBuffer;

static int64_t dispose_count;

AshesMemoryBuffer *ashes_ffi_memory_buffer_create(int64_t kind) {
    AshesMemoryBuffer *buffer = (AshesMemoryBuffer *)calloc(1, sizeof(AshesMemoryBuffer));
    if (kind == 0) {
        return buffer;
    }

    buffer->length = kind == 2 ? 1024u * 1024u : 5u;
    buffer->data = (uint8_t *)malloc((size_t)buffer->length);
    for (uint64_t index = 0; index < buffer->length; index++) {
        buffer->data[index] = (uint8_t)(index & 0xffu);
    }
    return buffer;
}

uint32_t ashes_ffi_memory_buffer_create_out(int64_t kind, AshesMemoryBuffer **output) {
    *output = ashes_ffi_memory_buffer_create(kind);
    return 0;
}

uint8_t *LLVMGetBufferStart(AshesMemoryBuffer *buffer) {
    return buffer->data;
}

uint64_t LLVMGetBufferSize(AshesMemoryBuffer *buffer) {
    return buffer->length;
}

void LLVMDisposeMemoryBuffer(AshesMemoryBuffer *buffer) {
    dispose_count++;
    free(buffer->data);
    free(buffer);
}

int64_t ashes_ffi_memory_buffer_dispose_count(void) {
    return dispose_count;
}
