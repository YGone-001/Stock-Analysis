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

public partial class SparrowLegacyViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly SparrowLegacyScannerService _scannerService;
    private CancellationTokenSource _cts;

    private SparrowLegacyScanParameters _parameters = new SparrowLegacyScanParameters();

    public SparrowLegacyScanParameters Parameters
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

    public string StartButtonText => IsScanning ? "⏹ 停止漏斗选股" : "🚀 执行漏斗选股 (14:30专用)";

    private string _logText = "注意：线程越多，扫描速度越快，但是越容易数据错误导致结果异常\n";
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

    private string _progressDesc = "准备就绪";
    public string ProgressDesc
    {
        get => _progressDesc;
        set { _progressDesc = value; OnPropertyChanged(); }
    }

    private string _statsDesc = "";
    public string StatsDesc
    {
        get => _statsDesc;
        set { _statsDesc = value; OnPropertyChanged(); }
    }



    public SparrowLegacyViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _scannerService = new SparrowLegacyScannerService(mainVm.DataProvider);
    }

    private void AppendLog(string msg, bool isHighlight = false)
    {
        string prefix = $"[{DateTime.Now:HH:mm:ss}] {(isHighlight ? "🔍 " : "")}";
        LogText += $"{prefix}{msg}\n";
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsScanning)
        {
            _cts?.Cancel();
            AppendLog("⚠️ 正在拉起手刹，停止漏斗扫描并保存当前进度...");
            return;
        }

        var stockNameMap = _mainVm.StockVM?.StockNameMap;
        if (stockNameMap == null || stockNameMap.Count == 0)
        {
            HandyControl.Controls.MessageBox.Show("全量代码表尚未加载，请稍等几秒或检查网络！", "提示", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Exclamation);
            return;
        }

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
        ProgressDesc = "正在扫描...";
        StatsDesc = "";

        // Apply parameter scaling
        var activeParams = new SparrowLegacyScanParameters
        {
            MacroDef = Parameters.MacroDef,
            MinRise = Parameters.MinRise,
            MaxRise = Parameters.MaxRise,
            VolRatio = Parameters.VolRatio,
            MinAmount = Parameters.MinAmount * 10000.0,
            CheckMA60 = Parameters.CheckMA60,
            MinAdhesion = Parameters.MinAdhesion / 100.0,
            MaxAdhesion = Parameters.MaxAdhesion / 100.0,
            MaxConcurrency = Parameters.MaxConcurrency,
            UseCache = Parameters.UseCache
        };

        var progress = new Progress<SparrowLegacyScanReport>(report =>
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
            if (report.P2Survivors.HasValue)
            {
                ProgressDesc = $"快照海选: {report.ProgressValue} / {ProgressMax}";
                StatsDesc = $"幸存: {report.P2Survivors.Value} 只";
            }
            if (report.P3Winners.HasValue)
            {
                ProgressDesc = $"深度核验: {report.ProgressValue} / {ProgressMax}";
                StatsDesc = $"终极入围: {report.P3Winners.Value} 只";
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

                _mainVm.StockVM.AddGroup($"选股_{DateTime.Now:yyyyMMdd}", list);
                Growl.Success($"已同步入围 {results.Count} 只标的！");
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
