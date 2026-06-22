#!/usr/bin/env bash
# 导出可在 NAS(旧版 Docker)上 `docker load` 的镜像包。
#
# 产物(dist/):
#   azbackup-backup-amd64.tar.gz   linux/amd64,Docker 镜像格式(manifest.json,非 OCI)
#   azbackup-restore-amd64.tar.gz  同上
#   docker-compose.yml             以 env 驱动的 compose(从环境变量填入连接串/口令)
#
# 关键:旧 NAS(如 Synology/QNAP 的 Docker 20.10)`docker load` 只认传统 Docker Image
# Spec(顶层 <hash>.tar 各层 + manifest.json,无 oci-layout)。buildx 的 type=docker/oci
# 都会带 OCI 痕迹(blobs/ + oci-layout),旧 docker 会失败。因此这里用 buildx 出 OCI 中间
# 产物,再用 skopeo 转成传统 docker-archive 格式。需要能拉取 quay.io/skopeo/stable。
#
# 用法:
#   ./build/nas-export.sh                 # 构建 backup+restore,compose 用占位符
#   ./build/nas-export.sh backup          # 只构建 backup
#   # 让生成的 compose 直接带上真实配置:
#   AZURE_STORAGE_CONNECTION_STRING='...' AZBACKUP_PASSWORD='口令' \
#   AZURE_STORAGE_CONTAINER=test NAS_SOURCE_PATH=/volume1/data \
#     ./build/nas-export.sh all
#
# 把整个 dist/ 拷到 NAS,然后:
#   docker load -i azbackup-backup-amd64.tar.gz
#   docker load -i azbackup-restore-amd64.tar.gz
#   docker compose run --rm backup
set -euo pipefail

cd "$(dirname "$0")/.."

TAG="${TAG:-amd64}"
PLATFORM="${PLATFORM:-linux/amd64}"
OUT="${OUT:-dist}"
TARGET="${1:-all}"
mkdir -p "$OUT"

SKOPEO_IMAGE="${SKOPEO_IMAGE:-quay.io/skopeo/stable:latest}"

build_one() {
  local name="$1" df="$2"
  local image="azbackup-${name}:${TAG}"
  local ocitar="$OUT/.${name}-oci.tar"          # buildx OCI 中间产物
  local tar="$OUT/azbackup-${name}-${TAG}.tar"  # 最终传统 docker-archive

  echo ">> 构建 ${image}(${PLATFORM},OCI 中间产物)..."
  docker buildx build \
    --platform "$PLATFORM" \
    -f "docker/${df}" \
    -t "$image" \
    --output "type=oci,dest=${ocitar}" \
    .

  echo ">> 用 skopeo 转换为传统 docker-archive 格式..."
  rm -f "$tar"
  docker run --rm -v "$(pwd)/${OUT}:/work" "$SKOPEO_IMAGE" \
    copy "oci-archive:/work/$(basename "$ocitar")" \
         "docker-archive:/work/$(basename "$tar"):${image}"
  rm -f "$ocitar"

  echo ">> 压缩 → ${tar}.gz"
  gzip -f "$tar"

  # 校验:传统格式(manifest.json,无 oci-layout)。
  local listing; listing="$(tar tzf "${tar}.gz")"
  if ! grep -q '^manifest.json$' <<<"$listing"; then
    echo "   ❌ 未发现 manifest.json!" >&2; exit 1
  fi
  if grep -q 'oci-layout' <<<"$listing"; then
    echo "   ❌ 仍含 oci-layout,转换失败" >&2; exit 1
  fi
  echo "   ✅ 传统 Docker 镜像格式(manifest.json,无 oci-layout)——旧版 docker load 兼容"
}

case "$TARGET" in
  backup)  build_one backup  backup.Dockerfile ;;
  restore) build_one restore restore.Dockerfile ;;
  all)
    build_one backup  backup.Dockerfile
    build_one restore restore.Dockerfile
    ;;
  *) echo "usage: $0 [backup|restore|all]" >&2; exit 2 ;;
esac

# ---- 生成 compose(主要是 env)----
CONN="${AZURE_STORAGE_CONNECTION_STRING:-DefaultEndpointsProtocol=https;AccountName=改我;AccountKey=改我;EndpointSuffix=core.windows.net}"
CONTAINER="${AZURE_STORAGE_CONTAINER:-test}"
PASSWORD="${AZBACKUP_PASSWORD:-改成你的口令}"
SRCPATH="${NAS_SOURCE_PATH:-/volume1/path/to/backup}"
DATATIER="${AZBACKUP_DATA_TIER:-Hot}"
RETCOUNT="${AZBACKUP_RETENTION_COUNT:-7}"

compose="$OUT/docker-compose.yml"
cat > "$compose" <<YAML
# AzureBackup — NAS 部署(由 build/nas-export.sh 生成)
#
# 1) 加载镜像:
#      docker load -i azbackup-backup-amd64.tar.gz
#      docker load -i azbackup-restore-amd64.tar.gz
# 2) 跑一次备份:
#      docker compose run --rm backup
# 3) 还原到 ./restore-target:
#      docker compose run --rm restore
#
# ⚠️ 本文件含存储密钥,请勿提交到 git / 公开分享。
# ⚠️ 测试用 AZBACKUP_DATA_TIER=Hot;生产改 Archive(还原会触发解冻,数小时)。

services:
  backup:
    image: azbackup-backup:${TAG}
    environment:
      AZURE_STORAGE_CONNECTION_STRING: "${CONN}"
      AZURE_STORAGE_CONTAINER: "${CONTAINER}"
      AZBACKUP_PASSWORD: "${PASSWORD}"
      AZBACKUP_DATA_TIER: "${DATATIER}"
      AZBACKUP_RETENTION_COUNT: "${RETCOUNT}"
      # 可选:
      # AZBACKUP_RETENTION_DAYS: "30"
      # AZBACKUP_DRY_RUN: "true"
      # AZBACKUP_EXCLUDE_FILE: "/config/exclude.txt"
    volumes:
      - "${SRCPATH}:/backup/source:ro"
    restart: "no"

  restore:
    image: azbackup-restore:${TAG}
    environment:
      AZURE_STORAGE_CONNECTION_STRING: "${CONN}"
      AZURE_STORAGE_CONTAINER: "${CONTAINER}"
      AZBACKUP_PASSWORD: "${PASSWORD}"
      # AZRESTORE_SNAPSHOT: "latest"     # 或具体快照 id
      # AZRESTORE_SELECT: "/config/select.txt"  # 仅还原清单内文件
      # AZRESTORE_VERIFY: "local"        # 只校验、不落盘
    volumes:
      - "./restore-target:/restore/target"
    restart: "no"
YAML

echo
echo ">> 生成 ${compose}"
[ "$CONN" = "${AZURE_STORAGE_CONNECTION_STRING:-}" ] && [ -n "${AZURE_STORAGE_CONNECTION_STRING:-}" ] \
  && echo "   ✅ 已写入真实连接串" || echo "   ℹ️ 连接串/口令为占位符,请在 ${compose} 内替换"
echo
echo ">> 完成。dist/ 内容:"
ls -lh "$OUT"
