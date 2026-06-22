#!/usr/bin/env bash
# 本地端到端测试:用 Azurite(Azure Storage 模拟器)跑完整 备份→去重→还原→逐字节校验。
#
# 无需任何 Azure 凭据。关键点:数据层用 Hot(AZBACKUP_DATA_TIER=Hot),
# 因为 Azurite 不支持 Archive 解冻;逻辑链路与真实 Azure 一致。
#
# 用法:
#   ./build/local-e2e-test.sh           # 复用已有 :local 镜像(没有则报错提示构建)
#   BUILD=1 ./build/local-e2e-test.sh   # 先构建镜像(本机单架构)
#   KEEP=1  ./build/local-e2e-test.sh   # 测试后保留 azurite/网络/临时目录(便于排查)
#
# 退出码:0 全过;非 0 表示某步失败。
set -euo pipefail

cd "$(dirname "$0")/.."

BUILD="${BUILD:-0}"
KEEP="${KEEP:-0}"
NET="azbk-e2e-net"
AZURITE="azbk-e2e-azurite"
CONTAINER="testbk"
PASSWORD="e2e-pw-123"
SRC="$(mktemp -d /tmp/azbk-e2e-src.XXXXXX)"
DST="$(mktemp -d /tmp/azbk-e2e-restore.XXXXXX)"

# Azurite 固定开发账号(公开的众所周知凭据,仅模拟器用)。
CONN="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://${AZURITE}:10000/devstoreaccount1;"

pass() { printf '  \033[32m✅ %s\033[0m\n' "$1"; }
fail() { printf '  \033[31m❌ %s\033[0m\n' "$1"; exit 1; }
step() { printf '\n\033[1m>> %s\033[0m\n' "$1"; }

cleanup() {
  if [[ "$KEEP" == "1" ]]; then
    echo ">> KEEP=1:保留 ${AZURITE} / ${NET} / ${SRC} / ${DST}"
    return
  fi
  docker rm -f "$AZURITE" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -rf "$SRC" "$DST"
}
trap cleanup EXIT

# 在 azurite 网络内跑 backup/restore 镜像的辅助函数。
run_backup()  { docker run --rm --network "$NET" -v "$SRC:/backup/source:ro"  "$@" azbackup-backup:local; }
run_restore() { docker run --rm --network "$NET" -v "$DST:/restore/target"     "$@" azbackup-restore:local; }
base_env=(-e AZURE_STORAGE_CONNECTION_STRING="$CONN" -e AZURE_STORAGE_CONTAINER="$CONTAINER" -e AZBACKUP_PASSWORD="$PASSWORD")

# ---- 0. 构建镜像(可选)----
if [[ "$BUILD" == "1" ]]; then
  step "构建镜像(本机单架构)"
  arch="$(uname -m)"; plat="linux/amd64"; [[ "$arch" == "arm64" || "$arch" == "aarch64" ]] && plat="linux/arm64"
  PLATFORMS="$plat" PUSH=0 ./build/docker-build.sh all
fi
docker image inspect azbackup-backup:local  >/dev/null 2>&1 || fail "缺少 azbackup-backup:local —— 先跑 BUILD=1 ./build/local-e2e-test.sh"
docker image inspect azbackup-restore:local >/dev/null 2>&1 || fail "缺少 azbackup-restore:local —— 先跑 BUILD=1 ./build/local-e2e-test.sh"

# ---- 1. 起 Azurite ----
step "启动 Azurite"
docker network create "$NET" >/dev/null 2>&1 || true
docker rm -f "$AZURITE" >/dev/null 2>&1 || true
docker run -d --name "$AZURITE" --network "$NET" \
  mcr.microsoft.com/azure-storage/azurite \
  azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck >/dev/null
sleep 3
pass "Azurite 运行中"

# ---- 2. 预建 container(代码不自动建)----
step "预建 container '$CONTAINER'"
docker run --rm --network "$NET" mcr.microsoft.com/azure-cli \
  az storage container create --name "$CONTAINER" --connection-string "$CONN" >/dev/null
pass "container 已建"

# ---- 3. 生成测试数据 ----
step "生成测试数据"
mkdir -p "$SRC/sub/deep" "$SRC/empty-dir"
# 可压缩文本(重复行);避免 `yes | head`,它在 pipefail 下会因 SIGPIPE 误判失败。
for i in 1 2 3; do
  line="compressible line $i $(printf 'x%.0s' {1..200})"
  for _ in $(seq 500); do printf '%s\n' "$line"; done > "$SRC/text$i.txt"
done
head -c 1048576 /dev/urandom > "$SRC/random.bin"        # 不可压
echo "nested file content"     > "$SRC/sub/note.md"
head -c 300000  /dev/urandom   > "$SRC/sub/deep/data.dat"
head -c 50000   /dev/urandom   > "$SRC/photo.jpg"        # 不压缩扩展名
touch -t 202001010101.01 "$SRC/text1.txt"               # 特定 mtime
pass "$(find "$SRC" -type f | wc -l | tr -d ' ') 个文件 + 空目录"

# ---- 4. 备份 ×2(第二次验证去重)----
step "首次备份"
out="$(run_backup "${base_env[@]}" -e AZBACKUP_DATA_TIER=Hot -e AZBACKUP_RETENTION_COUNT=7)"; echo "  $out"
echo "$out" | grep -q "packs=[1-9]" || fail "首次备份未产生 pack"
pass "首次备份产生了 pack"

step "二次备份(应零上传 = 去重)"
out="$(run_backup "${base_env[@]}" -e AZBACKUP_DATA_TIER=Hot -e AZBACKUP_RETENTION_COUNT=7)"; echo "  $out"
echo "$out" | grep -q "new/mod=0 packs=0 vols=0 bytes=0" || fail "去重失败,二次备份有上传"
pass "去重生效:零上传"

# ---- 5. 还原 ----
step "还原最新快照"
out="$(run_restore "${base_env[@]}")"; echo "$out" | sed 's/^/  /'
echo "$out" | grep -q "failures=0" || fail "还原有失败项"
pass "还原 0 失败"

# ---- 6. 逐字节校验 ----
step "校验:源 vs 还原"
diff -r "$SRC" "$DST" && pass "diff -r 逐字节一致" || fail "源与还原不一致"
[[ -d "$DST/empty-dir" ]] && pass "空目录已还原" || fail "空目录缺失"

# ---- 7. 改动产新包 + 内置 local verify ----
step "改动产新包"
echo "MODIFIED" >> "$SRC/text2.txt"; echo "new file" > "$SRC/added.txt"
out="$(run_backup "${base_env[@]}" -e AZBACKUP_DATA_TIER=Hot -e AZBACKUP_RETENTION_COUNT=7)"; echo "  $out"
echo "$out" | grep -q "new/mod=2 packs=1" || fail "改动未按预期只上传变更"
pass "仅上传变更"

step "内置 local verify"
out="$(run_restore "${base_env[@]}" -e AZRESTORE_VERIFY=local)"; echo "$out" | tail -1 | sed 's/^/  /'
echo "$out" | grep -q "verify OK" || fail "local verify 未通过"
pass "verify OK"

# ---- 8. 错密码拒绝 ----
step "错密码应被拒绝"
if run_restore -e AZURE_STORAGE_CONNECTION_STRING="$CONN" -e AZURE_STORAGE_CONTAINER="$CONTAINER" \
     -e AZBACKUP_PASSWORD="WRONG" >/dev/null 2>&1; then
  fail "错密码竟然通过了"
fi
pass "错密码被拒"

printf '\n\033[1;32m==== 本地端到端测试全部通过 ====\033[0m\n'
