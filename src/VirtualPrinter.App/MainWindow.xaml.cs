using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VirtualPrinter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshLog();
    }

    private void RefreshLog()
    {
        try
        {
            var logPath = Logger.LogFilePath;
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                LogBox.Text = File.ReadAllText(logPath);
                LogBox.ScrollToEnd();
            }
            else
            {
                LogBox.Text = "(まだログがありません。印刷を実行してください。)";
            }
        }
        catch (Exception ex)
        {
            LogBox.Text = $"ログ読込エラー: {ex.Message}";
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = App.OutputRoot;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
