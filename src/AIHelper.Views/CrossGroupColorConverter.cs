using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using AIHelper.Models;

#pragma warning disable CS8600
namespace AIHelper.Views;

public class CrossGroupColorConverter : IMultiValueConverter
{
	private static readonly SolidColorBrush DefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"));
	private static readonly SolidColorBrush RedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
	private static readonly SolidColorBrush GoldBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DAA520"));
	private static readonly SolidColorBrush BlueBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));

	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if (values.Length < 2 || values[0] == null || values[1] == null)
		{
			return DefaultBrush;
		}
		string pureCode = values[0].ToString();
		if (!(values[1] is IEnumerable<StockGroupModel> enumerable) || string.IsNullOrEmpty(pureCode))
		{
			return DefaultBrush;
		}
		int num = 0;
		bool flag = false;
		bool flag2 = false;
		foreach (StockGroupModel item in enumerable)
		{
			if (!item.IsOverview && item.Stocks != null && item.Stocks.Any((StockModel s) => s.PureCode == pureCode))
			{
				num++;
				if (item.Header.Contains("选股"))
				{
					flag = true;
				}
				if (item.Header.Contains("持仓"))
				{
					flag2 = true;
				}
			}
		}
		if (num >= 2)
		{
			if (flag2)
			{
				return RedBrush;
			}
			if (flag)
			{
				return GoldBrush;
			}
			return BlueBrush;
		}
		return DefaultBrush;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
