# 发布流程（打包 / 签名 / CI）

面向维护者。代码侧的硬规则在 `AGENTS.md`，这里是完整机制与失败模式。

## 1. 版本号：两个属性，各自负责一件事

`task_monitor.csproj` 里：

| 属性 | 例子 | 谁在用 |
| --- | --- | --- |
| `<Version>` | `2027.11.18` | SDK 生成 `AssemblyVersion` / `AssemblyFileVersion`。**必须是四段数字**——Win32 资源编译器不接受 `2027.11.18.BM` |
| `<InformationalVersion>` | `2027.11.18.BM` | 关于页（`VersionInfo.Current`）、打包产物名、release tag |

`<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` 必须保留，否则 CI 会把 commit hash 拼到版本串尾巴。

`release.yml` 的 tag 校验接受 **两者之一**：`v2027.11.18` 或 `v2027.11.18.BM` 都能过，只要等于其中任意一个属性。

## 2. 发布的三步（人工）

1. 改 csproj 两个属性（见上）。
2. `CHANGELOG.md` 最上方写 `## v<InformationalVersion>` 段落——Release 正文从这里抓；**缺失或为空，workflow 直接 fail**。
3. `git tag v2027.11.18.BM && git push origin v2027.11.18.BM`，其余全自动。

## 3. 打包：`tools/pack.ps1`

```powershell
pwsh -File tools\pack.ps1            # Release 构建 + dist\task_monitor-<ver>-x64.exe + .sha256
pwsh -File tools\pack.ps1 -Zip       # 再加 zip
pwsh -File tools\pack.ps1 -Sign      # 打包后签名；没有凭据就失败（宁可红，不要静默漏签）
pwsh -File tools\pack.ps1 -SkipBuild # 复用已有 Release 产物
```

产物全部落在 `dist/`，**不进版控**（`.gitignore` 里有 `/dist/` 与 `task_monitor-*-x64.*`）。

版本串解析顺序：`-Version` 参数 → csproj `<InformationalVersion>` → `<Version>`。

## 4. 签名：`tools/sign.ps1`

凭据来源优先级（先命中先用）：

1. `-PfxPath` + `-PfxPassword`
2. `-PfxBase64`（CI 用，解码到 `%TEMP%` 的随机文件名，`finally` 删除）
3. 环境变量 `SIGN_PFX_BASE64`
4. 环境变量 `SIGN_PFX` + `SIGN_PFX_PASSWORD`
5. `-Thumbprint`（证书已装在用户存储里）
6. 环境变量 `SIGN_CERT_THUMBPRINT`

全都没有时：打印警告并 `exit 0`；只有带 `-Require` 才是错误。

签名后立刻 `signtool verify /pa /v` 复核，失败就抛。时间戳走 `http://timestamp.digicert.com`（`-TimestampUrl` 可改）——没有时间戳的签名在证书过期后即失效。

`tools/new-dev-cert.ps1` 只用于本机验证流程：生成自签代码签名证书、导出到 `certs/`、打印信任它的命令。**别人机器不认**，SmartScreen 照样警告。

## 5. CI：`.github/workflows/release.yml`

| 步骤 | 行为 |
| --- | --- |
| checkout | `fetch-depth: 0`（需要 tag） |
| 校验 tag | tag（去掉 `v`）必须等于 `<Version>` 或 `<InformationalVersion>` |
| 打包 | 调 `tools/pack.ps1`，把 `ASSET_NAME` 写进 `GITHUB_ENV` |
| Release 正文 | 从 `CHANGELOG.md` 抽 `## <tag>` 段落，缺/空 → fail |
| 签名 | 有 `secrets.SIGN_PFX_BASE64` 才签（`tools/sign.ps1 -Require`），否则警告并继续；结果写进 `$GITHUB_STEP_SUMMARY` |
| `gh release create` | 附上 exe（必需）与 `.sha256`（有就附） |
| CNB（可选） | `secrets.CNB_TOKEN` 为空 → 整段跳过；目标仓库地址取 `vars.CNB_REPO`，不再硬编码上游路径 |

`sync-cnb.yml` 同理：`vars.CNB_REPO` 为空时 job 级跳过（`secrets` 不能出现在 job 级 `if`，令牌检查放在 step 上）。

## 6. 常见失败模式

- **`MSB3021`/`MSB3027` 复制失败**：Debug 实例还在跑、锁着 exe。用 `bin\Debug\net48\shutdown.sentinel` 让它自己退，别 `taskkill`。
- **`AssemblyVersion` 生成失败 / `CS7034`**：把 `-BM` 之类后缀写进了 `<Version>`。后缀只能放 `<InformationalVersion>`。
- **Release 正文为空**：`CHANGELOG.md` 里没有 `## v<tag>` 段落，或段落后面紧跟文件结尾（抽取逻辑读到下一个 `## ` 或 EOF）。
- **签名失败但构建继续**：说明凭据没配——这是设计行为（本地打包才会用 `-Require` 硬失败）。想强制，CI 里把签名 step 的 `if` 去掉并加 `-Require`。
- **CNB 步骤报 401**：`CNB_TOKEN` 缺 repo-code 写权限；本 fork 没有 CNB 镜像时应当留空而不是填错值。

## 7. 为什么签名材料永不进版控

`.pfx`/`.snk` 一旦泄漏，任何人都能以本项目的名义签名分发恶意程序。`.gitignore` 显式列了 `*.pfx`、`*.p12`、`*.snk`、`*.pvk`、`certs/`、`.signing/`、`secrets/`；CI 只经 secret 注入。公钥证书（`.cer`）可以提交，私钥永远不行。