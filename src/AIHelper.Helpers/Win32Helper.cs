using System;
using System.Runtime.InteropServices;

namespace AIHelper.Helpers;

public static class Win32Helper
{
	private const int SW_RESTORE = 9;

	private const int SW_SHOW = 5;

	[DllImport("user32.dll")]
	public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	public static bool FocusFolderWindow(string folderName)
	{
		IntPtr intPtr = FindWindow("CabinetWClass", folderName);
		if (intPtr != IntPtr.Zero)
		{
			ShowWindow(intPtr, 9);
			SetForegroundWindow(intPtr);
			return true;
		}
		return false;
	}
}
