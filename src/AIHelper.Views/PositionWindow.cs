using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Models;
using HandyControl.Controls;

namespace AIHelper.Views;

public partial class PositionWindow : System.Windows.Window
{
	private string _stockCode;

	public PositionWindow(string stockCode, string stockName)
	{
		InitializeComponent();
		_stockCode = stockCode;
		TxtTitle.Text = "设置持仓 - " + stockName;
		HoldingInfo holding = HoldingsManager.GetHolding(stockCode);
		if (holding != null)
		{
			TxtVolume.Text = holding.Volume.ToString();
			TxtCostPrice.Text = holding.CostPrice.ToString("F3");
		}
		else
		{
			TxtVolume.Text = "0";
			TxtCostPrice.Text = "0.00";
		}
	}

	private void BtnSave_Click(object sender, RoutedEventArgs e)
	{
		if (int.TryParse(TxtVolume.Text, out var result) && double.TryParse(TxtCostPrice.Text, out var result2))
		{
			HoldingsManager.SetHolding(_stockCode, result2, result);
			Growl.Success("[" + _stockCode + "] 持仓配置已保存！");
			base.DialogResult = true;
			Close();
		}
		else
		{
			Growl.Warning("请输入正确的数字格式！");
		}
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

}
