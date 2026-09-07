# xdevelop 仓库瘦身计划

日期：2026-09-08

范围：`xdevelop` 分支及其 Git 历史、开发工作区和发布产物管理

原则：先做可逆的本地清理，再修正当前树，最后在团队协同窗口重写历史。

## 1. 当前基线

盘点时 `xdevelop` 与 `origin/xdevelop` 均指向 `2bb5dd9`，工作区干净。

| 指标 | 当前值 | 结论 |
| --- | ---: | --- |
| 当前分支受 Git 跟踪的文件 | 3.55 MiB | 当前源码树本身并不大 |
| `.git` 目录 | 94.17 MiB | 主要由历史发布 DLL 和恢复包构成 |
| `src/bin` + `src/obj` | 209.54 MiB | 可再生成的构建产物 |
| `tests/**/bin` + `tests/**/obj` | 15.51 MiB | 可再生成的测试产物 |
| `publish/` + `publish-test/` | 328.19 MiB | 本地发布产物，已被忽略但仍占磁盘 |
| 可直接清理的本地生成物合计 | 约 553.24 MiB | 不需要改写 Git 历史 |
| `tmpobj/` | 1.28 MiB | 构建中间产物被误提交，约占当前跟踪内容的 36% |

历史中的主要大对象来自 `recovered-bundle/` 和 `publish-test/`，包括 15.38 MiB 的 `PresentationFramework.dll`、12.94 MiB 的 `System.Windows.Forms.dll` 和 12.56 MiB 的 `System.Private.CoreLib.dll`。此外：

- `origin/main` 当前树约 151.16 MiB；
- `backup/develop-before-purge` 当前树约 169.67 MiB；
- 只清理 `xdevelop` 的当前文件不会让远端仓库明显变小；
- Git LFS 不适合这些可从 .NET SDK/NuGet 重新生成的运行库，也不能自动清掉既有历史。

## 2. 目标与验收标准

- [ ] 干净检出后，受跟踪内容不超过 3 MiB，并且不包含 `bin/`、`obj/`、`tmpobj/`、`publish*/` 或第三方运行库。
- [ ] 日常开发完成一次“清理—还原—构建—测试”后，仓库目录可稳定回到约 10 MiB 以内（不含 `.git` 和开发者主动保留的发布包）。
- [ ] 重写并重新克隆后，`.git` 目标不超过 15 MiB；若未达成，至少相对当前 94.17 MiB 降低 80%。
- [ ] 全部活动分支和标签中不存在未批准的 5 MiB 以上 blob。
- [ ] `dotnet restore`、`dotnet build .\AIHelper.sln` 和 `dotnet test .\AIHelper.sln` 全部通过。
- [ ] 发布包从源码仓库迁出，通过 CI Artifact 或 GitHub Release 分发。

## 3. 分阶段任务

### P0：当天可做，零历史风险

- [x] **建立可重复的体积基线**：已增加 `tools/Measure-RepositorySize.ps1`，输出工作区、跟踪文件、Git 对象库和历史大 blob 四项指标；结果不提交机器绝对路径。_新建维护工具。_
- [x] **清理本地生成物**：已先用 `git clean -ndX` 预览，再定向清理 `publish/`、`publish-test/`、各项目的 `bin/` 和 `obj/`。_复用现有 `.gitignore`。_
- [x] **验证可再生成性**：清理后 restore、Release build、115 项测试和 win-x64 自包含单文件 publish 均通过；验证生成物随后再次清理。_复用现有解决方案和测试项目。_

预期收益：本机仓库目录立即减少约 553 MiB；不改变提交历史和远端分支。

### P1：修正当前分支

- [x] **移除误提交的 `tmpobj/`**：已从 Git 索引删除整个目录，在 `.gitignore` 增加根级 `/tmpobj/`；源码、项目文件和解决方案均无引用。_修改忽略规则；删除生成组件。_
- [x] **加入大文件门禁**：已增加 PowerShell 检查脚本和 GitHub Actions，默认拒绝新增的 5 MiB 以上 blob；确有必要的资源使用 `.large-file-allowlist` 显式白名单。_新建仓库治理检查。_
- [ ] **明确发布产物出口**：CI 发布到流水线 Artifact/GitHub Release；本地发布输出到仓库外或在验证后清理。_修改发布流程，不修改业务组件。_
- [ ] **复核辅助项目和一次性迁移脚本**：确认 `ResReader/`、`test_api_proj/` 及根目录 `fix_*.py`、`patch*.py`、`refactor_*.py` 是否仍需长期维护；仅在责任人确认无保留价值后删除。_候选清理项，体积收益很小，不作为首要任务。_

