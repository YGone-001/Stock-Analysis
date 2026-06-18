using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using AIHelper.Models;

namespace AIHelper.Views;

public class CrossGroupColorConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		SolidColorBrush result = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"));
		if (values.Length < 2 || values[0] == null || values[1] == null)
		{
			return result;
		}
		string pureCode = values[0].ToString();
		if (!(values[1] is IEnumerable<StockGroupModel> enumerable) || string.IsNullOrEmpty(pureCode))
		{
			return result;
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
				return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
			}
			if (flag)
			{
				return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DAA520"));
			}
			return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
		}
		return result;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
