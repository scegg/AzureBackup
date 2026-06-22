# syntax=docker/dockerfile:1
# Image for the restore tool (azrestore) — independent of the backup image.
# Build context = repo root.

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/AzureBackup.Core/AzureBackup.Core.csproj         src/AzureBackup.Core/
COPY src/AzureBackup.Restore/AzureBackup.Restore.csproj   src/AzureBackup.Restore/
RUN dotnet restore src/AzureBackup.Restore/AzureBackup.Restore.csproj

COPY src/ src/
RUN dotnet publish src/AzureBackup.Restore/AzureBackup.Restore.csproj \
        -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends p7zip-full \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Restore destination is mounted here at runtime (writable):
#   -v /restore-target:/restore/target
VOLUME ["/restore/target"]

ENTRYPOINT ["./azrestore"]
