using System;
using System.Threading.Tasks;
using System.Diagnostics;

namespace AIHelper.Helpers
{
	public static class TaskExtensions
	{
		/// <summary>
		/// Safely executes a Task without awaiting it, catching any exceptions to prevent application crashes.
		/// </summary>
		public static async void SafeFireAndForget(this Task task, Action<Exception>? onException = null)
		{
			try
			{
				await task.ConfigureAwait(false);
			}
			catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
				if (onException != null)
				{
					onException(ex);
				}
				else
				{
					Trace.WriteLine($"Unobserved exception in SafeFireAndForget Task: {ex}");
				}
			}
		}
	}
}
