// annexplorer decentralized verifier: reproduces a Qubic ANN solution's score AND
// reconstructs its neural genome (graph + metrics + mutation trace) from PUBLIC inputs.
// Output: reconstruction JSON on stdout. Deterministic + open => independently checkable.
#define NO_UEFI
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <cwchar>
#include <string>

#include "public_settings.h"
#include "platform/m256.h"
#include "mining/score_engine.h"

void setMem(void* b, unsigned long long s, unsigned char v) { memset(b, (int)v, (size_t)s); }
void copyMem(void* d, const void* s, unsigned long long n) { memcpy(d, s, (size_t)n); }

using namespace score_engine;

// Epoch-222 / v1.299.1 parameter sets (from public_settings.h)
using HI  = HyperIdentityParams<512, 512, 1000, 728, 1174, 150, 316>;
using ADD = AdditionParams<14, 8, 256, 256, 256, 256, 74100>;

static int hb(const char* h){ auto v=[&](char c)->int{ if(c>='0'&&c<='9')return c-'0'; if(c>='a'&&c<='f')return c-'a'+10; if(c>='A'&&c<='F')return c-'A'+10; return 0;}; return (v(h[0])<<4)|v(h[1]); }
static void hx(const char* hex, unsigned char* out, int n){ for(int i=0;i<n;i++) out[i]=(unsigned char)hb(hex+2*i); }

static const int MAX_LINKS = 4000; // render cap; totals still reported

