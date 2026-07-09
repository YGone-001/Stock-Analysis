namespace AIHelper.Services;

public interface IDialogService
{
	void ShowMessage(string message, string title);
	bool ShowConfirm(string message, string title);
	string ShowInput(string title, string defaultValue = "");
	
	void ShowChart(string url, string title);
	void ShowImage(string imageUrl);
	void ShowWenCai(string code, string name);
	void ShowLiveChart(string code, string name);
	void ShowPositionConfig(string code, string name);
	void ShowImportExport();
	void ShowProxySettings();
	void ShowSparrowScanner();
}
