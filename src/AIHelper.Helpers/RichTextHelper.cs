using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIHelper.Helpers;

public static class RichTextHelper
{
	public static readonly DependencyProperty StockClickCommandProperty = DependencyProperty.RegisterAttached("StockClickCommand", typeof(ICommand), typeof(RichTextHelper), new PropertyMetadata(null));

	public static readonly DependencyProperty ChatTextProperty = DependencyProperty.RegisterAttached("ChatText", typeof(string), typeof(RichTextHelper), new PropertyMetadata(null, OnChatTextChanged));

	public static ICommand GetStockClickCommand(DependencyObject obj)
	{
		return (ICommand)obj.GetValue(StockClickCommandProperty);
	}

	public static void SetStockClickCommand(DependencyObject obj, ICommand value)
	{
		obj.SetValue(StockClickCommandProperty, value);
	}

	public static string GetChatText(DependencyObject obj)
	{
		return (string)obj.GetValue(ChatTextProperty);
	}

	public static void SetChatText(DependencyObject obj, string value)
	{
		obj.SetValue(ChatTextProperty, value);
	}

	private static void OnChatTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (!(d is TextBlock textBlock))
		{
			return;
		}
		textBlock.Inlines.Clear();
		string text = e.NewValue as string;
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		MatchCollection matchCollection = EmojiRegex.Matches(text);
		int num = 0;
		foreach (Match item in matchCollection)
		{
			if (item.Index > num)
			{
				ParseStocks(textBlock, text.Substring(num, item.Index - num));
			}
			string value = item.Groups[1].Value;
			try
			{
				Image childUIElement = new Image
				{
					Source = new BitmapImage(new Uri("pack://application:,,,/Assets/emoji/" + value + ".png")),
					Width = 24.0,
					Height = 24.0,
					Margin = new Thickness(2.0, -4.0, 2.0, -4.0),
					VerticalAlignment = VerticalAlignment.Center
				};
				textBlock.Inlines.Add(new InlineUIContainer(childUIElement)
				{
					BaselineAlignment = BaselineAlignment.Center
				});
			}
			catch
			{
				textBlock.Inlines.Add(new Run(item.Value));
			}
			num = item.Index + item.Length;
		}
		if (num < text.Length)
		{
			ParseStocks(textBlock, text.Substring(num));
		}
	}

	private static readonly Regex EmojiRegex = new Regex("\\[emoji:(\\d{2})\\]", RegexOptions.Compiled);
	private static readonly Regex StockCodeRegex = new Regex("\\b(60\\d{4}|00\\d{4}|30\\d{4}|43\\d{4}|83\\d{4}|87\\d{4}|688\\d{3})\\b", RegexOptions.Compiled);

	private static void ParseStocks(TextBlock textBlock, string text)
	{
		TextBlock textBlock2 = textBlock;
		MatchCollection matchCollection = StockCodeRegex.Matches(text);
		int num = 0;
		foreach (Match match in matchCollection)
		{
			if (match.Index > num)
			{
				textBlock2.Inlines.Add(new Run(text.Substring(num, match.Index - num)));
			}
			Hyperlink hyperlink = new Hyperlink(new Run(match.Value)
			{
				FontWeight = FontWeights.Bold
			})
			{
				Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB")),
				TextDecorations = null,
				Cursor = Cursors.Hand,
				ToolTip = "点击添加至左侧 [聊天室] 自选组"
			};
			hyperlink.Click += delegate
			{
				ICommand stockClickCommand = GetStockClickCommand(textBlock2);
				if (stockClickCommand != null && stockClickCommand.CanExecute(match.Value))
				{
					stockClickCommand.Execute(match.Value);
				}
			};
			textBlock2.Inlines.Add(hyperlink);
			num = match.Index + match.Length;
		}
		if (num < text.Length)
		{
			textBlock2.Inlines.Add(new Run(text.Substring(num)));
		}
	}
}
