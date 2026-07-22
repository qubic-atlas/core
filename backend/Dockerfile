# Qubic Atlas API (ASP.NET Core, .NET 10) — multi-stage build.
# The native C++ verifier binary is brought in from the prebuilt verifier image so the
# API can shell out to it (GET /api/verify, POST /api/jobs/:id/result).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first (cache-friendly). nuget.config points at the vendored ./nupkgs source.
COPY nuget.config ./
COPY nupkgs ./nupkgs
COPY QubicAtlas.csproj ./
RUN dotnet restore QubicAtlas.csproj
COPY . ./
RUN dotnet publish QubicAtlas.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# The verifier binaries (dynamically linked against glibc/libstdc++, both present in the
# aspnet runtime image) — shared with the worker so scores are reproduced identically.
# Multi-epoch: build3 (verifier) + the three historical era-binaries (build0/1/2) so the API
# can run the epoch-correct scorer for /api/verify and the referee recompute.
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier /usr/local/bin/verifier
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build0 /usr/local/bin/verifier-build0
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build1 /usr/local/bin/verifier-build1
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build2 /usr/local/bin/verifier-build2
COPY --from=build /app ./
ENV PORT=8099 \
    QUBIC_RPC=https://rpc.qubic.org \
    VERIFIER=/usr/local/bin/verifier \
    CACHE_DIR=/data/cache \
    ATLAS_CONFIRMATIONS=1
EXPOSE 8099
ENTRYPOINT ["dotnet", "QubicAtlas.dll"]
