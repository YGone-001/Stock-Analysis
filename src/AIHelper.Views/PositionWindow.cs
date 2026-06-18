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

public class PositionWindow : System.Windows.Window, IComponentConnector
{
	private string _stockCode;

	internal TextBlock TxtTitle;

	internal System.Windows.Controls.TextBox TxtVolume;

	internal System.Windows.Controls.TextBox TxtCostPrice;

	private bool _contentLoaded;

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

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/positionwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			TxtTitle = (TextBlock)target;
			break;
		case 2:
			TxtVolume = (System.Windows.Controls.TextBox)target;
			break;
		case 3:
			TxtCostPrice = (System.Windows.Controls.TextBox)target;
			break;
		case 4:
			((Button)target).Click += BtnSave_Click;
			break;
		case 5:
			((Button)target).Click += BtnCancel_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
