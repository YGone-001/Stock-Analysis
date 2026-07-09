using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;

namespace AIHelper.Views;

public partial class ImportExportWindow : Window
{
	private MainViewModel _mainVm;

	public ImportExportWindow(MainViewModel mainVm)
	{
		InitializeComponent();
		_mainVm = mainVm;
	}

	private void BtnCopy_Click(object sender, RoutedEventArgs e)
	{
		TxtData.SelectAll();
		TxtData.Copy();
		TxtStatus.Text = "✅ 已成功复制到剪贴板";
	}

	private void BtnExport_Click(object sender, RoutedEventArgs e)
	{
		if (_mainVm?.StockVM != null)
		{
			bool valueOrDefault = RbAllTabs.IsChecked.GetValueOrDefault();
			string exportText = _mainVm.StockVM.GetExportText(valueOrDefault);
			if (string.IsNullOrEmpty(exportText))
			{
				TxtStatus.Text = "⚠\ufe0f 没有可导出的打勾股票";
				return;
			}
			TxtData.Text = exportText;
			TxtStatus.Text = "✅ 导出成功，可点击下方复制";
		}
	}

	private void BtnImport_Click(object sender, RoutedEventArgs e)
	{
		if (_mainVm?.StockVM == null)
		{
			return;
		}
		string text = TxtData.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		int num = 0;
		int num2 = 0;
		string currentGroupName = "导入分组";
		StockViewModel stockVm = _mainVm.StockVM;
		string[] array = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (string.IsNullOrEmpty(text2))
			{
				continue;
			}
			if (text2.StartsWith("G"))
			{
				int num3 = text2.LastIndexOf('G');
				if (num3 == 0)
				{
					currentGroupName = text2.Substring(1).Trim();
				}
				else
				{
					currentGroupName = text2.Substring(1, num3 - 1).Trim();
				}
				if (string.IsNullOrWhiteSpace(currentGroupName))
				{
					currentGroupName = "未命名分组";
				}
				bool flag = false;
				foreach (StockGroupModel g in stockVm.StockGroups)
				{
					if (g.Header == currentGroupName)
					{
						flag = true;
						Application.Current?.Dispatcher.Invoke(() => stockVm.SelectedGroup = g);
						break;
					}
				}
				if (!flag && stockVm.AddGroupCommand.CanExecute(currentGroupName))
				{
					Application.Current?.Dispatcher.Invoke(delegate
					{
						stockVm.AddGroupCommand.Execute(currentGroupName);
					});
				}
				continue;
			}
			string text3 = text2.ToLower().Replace("sh", "").Replace("sz", "");
			if (text3.Length == 6 && int.TryParse(text3, out var _))
			{
				if (stockVm.AddStockFromImport(text3))
				{
					num2++;
				}
			}
			else
			{
				num++;
			}
		}
		MessageBox.Show($"解析与导入完成！\n成功导入: {num2} 只股票\n格式错误/忽略: {num}", "导入结果");
		AnalyticsService.Log("4", "9");
		Close();
	}
}
