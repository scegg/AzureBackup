# 使用(azbackup)

还原工具见 [restore.md](restore.md)。完整配置见 [configuration.md](configuration.md)。

## 单次备份(外部调度,如 K8s CronJob)

```bash
docker run --rm \
  -v /data:/backup/source:ro \
  -v /etc/azbackup/exclude:/config/exclude:ro \
  -e AZURE_STORAGE_ACCOUNT=myaccount \
  -e AZURE_STORAGE_CONTAINER=backups \
  -e AZURE_STORAGE_CONNECTION_STRING=... \
  -e AZBACKUP_PASSWORD_FILE=/run/secrets/azbackup_pw \
  -v /path/pw.txt:/run/secrets/azbackup_pw:ro \
  -e AZBACKUP_EXCLUDE_FILE=/config/exclude \
  -e AZBACKUP_RETENTION_COUNT=14 \
  -e AZBACKUP_RETENTION_DAYS=30 \
  azbackup-backup:local
```

`AZBACKUP_CRON` 未设置 → 跑一次即退出(适合外部调度)。

## 容器内常驻调度

```bash
docker run -d --restart=unless-stopped \
  -v /data:/backup/source:ro \
  -e AZBACKUP_CRON="0 3 * * *" \
  -e AZBACKUP_GC_MODE=cron \
  -e AZBACKUP_GC_CRON="0 5 * * 0" \
  ...其余同上... \
  azbackup-backup:local
```

- 到点触发;**上次未完成则跳过本次**(锁租约)。
- GC 可随备份执行(`after-backup`)或独立 cron(示例:每周日 5 点)。

## 多任务(一个容器串联多个备份)

挂入一个 jobs 文件,一个容器按序跑完全部,一个 cron 触发整批:

```bash
docker run -d --restart=unless-stopped \
  -v /srv/web:/backup/web:ro \
  -v /srv/db:/backup/db:ro \
  -v /etc/azbackup/jobs.yaml:/config/jobs.yaml:ro \
  -e AZBACKUP_JOBS_FILE=/config/jobs.yaml \
  -e AZBACKUP_CRON="0 3 * * *" \
  -e AZURE_STORAGE_ACCOUNT=myaccount \
  -e AZBACKUP_PASSWORD_FILE=/run/secrets/azbackup_pw \
  -v /path/pw.txt:/run/secrets/azbackup_pw:ro \
  azbackup-backup:local
```

`jobs.yaml` 格式与 env 三类作用域见 [configuration.md](configuration.md#jobs-文件多任务可选)。
不提供 `AZBACKUP_JOBS_FILE` 即单任务模式(全部取 env)。

## 排除文件(gitignore 语义)

```gitignore
# 排除所有日志
**/*.log
# 但保留这个
!important/keep.log
# 排除缓存目录
cache/
```

## 退出码(草案)

| 码 | 含义 |
|----|------|
| 0 | 成功 |
| 2 | 配置/参数错误 |
| 3 | 因锁被占用而跳过(上次未完成) |
| 4 | 部分失败(已记录,快照根对象未发布;多任务下指某些 job 失败) |
| 1 | 其他失败 |