预期收益：当前跟踪树约从 3.55 MiB 降至 2.27 MiB，并阻止问题再次发生。

### P2：维护窗口内重写 Git 历史

- [ ] **冻结写入并通知协作者**：暂停向 `main`、`develop`、`xdevelop` 推送，记录受保护分支、标签、开放 PR 和部署引用。_流程任务。_
- [ ] **创建仓库外备份**：使用 `git bundle create` 生成包含全部分支和标签的只读备份，校验 bundle 后再继续。_新建可回滚备份。_
- [ ] **确定保留的 refs**：决定是否保留 `backup/develop-before-purge`；若保留，也必须参与重写，否则本地和远端仍会引用大对象。_关键决策点。_
- [ ] **使用 `git filter-repo` 删除历史生成目录**：从所有保留的分支和标签中移除 `recovered-bundle/`、`publish-test/`、`tmpobj/`，并根据完整大文件报告补充其他生成路径。_重写历史，所有相关 commit SHA 会变化。_
- [ ] **隔离验证重写结果**：从重写后的仓库新建临时克隆，执行 `git fsck --full`、大 blob 扫描、restore、build、test 和 publish；确认主程序资源完整。_独立验收环境。_
- [ ] **原子更新远端**：在确认备份和验证通过后，按受保护分支流程更新 `main`、`develop`、`xdevelop` 及相关标签，并删除或重写遗留备份 refs。_需要管理员协作和强制更新权限。_
- [ ] **要求团队重新克隆**：不要让旧克隆直接 merge/push 回新历史；旧仓库只读保留到观察期结束。_协作防回灌。_

预期收益：删除当前 `.git` 中绝大部分约 94 MiB 的历史发布二进制；最终数值以重新克隆后的 `git count-objects -vH` 为准。

### P3：持续治理

- [ ] **CI 每周体积审计**：输出最大 20 个 blob、当前树体积和相对基线增量；超过阈值时失败并给出文件路径。_新建自动化检查。_
- [x] **发布策略文档化**：README 已说明构建输出不进入源码控制，正式发布包应放入 CI Artifact 或 GitHub Release。_修改现有文档。_
- [ ] **季度依赖审计**：检查重复 PackageReference、未使用依赖和运行时发布模式；依赖调整必须通过功能与启动测试。_修改现有项目配置时需单独评审。_

## 4. 发布包体积的独立优化（可选）

当前 `publish/AIHelper-win-x64/AIHelper.exe` 约 160.66 MiB。这是“交付物体积”而非“源码仓库体积”，不应与历史清理混为一个变更。建议另开任务比较：

1. 框架依赖发布：体积最小，但目标机器必须安装匹配的 .NET Desktop Runtime；
2. 自包含单文件发布：部署方便，但体积较大；可评估单文件压缩；
3. WPF trimming：潜在收益较高，但反射、资源和第三方控件风险也高，只有完整回归测试后才能启用；
4. ReadyToRun：通常以更大体积换启动性能，不应作为瘦身默认选项。

## 5. 回滚与风险控制

- 历史重写前不删除仓库外 bundle，观察期建议至少 7 天。
- 强制更新远端前保存旧 tip SHA，并确保部署系统没有硬编码旧 SHA。
- GitHub 等托管平台可能延迟回收服务端不可达对象；以“新克隆体积”作为首要验收指标，必要时再申请服务端垃圾回收。
- 若重写窗口暂时无法协调，先完成 P0、P1 和门禁；这能解决本机 553 MiB 与后续增量问题，但不会消除 94 MiB 的历史对象库。

## 6. 推荐执行顺序

`P0 本地清理与验证` → `P1 当前树修正与门禁` → `确定 refs/维护窗口` → `P2 历史重写` → `重新克隆验收` → `P3 持续治理`
