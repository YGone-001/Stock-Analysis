#nullable enable
using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace AIHelper;

public class App : Application
{
	private bool _contentLoaded;

	protected override void OnStartup(StartupEventArgs e)
	{
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Information()
			.WriteTo.File(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "aihelper-.log"), 
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: 30,
				fileSizeLimitBytes: 10_000_000,
				outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
			.CreateLogger();

		Log.Information("应用程序启动");

		base.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs args)
		{
			Log.Fatal(args.Exception, "[FATAL UI EXCEPTION] UI线程发生未捕获异常");
			MessageBoxResult result = MessageBox.Show("UI线程发生未捕获异常，继续运行可能会导致程序处于不稳定状态。是否要继续尝试运行？\n\n【错误信息】: " + args.Exception.Message, "赛博警报 (UI)", MessageBoxButton.YesNo, MessageBoxImage.Error);
			if (result == MessageBoxResult.Yes)
			{
				args.Handled = true;
			}
		};
		AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
		{
			Exception? ex = args.ExceptionObject as Exception;
			Log.Fatal(ex, "[FATAL BACKGROUND EXCEPTION] 后台线程发生致命崩溃");
			MessageBox.Show("后台线程发生致命崩溃！\n\n【错误信息】: " + ex?.Message + "\n\n【堆栈跟踪】:\n" + ex?.StackTrace, "\ud83d\udea8 赛博警报 (后台)", MessageBoxButton.OK, MessageBoxImage.Hand);
		};
		TaskScheduler.UnobservedTaskException += delegate(object? sender, UnobservedTaskExceptionEventArgs args)
		{
			Log.Fatal(args.Exception, "[FATAL TASK EXCEPTION] 异步任务发生致命崩溃");
			MessageBox.Show("异步任务发生致命崩溃！\n\n【错误信息】: " + args.Exception.InnerException?.Message, "\ud83d\udea8 赛博警报 (Task)", MessageBoxButton.OK, MessageBoxImage.Hand);
			args.SetObserved();
		};
		base.OnStartup(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		Log.Information("应用程序退出");
		Log.CloseAndFlush();
		base.OnExit(e);
	}

	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
			Uri resourceLocator = new Uri("/AIHelper;component/app.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[STAThread]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
