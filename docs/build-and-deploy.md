# 构建与部署

## 本地构建 + 测试

```bash
./build/build.sh           # 默认 Release(restore/build/test 整个解决方案)
./build/build.sh Debug
```

## 构建 Docker 镜像

两个独立镜像,构建上下文均为仓库根:

```bash
# 多架构(amd64 + arm64)默认开启,经 docker buildx;多架构必须 --push
REGISTRY=myacr.azurecr.io/ TAG=0.1.0 PUSH=1 ./build/docker-build.sh all

# 本地单架构镜像(可 --load 到本机 docker)
PLATFORMS=linux/amd64 ./build/docker-build.sh backup
PLATFORMS=linux/arm64 ./build/docker-build.sh restore
```

- `PLATFORMS` 默认 `linux/amd64,linux/arm64`;`PUSH=1` 推送(多架构清单无法 `--load` 到本地)。
- 基础镜像 `mcr.microsoft.com/dotnet/{sdk,runtime}:10.0` 与 `p7zip-full` 均提供两种架构,buildx 自动选择。

- `docker/backup.Dockerfile` → `azbackup-backup`(含 p7zip-full,产物 `azbackup`)
- `docker/restore.Dockerfile` → `azbackup-restore`(含 p7zip-full,产物 `azrestore`)

两者均为多阶段:`sdk:10.0` 构建 → `runtime:10.0` 运行。

## CI

`.github/workflows/ci.yml`:在 PR / push 到 main 时构建 + 测试。镜像发布流水线待定。

## 部署形态(建议)

- **备份**:K8s `CronJob`(外部调度,镜像内不常驻)或常驻容器 + `AZBACKUP_CRON`。
- **还原**:按需手动 `docker run -it`(交互式选择备份与文件)。
- 凭据/密码用 Secret 挂载为文件,经 `*_FILE` 读取。

## 发布

⏸️ **暂不发布**——待功能测试通过后再启用发布流水线。

计划:
- [ ] **同时发布到 GHCR(`ghcr.io`)与 Docker Hub**(同一多架构清单推送到两个 registry)。
- [ ] 多架构 amd64 + arm64(构建脚本已支持,见上)。
- [ ] 版本与标签策略(语义化版本 + 与 `FormatVersion` 的对应)。
- [ ] CI 发布 job:在打 tag 时构建并推送两个 registry(需配置各自的登录密钥)。
