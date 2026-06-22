# syntax=docker/dockerfile:1
# Image for the backup tool (azbackup). Build context = repo root.

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/AzureBackup.Core/AzureBackup.Core.csproj       src/AzureBackup.Core/
COPY src/AzureBackup.Backup/AzureBackup.Backup.csproj   src/AzureBackup.Backup/
RUN dotnet restore src/AzureBackup.Backup/AzureBackup.Backup.csproj

COPY src/ src/
RUN dotnet publish src/AzureBackup.Backup/AzureBackup.Backup.csproj \
        -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# xz = LZMA2 (same algorithm family as 7-Zip) for max-compression streaming.
RUN apt-get update \
    && apt-get install -y --no-install-recommends xz-utils \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The volume to back up is mounted here at runtime (read-only):
#   -v /data:/backup/source:ro
VOLUME ["/backup/source"]

ENTRYPOINT ["./azbackup"]
