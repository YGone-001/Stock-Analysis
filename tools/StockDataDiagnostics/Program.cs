using AIHelper.Services.StockData;
using AIHelper.Helpers;

string sampleCode = args.FirstOrDefault(value => !value.StartsWith("--")) ?? "600519";
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("股票数据源诊断，样本代码：" + sampleCode);

if (args.Contains("--refresh-codes"))
{
    StockDataResult refresh = await NetworkHelper.GetDataResultAsync("/api/codes?force=1");
    Console.WriteLine("代码表刷新：" + (refresh.Success ? "成功" : "失败") + "；来源 " + refresh.Source + (string.IsNullOrWhiteSpace(refresh.Error) ? "" : "；" + refresh.Error));
}

if (args.Contains("--dump-quote"))
{
    StockDataResult quote = await NetworkHelper.GetDataResultAsync("/api/quote?code=" + sampleCode);
    Console.WriteLine(quote.Json);
}

IReadOnlyList<StockDataDiagnosticItem> results = await new StockDataDiagnostics().RunAsync(sampleCode);
foreach (StockDataDiagnosticItem item in results)
{
    Console.WriteLine((item.Success ? "[PASS] " : "[FAIL] ") + item.Name + " - " + item.Message);
}

int passed = results.Count(item => item.Success);
Console.WriteLine($"结果：{passed}/{results.Count} 项通过");
return passed == results.Count ? 0 : 1;
