#include <stdint.h>

typedef struct ashes_ffi_handle {
    int64_t value;
} ashes_ffi_handle;

static ashes_ffi_handle handles[64];
static uint64_t next_handle = 0;

ashes_ffi_handle *ashes_ffi_buffer_make(int64_t value) {
    ashes_ffi_handle *handle = &handles[next_handle % 64];
    next_handle += 1;
    handle->value = value;
    return handle;
}

int64_t ashes_ffi_buffer_read(ashes_ffi_handle *handle) {
    return handle->value;
}

static ashes_ffi_handle *buffer_result(
    int64_t marker,
    ashes_ffi_handle *const *items,
    uint64_t count) {
    if ((count == 0 && items != 0) || (count != 0 && items == 0)) {
        return ashes_ffi_buffer_make(-1);
    }

    int64_t sum = 0;
    for (uint64_t i = 0; i < count; i += 1) {
        if (items[i] == 0) {
            return ashes_ffi_buffer_make(-2);
        }
        sum += items[i]->value;
    }
    return ashes_ffi_buffer_make(marker + (int64_t)(count * 100) + sum);
}

ashes_ffi_handle *LLVMFunctionType(
    ashes_ffi_handle *return_type,
    ashes_ffi_handle *const *parameter_types,
    uint32_t parameter_count,
    uint8_t is_var_arg) {
    (void)return_type;
    (void)is_var_arg;
    return buffer_result(1000, parameter_types, parameter_count);
}

ashes_ffi_handle *LLVMBuildCall2(
    ashes_ffi_handle *builder,
    ashes_ffi_handle *function_type,
    ashes_ffi_handle *function,
    ashes_ffi_handle *const *arguments,
    uint32_t argument_count,
    const char *name) {
    (void)builder;
    (void)function_type;
    (void)function;
    (void)name;
    return buffer_result(2000, arguments, argument_count);
}

ashes_ffi_handle *LLVMBuildGEP2(
    ashes_ffi_handle *builder,
    ashes_ffi_handle *source_type,
    ashes_ffi_handle *pointer,
    ashes_ffi_handle *const *indices,
    uint32_t index_count,
    const char *name) {
    (void)builder;
    (void)source_type;
    (void)pointer;
    (void)name;
    return buffer_result(3000, indices, index_count);
}

ashes_ffi_handle *LLVMConstArray2(
    ashes_ffi_handle *element_type,
    ashes_ffi_handle *const *values,
    uint64_t value_count) {
    (void)element_type;
    return buffer_result(4000, values, value_count);
}

ashes_ffi_handle *LLVMConstStructInContext(
    ashes_ffi_handle *context,
    ashes_ffi_handle *const *values,
    uint32_t value_count,
    uint8_t packed) {
    (void)context;
    (void)packed;
    return buffer_result(5000, values, value_count);
}

ashes_ffi_handle *LLVMStructTypeInContext(
    ashes_ffi_handle *context,
    ashes_ffi_handle *const *element_types,
    uint32_t element_count,
    uint8_t packed) {
    (void)context;
    (void)packed;
    return buffer_result(6000, element_types, element_count);
}

int64_t ashes_ffi_buffer_sum(ashes_ffi_handle *const *items, uint64_t count) {
    if (count == 0) {
        return items == 0 ? 1000 : -1000;
    }
    if (items == 0) {
        return -2000;
    }

    int64_t sum = 0;
    for (uint64_t i = 0; i < count; i += 1) {
        if (items[i] == 0) {
            return -3000;
        }
        sum += items[i]->value;
    }
    return sum;
}
