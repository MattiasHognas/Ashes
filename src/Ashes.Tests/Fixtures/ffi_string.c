#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static int64_t dispose_count = 0;
static char borrowed_text[] = "borrowed text";
static char embedded_text[] = {'a', 0, 'b', 0};

static char *copy_bytes(const unsigned char *bytes, size_t length) {
    char *result = (char *)malloc(length + 1);
    memcpy(result, bytes, length);
    result[length] = 0;
    return result;
}

void LLVMDisposeMessage(char *message) {
    if (message != 0) {
        dispose_count += 1;
        free(message);
    }
}

int64_t ashes_ffi_string_dispose_count(void) {
    return dispose_count;
}

void *ashes_ffi_string_handle(int64_t value) {
    return (void *)(uintptr_t)value;
}

char *LLVMGetHostCPUName(void) {
    static const unsigned char value[] = "owned \xE2\x9C\x93";
    return copy_bytes(value, sizeof(value) - 1);
}

char *LLVMGetHostCPUFeatures(void) {
    return copy_bytes((const unsigned char *)"", 0);
}

char *LLVMCopyStringRepOfTargetData(void *data) {
    (void)data;
    return copy_bytes((const unsigned char *)embedded_text, sizeof(embedded_text) - 1);
}

char *LLVMPrintModuleToString(void *module) {
    static const unsigned char invalid[] = {0xF0, 0x28, 0x8C, 0x28};
    (void)module;
    return copy_bytes(invalid, sizeof(invalid));
}

const char *LLVMGetTargetName(void *target) {
    return target == 0 ? 0 : borrowed_text;
}

uint8_t LLVMVerifyModule(void *module, uint32_t action, char **message) {
    (void)module;
    if (action == 0) {
        return 0;
    }
    *message = copy_bytes((const unsigned char *)"verify failed", 13);
    return 1;
}
