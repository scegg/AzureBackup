# 还原(azrestore)

独立镜像/可执行,与备份工具分离。运行时**除密码外的所有配置都来自 Azure Blob 仓库的结构文件**。

## 流程(两级选择:先选 container,再选快照)

```
1. 连接源(AZURE_STORAGE_*)
2. 选 container(= 一个备份):
     - 用户**直接指定** `AZURE_STORAGE_CONTAINER`;或
     - **不指定 → 列出该 account 下所有 container 供选择**
3. 用密码解密该仓库的 root/refs/struct(Hot 层),失败=密码错→退出
4. 列出该仓库所有备份(snapshotId + 时间)
5. 用户选择某一次备份(快照)
6. 选择范围:
     a) 全部还原
     b) 列出文件 → 树结构浏览(按需展开各级 tree 对象)→ 逐个/批量 add 选中
7. 选择目的地(AZRESTORE_TARGET_PATH)
8. **选择解冻等级**(`Standard`/`High`):还原时由用户交互指定;`AZRESTORE_REHYDRATE_PRIORITY` 作为默认/非交互回退
9. 计算所需 pack 集合 → 按所选等级解冻(rehydrate)其全部卷 → 轮询直至就绪
10. 下载 → 解密 → 解压 → 还原到目的地(含空目录重建、mtime、权限)
```

> 一个 account 下的多备份隔离与命名,见 [需求 §6.1/§6.2](requirements.md)。

## Archive 解冻(重要)

- 数据本体在 **Archive 层**,下载前必须**解冻**:`Standard` 数小时、`High` 较快但更贵。
- ⚠️ **纯增量**下,选中的文件可能分布在**多个历史 pack**,需解冻其涉及的**全部卷**。工具会先汇总待解冻清单、统一发起,再轮询。
- 解冻与下载可能产生**读取/出站流量费用**。

## 运行示例(交互式)

```bash
docker run --rm -it \
  -v /restore-here:/restore/target \
  -e AZURE_STORAGE_ACCOUNT=myaccount \
  -e AZURE_STORAGE_CONTAINER=backups \
  -e AZURE_STORAGE_CONNECTION_STRING=... \
  -e AZBACKUP_PASSWORD_FILE=/run/secrets/azbackup_pw \
  -v /path/pw.txt:/run/secrets/azbackup_pw:ro \
  azbackup-restore:local
```

## 非交互(可选,待设计)

为脚本化还原,计划提供参数化模式:`--snapshot <id> --select <file|@listfile|all> --target <path> --yes`。

## 校验(verify)

- **本地 verify**(`AZRESTORE_VERIFY=local`,本工具):**解冻 + 下载 + 解密**,逐文件比对内容与结构文件中的 hash。深度校验,需 Archive 解冻、有费用与耗时。
- **远端 verify**(查卷是否都存在)**在备份工具(azbackup)里**,可像 GC 一样定时执行,见 [配置 `AZBACKUP_VERIFY_MODE`](configuration.md) 与 [架构 §4.1](architecture.md)。

还原完成后默认按 hash **逐文件校验已还原数据**,不一致则报错并以非 0 退出。
