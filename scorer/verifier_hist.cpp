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

int main(int argc, char** argv) {
    if (argc < 5) { fprintf(stderr, "usage: verifier_hist <seedHex> <pkHex> <nonceHex> <expected|-1>\n"); return 2; }
    unsigned char miningSeed[32], publicKey[32], nonce[32];
    hx(argv[1], miningSeed, 32); hx(argv[2], publicKey, 32); hx(argv[3], nonce, 32);
    long expected = atol(argv[4]);
    int isHI = (nonce[0] & 1) == 0;

    unsigned char* pool = (unsigned char*)malloc(POOL_VEC_PADDING_SIZE);
    if (!pool) { fprintf(stderr, "pool alloc failed\n"); return 2; }
    unsigned char state[STATE_SIZE];
    generateRandom2Pool(miningSeed, state, pool);

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
    free(pool);
    return 0;
}
