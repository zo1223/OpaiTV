using System.Configuration;
using System.Data;
using System.Windows;

namespace OrionTV.Desktop.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 免安装版原地更新：旧程序退出前会用这个参数启动“临时目录里的新版本”，
        // 由它把新文件覆盖回原目录并重新启动，然后直接退出（不创建主窗口）。
        if (PortableUpdateApplier.TryParseArguments(e.Args, out var request))
        {
            try
            {
                PortableUpdateApplier.Run(request);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"原地更新没有完成：{exception.Message}\n\n请关闭所有 OpaiTV 窗口后重新打开并再次更新；如果程序无法启动，请重新下载免安装包解压覆盖到：\n{request.TargetDirectory}",
                    "OpaiTV 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Environment.Exit(0);
            return;
        }

        _ = Task.Run(() => DesktopUpdateService.CleanUpPortableStaging());
        base.OnStartup(e);
    }
}

