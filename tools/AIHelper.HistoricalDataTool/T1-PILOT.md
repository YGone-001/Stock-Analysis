# 留存真实 T1 研究样本与次日来源覆盖

先留存一次真实盘中运行，再在目标交易日收盘后显式取数。默认离线，
不计算投资收益，不下单，也不补采历史分钟数据来替换原始决策证据。
工程可用、样本已留存、来源可访问、记录覆盖完整、PIT 认证是分别显示的状态。

## 1. 盘中操作：沿用 Sparrow 界面

1. 使用应用现有设置，选择操作员已获授权的行情/分钟来源。先确认该来源可用；
   如需要网关或凭据，按现有配置方式设置，不把凭据写入研究文件。
2. 在实际开市日的 14:30–14:55（北京时间）选择一个原策略及较小的 TopN，例如 5；
   勾选现有“快照采集”选项，再启动一次原策略扫描。策略选股和候选池不变。
3. 等待扫描及快照完成，检查快照状态中的 RunId、日期、实际采集完成时间及异常。
   扫描在 14:30 开始，不代表随后采到的数据在 14:30 已可用。
4. 打开现有盘中特征页，点击分析。检查候选的 5m/15m 窗口、时间、缺失及排除原因。
5. 在“T1 研究排序”页点击“PriceOnlyV1 排序（本地计算，不取数）”，再点击
   “导出可复现研究工件”。保留原始 CSV/JSON 成对包。
6. 在该页新增的“研究归档目录”填入明确授权的目录，例如仓库根目录下的
   `local-research-data\t1-samples` 的完整路径，或仓库之外的独立目录。
   点击“留存 T1 研究样本”。按钮在缺少当前运行、特征或完成导出的包时不可用。
7. 从留存状态复制样本目录，供下面命令使用。检查原始运行身份、候选数以及
   “窗口之外”提示。PIT 认证、分钟价格基准和成交可行性仍未验证。

每次留存只复制和验证现有原始证据，不触发扫描、取数或重新计算排序。
新扫描、重新分析/排序/导出、取消或关窗会使迟到的留存任务失效。
不要用过时结果冒充当前运行。正常操作不需要手工编写嵌套 JSON。

## 2. 先查看离线状态

在仓库根目录的 PowerShell 执行：

```powershell
dotnet build .\AIHelper.sln -c Release
$sample = Read-Host '粘贴界面留存状态中的完整样本目录'
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --report-t1 --sample "$sample"
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --probe-t1 --sample "$sample" --output .\local-research-data\t1-outcomes
```

第二条操作命令默认 **OFFLINE**，不会访问网关或任何市场来源，也不读取来源环境配置。
它保存明确缺失的来源状态及已验证的 schema-2 关联，显示所缺日历、日线与价格基准。
未提供样本时，`--probe-t1` 直接报告 REAL_SAMPLE_NOT_YET_CAPTURED，分母为
NOT_APPLICABLE。工程可用不表示真实样本或真实覆盖已建立。

## 3. 收盘后，显式授权一次受限取数

使用现有 Tushare 历史网关配置。网关的启动、服务端 `TUSHARE_TOKEN` 与权限设置见
[market-data-server README](../../market-data-server/README.md)。客户端沿用
`HISTORICAL_GATEWAY_URL`，不读取客户端的 Tushare token、不寻找个人配置文件。

网关已按现有方式配置且运行后：

```powershell
$env:HISTORICAL_GATEWAY_URL = 'http://127.0.0.1:8000'
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --probe-t1 --sample "$sample" --output .\local-research-data\t1-outcomes --allow-network --provider tushare --calendar-days 14
```

这个命令明确授权本次请求；没有定时轮询或新增后台重试。执行前打印日期及符号范围，
只处理一个保存的决策、至多 20 个已捕获候选。先访问现有网关 `/health` 查看服务端
Tushare 是否已配置；未配置时记录 CREDENTIAL_REQUIRED，**不访问市场提供方**。
未选择或未配置网关记录 SOURCE_NOT_CONFIGURED；连接状态无法观察时记录 SOURCE_ERROR，
不会猜测权限。实际 HTTP 403/429 分别保留为 PERMISSION_DENIED/RATE_LIMITED。

请求范围如下：

- 每个实际出现的 SSE/SZSE/BSE 交易所最多一次日历请求，范围 D 到 D+14。
  `--calendar-days` 可显式设为 1–31；这个上界是查询范围，不是目标交易日算法。
- 使用现有 MapNextTradingDay 验证完整覆盖及开市链，按交易所取得首个实际开市日。
  缺少匹配日历只影响该交易所，不使用其他市场日历替代。
- 只有目标场次已结束，才对每个候选请求一次该确切目标日期的 Raw 日线，起止日期相同。
  缺失记录不顺延目标日期、不当成停牌、不填零价格。
- 保留现有 Acquisition/CoverageEvidence 来源包及实际接收时间。网关内部既有前台
  重试行为及 retry_count 原样保留；本操作不增加重试。客户端整体预算 90 秒，
  单 HTTP 请求 20 秒，响应上限 2 MiB，且不自动跟随重定向。
- 不调用现有不受符号过滤的证券主数据/停牌/生命周期接口，也不调用会探测无关
  历史日期的全量 capabilities 操作。相关元数据保持缺失；没有伪造的 OPEN 或 NoAction。

