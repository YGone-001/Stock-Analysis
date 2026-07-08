using AIHelper.Helpers;
using AIHelper.Services.StockData;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var r = await NetworkHelper.GetDataResultAsync("/api/quote?code=000001,600519,300750");
Console.WriteLine(r.Json);
