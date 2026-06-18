# AIHelper 反编译与数据接口梳理

## 已恢复内容

- 原始程序: `C:\Users\pc\Desktop\strock\strock\AIHelper.exe`
- 单文件提取目录: `C:\Users\pc\Documents\Codex\2026-06-11\06-11-10-04-40-aihelper\recovered-bundle`
- 反编译源码目录: `C:\Users\pc\Documents\Codex\2026-06-11\06-11-10-04-40-aihelper\decompiled-aihelper`
- 主程序集: `recovered-bundle\AIHelper.dll`
- 技术栈: .NET 8.0.6, WPF, x64, HandyControl, WebView2, SignalR

## www.98da.com 的作用

`www.98da.com` 不是单纯一个股票名称文件地址，而是程序的数据服务基地址。代码中的 `NetworkHelper.GetDataAsync` 会把相对接口拼到 `AkServerUrl` 上，默认就是 `https://www.98da.com`。

主要接口包括:

- `/api/codes`: 股票代码和名称表
- `/api/etf?limit=10000`: ETF 代码和名称表
- `/api/search?keyword=...`: 股票/ETF 搜索
- `/api/quote?code=...`: 行情快照
- `/api/minute?code=...`: 分时数据
- `/api/minute-trade-all?code=...`: 全天逐笔/成交明细
- `/api/kline-all?code=...&type=day`: K 线数据
- `/api/index?code=...&type=day`: 指数数据
- `/api/workday?date=...`: 交易日判断

代码里也有部分东方财富直连接口，主要在窗口图表或局部行情逻辑中使用，例如 `push2.eastmoney.com` 和 `push2his.eastmoney.com`。

## 401 Unauthorized 原因

`NetworkHelper.LoadAuthIfNeededAsync()` 会请求:

`https://www.ooppp.com/soft/psw.json`

然后用 `SecurityHelper.Decrypt` 解密其中的 `u` 和 `p`，生成 Basic Auth，给 98da 请求加 `Authorization` 头。

当前测试结果: `https://www.ooppp.com/soft/psw.json` 返回 404，所以程序无法生成 `_basicAuthValue`。后续请求 `https://www.98da.com/api/codes`、`/api/etf` 等接口时没有有效认证，服务端返回 401。

## 二开建议

短期修复:

- 保留并定时生成本地 `StockNameMap.json`，让股票名称不依赖 98da。
- 将 `/api/codes` 和 `/api/etf` 改为公开行情源或本地缓存。

中期改造:

- 抽象 `IDataProvider`，把 98da、东方财富、本地缓存拆成可切换的数据源。
- 在 `NetworkHelper` 中增加更清晰的认证失败日志，不再吞掉 `LoadAuthIfNeededAsync` 异常。
- 将 `AkServerUrl`、认证方式、代理设置做成可测试配置。

长期改造:

- 将 BAML 资源还原成 XAML，方便界面二开。
- 使用 .NET 8 SDK 建立可构建工程，逐步替换反编译生成的代码和资源。
