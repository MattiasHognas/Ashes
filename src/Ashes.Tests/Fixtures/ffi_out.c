#include <stdint.h>
#include <string.h>

typedef struct ashes_ffi_out_handle {
    int64_t value;
} ashes_ffi_out_handle;

static ashes_ffi_out_handle handles[] = {{101}, {202}, {303}, {404}};
static char target_message[] = "target message";
static char verify_message[] = "verify message";
static char emit_message[] = "emit message";
static char parse_message[] = "parse message";

ashes_ffi_out_handle *ashes_ffi_out_handle_at(int64_t index) {
    return &handles[index];
}

int64_t ashes_ffi_out_read(ashes_ffi_out_handle *handle) {
    return handle->value;
}

uint32_t LLVMGetTargetFromTriple(
    const char *triple,
    ashes_ffi_out_handle **target,
    char **error_message) {
    if (strcmp(triple, "success") == 0) {
        *target = &handles[0];
        *error_message = 0;
        return 7;
    }

    /* Deliberately leave target untouched to verify compiler-side zero initialization. */
    *error_message = target_message;
    return 8;
}

uint32_t LLVMVerifyModule(
    ashes_ffi_out_handle *module,
    uint32_t action,
    char **error_message) {
    (void)module;
    if (action == 0) {
        /* Deliberately leave error_message untouched. */
        return 0;
    }
    *error_message = verify_message;
    return 1;
}

uint32_t LLVMTargetMachineEmitToMemoryBuffer(
    ashes_ffi_out_handle *machine,
    ashes_ffi_out_handle *module,
    uint32_t codegen,
    const char *path,
    char **error_message,
    ashes_ffi_out_handle **buffer) {
    (void)machine;
    (void)module;
    (void)codegen;
    (void)path;
    *error_message = emit_message;
    *buffer = &handles[2];
    return 9;
}

uint32_t LLVMParseIRInContext(
    ashes_ffi_out_handle *context,
    ashes_ffi_out_handle *memory_buffer,
    ashes_ffi_out_handle **module,
    char **error_message) {
    (void)context;
    (void)memory_buffer;
    *module = &handles[3];
    *error_message = parse_message;
    return 10;
}
