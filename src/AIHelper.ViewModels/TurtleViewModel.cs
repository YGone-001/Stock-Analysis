using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AIHelper.Helpers;

using AIHelper.Models;
using AIHelper.Services.StockData;
using HandyControl.Controls;
using Serilog;

#pragma warning disable CS8602, CS8618
#pragma warning disable CS8602, CS8618
namespace AIHelper.ViewModels;

public partial class TurtleViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly TurtleScannerService _scannerService;
    private CancellationTokenSource _cts;

    private TurtleScanParameters _parameters = new TurtleScanParameters();

    public TurtleScanParameters Parameters
    {
        get => _parameters;
        set { _parameters = value; OnPropertyChanged(); }
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning != value)
            {
                _isScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }

    public string StartButtonText => IsScanning ? "⏹ 停止扫描" : "🚀 执行海龟突破选股 (东财高速通道)";

    private string _logText = "⚡ 海龟引擎已就绪！已回归 NetworkHelper，请点击【接口诊断】查看原始数据。\n";
    public string LogText
    {
        get => _logText;
        set { _logText = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private double _progressMax = 100;
    public double ProgressMax
    {
        get => _progressMax;
        set { _progressMax = value; OnPropertyChanged(); }
    }



    public TurtleViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _scannerService = new TurtleScannerService();
    }

    private void AppendLog(string msg, bool isHighlight = false)
    {
        string prefix = $"[{DateTime.Now:HH:mm:ss}] {(isHighlight ? "🐢 " : "")}";
        LogText += $"{prefix}{msg}\n";
    }

    [RelayCommand]
    private async Task Test()
    {
        AppendLog("\n🪺 [网络诊断] 正在向东方财富发送 000001(平安银行) K线请求...");
        try
        {
            string text = await NetworkHelper.GetDataAsync("https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=0.000001&klt=101&fqt=1&lmt=10&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61");
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("❌ [诊断结果] NetworkHelper 返回了空字符串！");
                return;
            }
            string text2 = text.Length > 800 ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text;
            AppendLog("[原始返回值] \n" + text2);
            if (text.Contains("{") && text.Contains("}"))
            {
                AppendLog("✅ [诊断结论] 接口畅通！且成功定位到 JSONP 内部的核心数据！");
            }
            else
            {
                AppendLog("❌ [诊断结论] 数据异常！没有找到 { } 包裹的 JSON 数据！", true);
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
            AppendLog($"❌ [诊断异常] {ex.GetType().Name}: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsScanning)
        {
            _cts?.Cancel();
            AppendLog("⚠️ 正在拉起手刹...");
            return;
        }

        var stockNameMap = _mainVm.StockVM?.StockNameMap;
        if (stockNameMap == null || stockNameMap.Count == 0) return;

        var targetPool = stockNameMap
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value) && !string.IsNullOrEmpty(kvp.Key) && 
                          !kvp.Value.Contains("ST") && !kvp.Key.StartsWith("688") && 
                          (kvp.Key.StartsWith("60") || kvp.Key.StartsWith("00") || kvp.Key.StartsWith("30")))
            .Select(kvp => (Code: kvp.Key, Name: kvp.Value))
            .ToList();

        IsScanning = true;
        LogText = ""; // Clear log
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ProgressValue = 0;

        // Apply parameter scaling
        var activeParams = new TurtleScanParameters
        {
            MacroDef = Parameters.MacroDef,
            N1 = Parameters.N1,
            N2 = Parameters.N2,
            MinTurnover = Parameters.MinTurnover,
            MaxTurnover = Parameters.MaxTurnover,
            MinAmount = Parameters.MinAmount * 10000.0,
            MaxConcurrency = Parameters.MaxConcurrency,
            UseCache = Parameters.UseCache
        };

        var progress = new Progress<TurtleScanReport>(report =>
        {
            if (report.LogMessage != null)
            {
                AppendLog(report.LogMessage, report.IsHighlight);
            }
            if (report.ProgressMax.HasValue)
            {
                ProgressMax = report.ProgressMax.Value;
            }
            if (report.ProgressValue.HasValue)
            {
                ProgressValue = report.ProgressValue.Value;
            }
        });

        try
        {
            var results = await _scannerService.ScanAsync(targetPool, activeParams, progress, _cts.Token);
            
            if (results != null && results.Count > 0 && !_cts.Token.IsCancellationRequested)
            {
                var list = new List<StockModel>();
                foreach (var item in results)
                {
                    int level = 0;
                    try
                    {
                        var stockGroups = _mainVm.StockVM.StockGroups;
                        foreach (var group in stockGroups)
                        {
                            foreach (var stock in group.Stocks)
                            {
                                if (stock.Code == item.Code)
                                {
                                    if (group.Header?.Contains("选股") == true)
                                    {
                                        level = 2;
                                        break;
                                    }
                                    if (level == 0) level = 1;
                                }
                            }
                            if (level == 2) break;
                        }
                    }
                    catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }

                    list.Add(new StockModel
                    {
                        Code = item.Code,
                        Name = item.Name,
                        IsChecked = true,
                        HighlightLevel = level
                    });
                }

                _mainVm.StockVM.AddGroup($"海龟_{DateTime.Now:MMdd}", list);
                Growl.Success($"突破检测完毕，擒获 {results.Count} 只海龟！");
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
            AppendLog("❌ 引擎崩溃: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }
}
