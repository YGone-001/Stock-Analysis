using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AIHelper;

public class App : Application
{
	private bool _contentLoaded;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs args)
		{
			MessageBox.Show("UI线程发生致命崩溃！\n\n【错误信息】: " + args.Exception.Message + "\n\n【堆栈跟踪】:\n" + args.Exception.StackTrace, "\ud83d\udea8 赛博警报 (UI)", MessageBoxButton.OK, MessageBoxImage.Hand);
			args.Handled = true;
		};
		AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
		{
			Exception ex = args.ExceptionObject as Exception;
			MessageBox.Show("后台线程发生致命崩溃！\n\n【错误信息】: " + ex?.Message + "\n\n【堆栈跟踪】:\n" + ex?.StackTrace, "\ud83d\udea8 赛博警报 (后台)", MessageBoxButton.OK, MessageBoxImage.Hand);
		};
		TaskScheduler.UnobservedTaskException += delegate(object? sender, UnobservedTaskExceptionEventArgs args)
		{
			MessageBox.Show("异步任务发生致命崩溃！\n\n【错误信息】: " + args.Exception.InnerException?.Message, "\ud83d\udea8 赛博警报 (Task)", MessageBoxButton.OK, MessageBoxImage.Hand);
			args.SetObserved();
		};
		base.OnStartup(e);
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
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
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