// Emit the full reconstruction JSON for ONE proof, scoring against an already-built seed pool.
static void runOne(const unsigned char* publicKey, const unsigned char* nonce, long expected, const unsigned char* pool) {
    int isHI = (nonce[0] & 1) == 0;
    const char* algo = isHI ? "HyperIdentity" : "Addition";

    printf("{\n");
    printf("  \"reconstructorVersion\": \"qubic-atlas-verify-1\",\n");
    printf("  \"algorithm\": \"%s\",\n", algo);

    if (isHI) {
        static ScoreHyperIdentity<HI> hi;            // large; keep static
        setMem(&hi, sizeof(hi), 0);
        hi.initMemory();
        unsigned int score = hi.computeScore(publicKey, nonce, pool);
        unsigned int threshold = HI::solutionThreshold;

        // --- read reconstructed genome from bestANN ---
        auto& ann = hi.bestANN;
        unsigned long long P = ann.population;
        const long long neighbors = (long long)HI::numberOfNeighbors;
        const long long radius = neighbors / 2;

        long long pos=0, neg=0, zero=0, evo=0;
        for (unsigned long long i=0;i<P;i++) if (ann.neuronTypes[i]==EVOLUTION_NEURON_TYPE) evo++;
        // count synapses over the active P*neighbors window
        unsigned long long synWindow = P * (unsigned long long)neighbors;
        for (unsigned long long k=0;k<synWindow;k++){ char w=ann.synapses[k]; if(w>0)pos++; else if(w<0)neg++; else zero++; }
        unsigned long long nonzero = pos+neg;

        // clamp helper (population-based ring)
        auto clamp=[&](long long idx,long long off)->long long{
            long long pop=(long long)P; long long n=idx+off;
            n += (pop & (n>>63));
            long long over=n-pop; n -= (pop & ~(over>>63));
            return n;
        };

        printf("  \"score\": %u,\n", score);
        printf("  \"threshold\": %u,\n", threshold);
        printf("  \"passesThreshold\": %s,\n", score>=threshold?"true":"false");
        printf("  \"rawScoreValid\": true,\n");
        if (expected>=0) printf("  \"expectedScore\": %ld,\n  \"scoreMatches\": %s,\n", expected, ((long)score==expected)?"true":"false");
        printf("  \"metrics\": {\n");
        printf("    \"inputNeurons\": %llu, \"outputNeurons\": %llu, \"ticks\": %llu,\n",
               (unsigned long long)HI::numberOfInputNeurons, (unsigned long long)HI::numberOfOutputNeurons, (unsigned long long)HI::numberOfTicks);
        printf("    \"population\": %llu, \"populationThreshold\": %llu, \"neighbors\": %llu,\n",
               P, (unsigned long long)HI::populationThreshold, (unsigned long long)HI::numberOfNeighbors);
        printf("    \"evolutionNeurons\": %lld, \"mutations\": %llu, \"executedMutations\": %u,\n",
               evo, (unsigned long long)HI::numberOfMutations, hi.annxTraceCount);
        {
            unsigned int acc=0; for(unsigned int i=0;i<hi.annxTraceCount;i++) if(hi.annxTrace[i].accepted) acc++;
            printf("    \"acceptedMutations\": %u, \"rejectedMutations\": %u,\n", acc, hi.annxTraceCount-acc);
        }
        printf("    \"nonzeroSynapses\": %llu, \"positiveSynapses\": %lld, \"negativeSynapses\": %lld, \"zeroSynapses\": %lld,\n",
               nonzero, pos, neg, zero);
        printf("    \"neighborSlots\": %llu, \"synapseDensity\": %.6f\n",
               synWindow, synWindow? (double)nonzero/(double)synWindow : 0.0);
        printf("  },\n");

        // --- nodes ---
        printf("  \"graph\": {\n    \"nodes\": [");
        for (unsigned long long i=0;i<P;i++){
            const char* t = ann.neuronTypes[i]==INPUT_NEURON_TYPE?"input":ann.neuronTypes[i]==OUTPUT_NEURON_TYPE?"output":"evolution";
            printf("%s{\"id\":%llu,\"type\":\"%s\",\"value\":%d}", i?",":"", i, t, (int)ann.neurons[i]);
        }
        printf("],\n");

        // --- links + full-fidelity downsampled weight matrix (over ALL synapses) ---
        const int G = 56; static long long mat[G*G]; for (int z=0;z<G*G;z++) mat[z]=0;
        printf("    \"links\": [");
        unsigned long long total=0; int rendered=0; bool first=true;
        for (unsigned long long i=0;i<P;i++){
            for (long long k=0;k<neighbors;k++){
                char w = ann.synapses[i*(unsigned long long)neighbors + (unsigned long long)k];
                if (w==0) continue;
                total++;
                long long off = (k<radius)?(k-radius):(k-radius+1); // skip self (0)
                long long tgt = clamp((long long)i, off);
                mat[(int)(i*G/P)*G + (int)((unsigned long long)tgt*G/P)] += w;
                if (rendered<MAX_LINKS){
                    printf("%s{\"source\":%llu,\"target\":%lld,\"weight\":%d}", first?"":",", i, tgt, (int)w);
                    first=false; rendered++;
                }
            }
        }
        printf("],\n");
        printf("    \"renderedLinks\": %d, \"totalLinks\": %llu, \"truncatedLinks\": %s,\n",
               rendered, total, total>(unsigned long long)rendered?"true":"false");
        printf("    \"matrix\": {\"g\": %d, \"cells\": [", G);
        for (int z=0;z<G*G;z++) printf("%s%lld", z?",":"", mat[z]);
        printf("]}\n  },\n");

        // --- mutation trace ---
        printf("  \"mutationTrace\": {\n    \"totalSteps\": %u, \"events\": [", hi.annxTraceCount);
        for (unsigned int i=0;i<hi.annxTraceCount;i++){
            auto& e=hi.annxTrace[i];
            printf("%s{\"step\":%llu,\"bestScore\":%u,\"candidateScore\":%u,\"accepted\":%s,\"populationBefore\":%llu,\"populationAfter\":%llu}",
                   i?",":"", e.step, e.bestScore, e.candidateScore, e.accepted?"true":"false", e.populationBefore, e.populationAfter);
        }
        printf("]\n  }\n");
    } else {
        static ScoreAddition<ADD> add;
        setMem(&add, sizeof(add), 0);
        add.initMemory();
        unsigned int score = add.computeScore(publicKey, nonce, pool);
        unsigned int threshold = ADD::solutionThreshold;

        auto& ann = add.bestANN;
        const unsigned long long P = ann.population;
        const unsigned long long maxN = ADD::populationThreshold;   // matrix stride
        const char* W = add.bestIncomingSynapseWeight;              // [target*maxN + src] in {-1,0,+1}

        long long pos=0, neg=0, evo=0;
        for (unsigned long long i=0;i<P;i++) if (ann.neuronTypes[i]==EVOLUTION_NEURON_TYPE) evo++;
        for (unsigned long long tgt=0;tgt<P;tgt++) for (unsigned long long src=0;src<P;src++){ char w=W[tgt*maxN+src]; if(w>0)pos++; else if(w<0)neg++; }
        unsigned long long nonzero=pos+neg, slots=P*(P>0?P-1:0);

        printf("  \"score\": %u,\n  \"threshold\": %u,\n  \"passesThreshold\": %s,\n  \"rawScoreValid\": true,\n",
               score, threshold, score>=threshold?"true":"false");
        if (expected>=0) printf("  \"expectedScore\": %ld,\n  \"scoreMatches\": %s,\n", expected, ((long)score==expected)?"true":"false");
        printf("  \"metrics\": {\n");
        printf("    \"inputNeurons\": %llu, \"outputNeurons\": %llu, \"ticks\": %llu,\n",
               (unsigned long long)ADD::numberOfInputNeurons,(unsigned long long)ADD::numberOfOutputNeurons,(unsigned long long)ADD::numberOfTicks);
        printf("    \"population\": %llu, \"populationThreshold\": %llu, \"neighbors\": %llu,\n", P, maxN, P>0?P-1:0);
        printf("    \"evolutionNeurons\": %lld, \"mutations\": %llu, \"executedMutations\": %u,\n",
               evo, (unsigned long long)ADD::numberOfMutations, add.annxTraceCount);
        { unsigned int acc=0; for(unsigned int i=0;i<add.annxTraceCount;i++) if(add.annxTrace[i].accepted) acc++;
          printf("    \"acceptedMutations\": %u, \"rejectedMutations\": %u,\n", acc, add.annxTraceCount-acc); }
        printf("    \"nonzeroSynapses\": %llu, \"positiveSynapses\": %lld, \"negativeSynapses\": %lld, \"zeroSynapses\": %lld,\n",
               nonzero, pos, neg, (long long)slots-(long long)nonzero);
        printf("    \"neighborSlots\": %llu, \"synapseDensity\": %.6f\n", slots, slots? (double)nonzero/(double)slots : 0.0);
        printf("  },\n");

        printf("  \"graph\": {\n    \"nodes\": [");
        for (unsigned long long i=0;i<P;i++){
            const char* t = ann.neuronTypes[i]==INPUT_NEURON_TYPE?"input":ann.neuronTypes[i]==OUTPUT_NEURON_TYPE?"output":"evolution";
            printf("%s{\"id\":%llu,\"type\":\"%s\",\"value\":0}", i?",":"", i, t);
        }
        printf("],\n    \"links\": [");
        const int G = 56; static long long mat[G*G]; for (int z=0;z<G*G;z++) mat[z]=0;
        unsigned long long total=0; int rendered=0; bool first=true;
        for (unsigned long long tgt=0;tgt<P;tgt++) for (unsigned long long src=0;src<P;src++){
            char w=W[tgt*maxN+src]; if(w==0) continue; total++;
            mat[(int)(src*G/P)*G + (int)(tgt*G/P)] += w;
            if (rendered<MAX_LINKS){ printf("%s{\"source\":%llu,\"target\":%llu,\"weight\":%d}", first?"":",", src, tgt, (int)w); first=false; rendered++; }
        }
        printf("],\n    \"renderedLinks\": %d, \"totalLinks\": %llu, \"truncatedLinks\": %s,\n",
               rendered, total, total>(unsigned long long)rendered?"true":"false");
        printf("    \"matrix\": {\"g\": %d, \"cells\": [", G);
        for (int z=0;z<G*G;z++) printf("%s%lld", z?",":"", mat[z]);
        printf("]}\n  },\n");

        printf("  \"mutationTrace\": {\n    \"totalSteps\": %u, \"events\": [", add.annxTraceCount);
        for (unsigned int i=0;i<add.annxTraceCount;i++){ auto& e=add.annxTrace[i];
            printf("%s{\"step\":%llu,\"bestScore\":%u,\"candidateScore\":%u,\"accepted\":%s,\"populationBefore\":%llu,\"populationAfter\":%llu}",
                   i?",":"", e.step, e.bestScore, e.candidateScore, e.accepted?"true":"false", e.populationBefore, e.populationAfter); }
        printf("]\n  }\n");
    }
    printf("}\n");
}

