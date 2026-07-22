#pragma once
// SHIM: profiling disabled. Keeps memory_util (copyMem/setMem/allocPool) but drops
// time_stamp_counter.h / file_io.h (MSVC intrinsics + host I/O not needed for scoring).
#include "memory_util.h"

#define PROFILE_SCOPE()
#define PROFILE_NAMED_SCOPE(name)
#define PROFILE_SCOPE_BEGIN() {
#define PROFILE_NAMED_SCOPE_BEGIN(name) {
#define PROFILE_SCOPE_END() }