日线可用和覆盖完整均指来源声明及本地字段校验。Raw 日线不能证明原始分钟价格的
复权基准相同。当前转换器保持决策基准 UNKNOWN_BASIS、Session UNKNOWN，且不生成
公司行为声明。因此可能看到日线覆盖 5/5，同时可用关联为 0/5；这是明确披露的限制。
PIT_AUTHENTICATION_UNVERIFIED、EXECUTION=NOT_VERIFIED 和 PRODUCTION_EVALUATION_BLOCKED
不会因为一次取数成功而解除。输入中 TEST_FIXTURE 始终不会计作真实生产样本。

## 4. 再次报告、离线导入与多日期清单

取数命令会打印 PILOT_EVIDENCE（source.json）和 ASSOCIATION_PACKAGE 的完整路径。
复制前者到变量，之后可完全离线重放，不需要手工构造声明：

```powershell
$evidence = Read-Host '粘贴 PILOT_EVIDENCE 的完整 source.json 路径'
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --report-t1 --sample "$sample" --pilot-evidence "$evidence"
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --probe-t1 --sample "$sample" --output .\local-research-data\t1-outcomes --import "$evidence"
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --report-t1 --archive-root .\local-research-data\t1-samples --outcome-root .\local-research-data\t1-outcomes --limit 25
```

`--import` 与 `--allow-network` 互斥，只接受本操作已生成的有界、版本化来源文件；
原始网络授权字段是文件的声明性来源记录，不是本次网络开关，也不是外部认证。
所有身份、日期、范围、指纹及关联都会重新验证。不能直接导入一份市场级数据集。

单样本报告打印策略、RunId、原始截止/采集时间、归档/快照/排序验证状态、完整候选池、
每个候选的交易所/目标日/原排名评分、分钟状态、来源缺失及基准阻塞原因。
来源与字段均通过且完整覆盖的计数单独显示；没有来源观察时显示 NOT_VERIFIED 或
NOT_APPLICABLE，不把未知状态伪装为已观察到的零覆盖。

清单仅检查显式指定的归档根目录，最多 100 个样本；默认 25，并显示是否还有未检查项。
可选的 --outcome-root 最多读取 100 个来源包。每个样本选用该目录中最新实际接收时间的
匹配声明，再调用原关联验证器。缺少来源输入的样本单独计数，日期/策略汇总保留
夹具数、候选数、短名单/排除数、证据分类与缺失原因。它不代表所有历史日期的完整库存。

命令退出码：0=帮助；2=有效研究诊断/预期阻塞或取消（生产评估仍阻塞）；
1=输入格式、范围、操作冲突或存储完整性错误。每个命令均可追加 `--help`。

## 5. 归档格式、时间与长期保留

```text
<archive-root>/<original-RunId>-<manifest-content-SHA256>/
  original-snapshot.json
  features.json
  <original-immutable-ranking-package-name>/
    ranking.csv
    provenance.json
  manifest.json
```

原快照 schema 1、排序 CSV/JSON 的字节与指纹保持原样。features.json 保存本次已经
完成的特征结果，不以新分析替代。manifest 保存完整源身份、参数指纹、时间、候选与质量
摘要、覆盖数、文件摘要、证据限制和来源引用。时间未被原事件记录就保持 null；
WPF 在本阶段新增记录的特征/排序/首次成功导出完成时间使用实际运行时观察。
归档创建时间在独立元数据中，不能替代任何源接收时间，也不参与确定性内容身份。

每文件上限 2 MiB。复制到临时目录、独立校验后原子发布；同内容可复用，同 RunId 的
不同内容冲突则拒绝，不覆盖或修复旧包。检查路径穿越和重解析点，临时目录清理限于
本次拥有的目录。归档不会受原有 100 份运行快照清理影响，也不按数量自动删档。

归档根目录可在 Git 之外，或在 Git 明确忽略的目录内。来源/关联输出根目录必须位于
实际 Git 忽略的本地研究目录，才能复用现有关联发布器；不得放在不可变样本目录里面。
不要把本地市场数据、source.json、关联 JSON 或凭据提交到 Git。

高级既有文件入口仅适用于已经保留了原始特征结果的操作员：

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --retain-t1 --snapshot "$snapshot" --ranking-package "$rankingPackage" --features "$originalFeatures" --archive-root .\local-research-data\t1-samples
```

三个输入必须属于同一原始运行；缺少特征即拒绝。常规盘中流程使用界面按钮，不需要这个入口。
工程夹具可加 --test-fixture；该标记只限制统计，不能授予来源信任。

## 6. 当前交付与真实试点边界

本阶段工程测试使用确定性离线夹具，包括实际 HistoricalHttpMarketDataSource 的内存 HTTP
处理器、WPF 运行所有权及真实文件发布路径。没有把夹具视为市场观察。
此次交付未执行操作员选定的真实盘中扫描或生产来源请求：REAL_SAMPLE_NOT_YET_CAPTURED。
请在下一个实际开市日按步骤 1 留存原始运行，再在各自目标交易日收盘后按步骤 3 操作。
不要事后补采再回填为原始 14:30 证据。STOP at T1-5-A；不开展收益回测或权重优化。
