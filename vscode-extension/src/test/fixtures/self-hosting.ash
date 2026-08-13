external type Database resource destructor databaseClose
external databaseClose(consume Database) -> void = "db_close@libdb"
external databaseVersion(borrow Database) -> Int = "db_version@libdb"
external LLVMFunctionType(FfiBuffer(LLVMTypeRef), u32) -> LLVMTypeRef
external LLVMGetTargetFromTriple(Str, out LLVMTargetRef, out *u8) -> Bool
external LLVMGetTargetName(LLVMTargetRef) -> FfiStr(nullable borrowed)
external LLVMGetHostCPUName() -> FfiStr(owned LLVMDisposeMessage)
