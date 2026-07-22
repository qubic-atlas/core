// Historical Qubic Atlas verifier: reproduces the SCORE for a given Core release's ANN
// scorer, built from that release's own source (score_addition.h / params differ per era).
// Score-only (+ pass/threshold) — the server applies the exact per-epoch threshold and
// computes the genome id. Params come from the tag's own score_params::ConfigProfile.
#define NO_UEFI
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <cwchar>
#include <tuple>
#include <cstddef>

#include "public_settings.h"
#include "platform/m256.h"
#include "mining/score_engine.h"
#include "test/score_params.h"   // ConfigProfile — the tag's own HI/ADD parameter wiring

void setMem(void* b, unsigned long long s, unsigned char v) { memset(b, (int)v, (size_t)s); }
void copyMem(void* d, const void* s, unsigned long long n) { memcpy(d, s, (size_t)n); }

using namespace score_engine;
using HI  = score_params::ConfigProfile::HyperIdentity;
using ADD = score_params::ConfigProfile::Addition;

static int hb(const char* h){ auto v=[&](char c)->int{ if(c>='0'&&c<='9')return c-'0'; if(c>='a'&&c<='f')return c-'a'+10; if(c>='A'&&c<='F')return c-'A'+10; return 0;}; return (v(h[0])<<4)|v(h[1]); }
static void hx(const char* hex, unsigned char* out, int n){ for(int i=0;i<n;i++) out[i]=(unsigned char)hb(hex+2*i); }

// Emit the score-only JSON for ONE proof, scoring against an already-built seed pool.
static void runOne(const unsigned char* publicKey, const unsigned char* nonce, long expected, const unsigned char* pool) {
    int isHI = (nonce[0] & 1) == 0;
    static ScoreEngine<HI, ADD> engine;
    setMem(&engine, sizeof(engine), 0);
    engine.initMemory();
    unsigned int score = engine.computeScore(publicKey, nonce, pool);
    unsigned int threshold = isHI ? HI::solutionThreshold : ADD::solutionThreshold;

    printf("{\n");
    printf("  \"reconstructorVersion\": \"qubic-atlas-hist-1\",\n");
    printf("  \"algorithm\": \"%s\",\n", isHI ? "HyperIdentity" : "Addition");
    printf("  \"score\": %u,\n", score);
    printf("  \"threshold\": %u,\n", threshold);
    printf("  \"passesThreshold\": %s,\n", score >= threshold ? "true" : "false");
    printf("  \"rawScoreValid\": true,\n");
    if (expected >= 0) printf("  \"expectedScore\": %ld,\n  \"scoreMatches\": %s,\n", expected, ((long)score==expected)?"true":"false");
    printf("  \"historical\": true\n}\n");
}

// ~512MB seed pool cached across calls: regenerated ONLY when the miningSeed changes (see verifier.cpp).
static unsigned char* g_pool = nullptr;
static unsigned char g_lastSeed[32];
static bool g_haveSeed = false;
static bool ensurePool(const unsigned char* miningSeed) {
    if (!g_pool) {
        g_pool = (unsigned char*)malloc(POOL_VEC_PADDING_SIZE);
        if (!g_pool) { fprintf(stderr, "pool alloc failed\n"); return false; }
    }
    if (!g_haveSeed || memcmp(miningSeed, g_lastSeed, 32) != 0) {
        unsigned char state[STATE_SIZE];
        generateRandom2Pool(miningSeed, state, g_pool);
        memcpy(g_lastSeed, miningSeed, 32);
        g_haveSeed = true;
    }
    return true;
}

int main(int argc, char** argv) {
    // Daemon mode — same protocol as verifier.cpp: one proof per stdin line, "__ATLAS_EOF__" delimiter.
    if (argc >= 2 && strcmp(argv[1], "--serve") == 0) {
        fprintf(stderr, "[verifier_hist] serve mode ready (pool cached per miningSeed)\n");
        char seedHex[80], pkHex[80], nonceHex[80], line[512]; long expected;
        while (fgets(line, sizeof(line), stdin)) {
            int n = sscanf(line, "%79s %79s %79s %ld", seedHex, pkHex, nonceHex, &expected);
            if (n < 3 || strlen(seedHex) < 64 || strlen(pkHex) < 64 || strlen(nonceHex) < 64) {
                printf("{\"error\":\"bad_request\"}\n__ATLAS_EOF__\n"); fflush(stdout); continue;
            }
            if (n < 4) expected = -1;
            unsigned char miningSeed[32], publicKey[32], nonce[32];
            hx(seedHex, miningSeed, 32); hx(pkHex, publicKey, 32); hx(nonceHex, nonce, 32);
            if (!ensurePool(miningSeed)) { printf("{\"error\":\"pool_alloc\"}\n__ATLAS_EOF__\n"); fflush(stdout); continue; }
            runOne(publicKey, nonce, expected, g_pool);
            printf("__ATLAS_EOF__\n"); fflush(stdout);
        }
        return 0;
    }

    // One-shot mode (unchanged output).
    if (argc < 5) { fprintf(stderr, "usage: verifier_hist <seedHex> <pkHex> <nonceHex> <expected|-1>\n"); return 2; }
    unsigned char miningSeed[32], publicKey[32], nonce[32];
    hx(argv[1], miningSeed, 32); hx(argv[2], publicKey, 32); hx(argv[3], nonce, 32);
    long expected = atol(argv[4]);
    if (!ensurePool(miningSeed)) return 2;
    runOne(publicKey, nonce, expected, g_pool);
    return 0;
}
