# Qubic Atlas verifier — the real Qubic Core ANN scorer(s), compiled to native CLIs.
# Bundles one binary per scorer era so it verifies any epoch 197–222 byte-exact:
#   verifier          build3  (v1.293–v1.299, epochs 215–222, current)
#   verifier-build0   v1.275–v1.281  (epochs 197–203)
#   verifier-build1   v1.282–v1.284  (epochs 204–206)
#   verifier-build2   v1.285–v1.292  (epochs 207–214)
# All four are compiled in-stage from their (shimmed) source trees for glibc consistency.
FROM debian:bookworm-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
ARG FLAGS="-std=c++17 -O2 -mavx2 -mbmi -mbmi2 -mlzcnt -mpopcnt -mrdrnd -fshort-wchar -DNO_UEFI -DNDEBUG -w"
# current (build3)
RUN chmod +x build.sh && ./build.sh && test -x ./verifier
# historical eras (source trees already carry the platform shims from build_hist.sh)
RUN for b in build0 build1 build2; do \
      clang++ $FLAGS -I coretree_$b/src -I coretree_$b \
        -I coretree_$b/lib/platform_common -I coretree_$b/lib/platform_os -I coretree_$b/lib/platform_efi \
        verifier_hist.cpp -o verifier-$b && test -x verifier-$b; \
    done

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends libstdc++6 && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/verifier /usr/local/bin/verifier
COPY --from=build /src/verifier-build0 /usr/local/bin/verifier-build0
COPY --from=build /src/verifier-build1 /usr/local/bin/verifier-build1
COPY --from=build /src/verifier-build2 /usr/local/bin/verifier-build2
# Smoke self-test: current build reproduces the known HyperIdentity score 321.
RUN verifier 7c6353ef719459edade7d19ea58982384904b0ab0e79f55d62720faf90241afb \
             1ae55e4e5ffdca1ec05fea0931adfe11dcc4e877d2301bbc2d15f9285196e57d \
             00716c692b6370756cde000a475453791dff948c38ed42a07216fe2126e9db27 321 | grep -q '"score": 321'
ENTRYPOINT ["verifier"]
