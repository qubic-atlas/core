# Qubic Atlas distributed verifier worker (.NET 10 console).
# Build context must be the parent `backend/` directory so the shared nuget.config /
# nupkgs are available:  docker build -f AtlasWorker/Dockerfile -t qubic-atlas/worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY nupkgs ./nupkgs
COPY AtlasWorker/AtlasWorker.csproj ./AtlasWorker/
RUN dotnet restore AtlasWorker/AtlasWorker.csproj
# GenomeId.cs is <Compile Include="../GenomeId.cs"> — it must sit beside the AtlasWorker/ folder.
COPY GenomeId.cs ./GenomeId.cs
COPY AtlasWorker/ ./AtlasWorker/
RUN dotnet publish AtlasWorker/AtlasWorker.csproj -c Release -o /app --no-restore

# Runtime = dotnet runtime + the same native verifier binary (the "verifier image").
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
# Multi-epoch: bundle all four era-binaries so a worker can verify any supported epoch.
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier /usr/local/bin/verifier
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build0 /usr/local/bin/verifier-build0
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build1 /usr/local/bin/verifier-build1
COPY --from=qubic-atlas/verifier:latest /usr/local/bin/verifier-build2 /usr/local/bin/verifier-build2
COPY --from=build /app ./
ENV ATLAS_URL=https://qubic-atlas.org \
    VERIFIER=/usr/local/bin/verifier \
    ATLAS_BUILDS=build0,build1,build2,build3 \
    POLL_INTERVAL_MS=3000 \
    ATLAS_KEY_FILE=/data/worker.key
ENTRYPOINT ["dotnet", "AtlasWorker.dll"]
