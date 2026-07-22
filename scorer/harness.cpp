// Native validation harness: reproduce a PRODUCTION Qubic ANN score from public inputs.
// If this prints score=321 for the known HyperIdentity solution, the scorer port is byte-exact.
#define NO_UEFI

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <cwchar>

#include "public_settings.h"
#include "platform/m256.h"
#include "mining/score_engine.h"

// Under NO_UEFI these are extern; provide the standard-library-backed definitions.
void setMem(void* b, unsigned long long s, unsigned char v) { memset(b, (int)v, (size_t)s); }
void copyMem(void* d, const void* s, unsigned long long n) { memcpy(d, s, (size_t)n); }

using namespace score_engine;

// Epoch 222 / v1.299.1 params (from public_settings.h)
//   HyperIdentity<in,out,ticks,neighbors,population,mutations,threshold>
//   Addition<in,out,ticks,neighbors,population,mutations,threshold>
using HI = HyperIdentityParams<512, 512, 1000, 728, 1174, 150, 316>;
using ADD = AdditionParams<14, 8, 256, 256, 256, 256, 74100>;

static int hexbyte(const char* h) {
    auto v=[&](char c)->int{ if(c>='0'&&c<='9')return c-'0'; if(c>='a'&&c<='f')return c-'a'+10; if(c>='A'&&c<='F')return c-'A'+10; return 0; };
    return (v(h[0])<<4)|v(h[1]);
}
static void hex2bytes(const char* hex, unsigned char* out, int n) {
    for (int i=0;i<n;i++) out[i]=(unsigned char)hexbyte(hex+2*i);
}

int main(int argc, char** argv) {
    // Gold: verified HyperIdentity solution, expected score 321, threshold 316
    const char* miningSeedHex = "7c6353ef719459edade7d19ea58982384904b0ab0e79f55d62720faf90241afb";
    const char* pubKeyHex     = "1ae55e4e5ffdca1ec05fea0931adfe11dcc4e877d2301bbc2d15f9285196e57d";
    const char* nonceHex      = "00716c692b6370756cde000a475453791dff948c38ed42a07216fe2126e9db27";
    unsigned int expected = 321;
    if (argc >= 5) { miningSeedHex=argv[1]; pubKeyHex=argv[2]; nonceHex=argv[3]; expected=(unsigned)atoi(argv[4]); }

    unsigned char miningSeed[32], publicKey[32], nonce[32];
    hex2bytes(miningSeedHex, miningSeed, 32);
    hex2bytes(pubKeyHex, publicKey, 32);
    hex2bytes(nonceHex, nonce, 32);

    printf("nonce[0]=0x%02x -> %s\n", nonce[0], (nonce[0]&1)==0 ? "HyperIdentity":"Addition");

    // Build the ~512MB random pool from the mining seed (public, deterministic).
    printf("Allocating random pool (%llu bytes)...\n", (unsigned long long)POOL_VEC_PADDING_SIZE);
    unsigned char* pool = (unsigned char*)malloc(POOL_VEC_PADDING_SIZE);
    if(!pool){ printf("alloc failed\n"); return 2; }
    unsigned char state[STATE_SIZE];
    generateRandom2Pool(miningSeed, state, pool);
    printf("Pool ready. Scoring...\n");

    ScoreEngine<HI, ADD>* engine = (ScoreEngine<HI, ADD>*)malloc(sizeof(ScoreEngine<HI, ADD>));
    engine->initMemory();
    unsigned int score = engine->computeScore(publicKey, nonce, pool);

    printf("\n=== RESULT ===\nscore    = %u\nexpected = %u\nthreshold= %u  passes=%s\nMATCH    = %s\n",
        score, expected, HI::solutionThreshold, score>=316?"yes":"no",
        score==expected ? "*** YES — byte-exact reproduction ***":"NO");
    free(pool); free(engine);
    return score==expected?0:1;
}
