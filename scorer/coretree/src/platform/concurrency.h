#pragma once
// SHIM: single-threaded no-op locking. Replaces Core's concurrency.h whose
// static_assert(sizeof(long)==4) and MSVC interlocked intrinsics don't hold on
// Linux LP64. Scoring runs single-threaded, so locks are unnecessary.

#define ACQUIRE(lock) ((void)0)
#define RELEASE(lock) ((void)0)
#define ACQUIRE_WITHOUT_DEBUG_LOGGING(lock) ((void)0)
#define RELEASE_WITHOUT_DEBUG_LOGGING(lock) ((void)0)

struct LockGuard
{
    LockGuard(volatile char&) {}
    LockGuard() {}
    ~LockGuard() {}
};