// ~512MB seed pool, cached across calls: regenerated ONLY when the miningSeed changes. This is the
// whole point of --serve mode — the ~610ms pool-gen is amortized over every proof sharing a seed.
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
    // ---- daemon mode: keep the process (and its seed pool) warm; one proof per stdin line ----
    // Request : "<seedHex> <pkHex> <nonceHex> <expected|-1>\n"
    // Response: the reconstruction JSON, then a lone "__ATLAS_EOF__" line as delimiter.
    // Exits on stdin EOF. The pool rebuilds only when the seed differs from the previous request.
    if (argc >= 2 && strcmp(argv[1], "--serve") == 0) {
        fprintf(stderr, "[verifier] serve mode ready (pool cached per miningSeed)\n");
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

    // ---- one-shot mode (unchanged output): single proof from argv; used by the API referee ----
    if (argc < 5) { fprintf(stderr, "usage: verifier <miningSeedHex> <pubKeyHex> <nonceHex> <expectedScore|-1> [threshold]\n"); return 2; }
    unsigned char miningSeed[32], publicKey[32], nonce[32];
    hx(argv[1], miningSeed, 32); hx(argv[2], publicKey, 32); hx(argv[3], nonce, 32);
    long expected = atol(argv[4]);
    if (!ensurePool(miningSeed)) return 2;
    runOne(publicKey, nonce, expected, g_pool);
    return 0;
}
