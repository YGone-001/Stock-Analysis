using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.Services.StockData;
using HandyControl.Controls;

namespace AIHelper.ViewModels;

public class SparrowViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _mainVm;
    private readonly SparrowScannerService _scannerService;
    private CancellationTokenSource _cts;

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Parameters
    private SparrowScanParameters _parameters = new SparrowScanParameters
    {
        MacroDef = true,
        MinRise = 0,
        MaxRise = 9.9,
        VolRatio = 1.0,
        MinAmount = 5000, // This is internally * 10000
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = 0,
        CheckAlpha = true,
        MaxConcurrency = 8,
        UseCache = true
    };

    public SparrowScanParameters Parameters
    {
        get => _parameters;
        set { _parameters = value; OnPropertyChanged(); }
    }

    // UI States
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

    public string StartButtonText => IsScanning ? "⏹ 停止扫描" : "🚀 执行漏斗选股 (东财直连引擎)";

    private string _logText = "⚡ 东财引擎已就绪！【智能 Cookie 轮换系统】已实装，随时准备金蝉脱壳！\n";
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

    private ICommand _startCommand;
    public ICommand StartCommand => _startCommand ??= new RelayCommand(ExecuteStartCommand);

    private ICommand _testCommand;
    public ICommand TestCommand => _testCommand ??= new RelayCommand(ExecuteTestCommand);

    public SparrowViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _scannerService = new SparrowScannerService(new EastMoneySpiderService());
    }

    private void AppendLog(string msg, bool isHighlight = false)
    {
        string prefix = $"[{DateTime.Now:HH:mm:ss}] {(isHighlight ? "🔍 " : "")}";
        LogText += $"{prefix}{msg}\n";
    }

    private async void ExecuteTestCommand(object parameter)
    {
        AppendLog("\n🪺 [网络诊断] 测试抗封锁智能客户端...");
        try
        {
            using var spider = new EastMoneySpiderService();
            string text = await spider.FetchEastMoneyKLineAsync("0.000001");
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("❌ [诊断结果] 返回了空字符串！");
                return;
            }
            string text2 = text.Length > 800 ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text;
            AppendLog("[原始返回值] \n" + text2);
            if (text.Contains("{") && text.Contains("}"))
            {
                AppendLog("✅ [诊断结论] 接口完全畅通！WAF 的 Cookie 挑战已被攻破！");
            }
            else
            {
                AppendLog("❌ [诊断结论] 数据异常！没有找到 { } 包裹的 JSON 数据！", true);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"❌ [诊断异常] {ex.Message}{(ex.InnerException != null ? " -> 底层原因: " + ex.InnerException.Message : "")}", true);
        }
    }

    private async void ExecuteStartCommand(object parameter)
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
        var activeParams = new SparrowScanParameters
        {
            MacroDef = Parameters.MacroDef,
            MinRise = Parameters.MinRise,
            MaxRise = Parameters.MaxRise,
            VolRatio = Parameters.VolRatio,
            MinAmount = Parameters.MinAmount * 10000.0,
            CheckMA60 = Parameters.CheckMA60,
            MinAdhesion = Parameters.MinAdhesion / 100.0,
            MaxAdhesion = Parameters.MaxAdhesion / 100.0,
            MinTurnover = Parameters.MinTurnover,
            MaxTurnover = Parameters.MaxTurnover,
            MomentumThreshold = Parameters.MomentumThreshold,
            CheckAlpha = Parameters.CheckAlpha,
            MaxConcurrency = Parameters.MaxConcurrency,
            UseCache = Parameters.UseCache
        };

        var progress = new Progress<SparrowScanReport>(report =>
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
                    catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Swallowed exception in SparrowViewModel.cs : {ex}"); }

                    list.Add(new StockModel
                    {
                        Code = item.Code,
                        Name = item.Name,
                        IsChecked = true,
                        HighlightLevel = level
                    });
                }

                _mainVm.StockVM.AddGroup($"DC选股_{DateTime.Now:MMdd}", list);
                Growl.Success($"东财引擎执行完毕，入围 {results.Count} 只！");
            }
        }
        catch (Exception ex)
        {
            AppendLog("❌ 引擎崩溃: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }
}
