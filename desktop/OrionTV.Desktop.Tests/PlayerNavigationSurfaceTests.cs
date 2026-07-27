using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using OrionTV.Desktop.App;
using OrionTV.Desktop.App.Views;

namespace OrionTV.Desktop.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfNavigationCollection
{
    public const string Name = "WPF navigation";
}

[Collection(WpfNavigationCollection.Name)]
public sealed class PlayerNavigationSurfaceTests
{
    [Fact]
    public void Sidebar_navigation_hides_and_restores_the_libvlc_foreground_window_repeatedly()
    {
        RunOnStaThread(() =>
        {
            var app = new OrionTV.Desktop.App.App();
            app.InitializeComponent();
            var window = new MainWindow();
            try
            {
                RemoveLoadedHandler(window, "MainWindow_Loaded");
                window.Navigate(AppPage.Player);

                var player = (PlayerView)typeof(MainWindow)
                    .GetField("_playerView", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                RemoveLoadedHandler(player, "OnLoaded");

                window.Show();
                PumpDispatcher();

                var foreground = app.Windows
                    .OfType<Window>()
                    .Single(candidate => candidate.Title == "LibVLCSharp.WPF");
                Assert.True(foreground.IsVisible);

                for (var iteration = 0; iteration < 2; iteration++)
                {
                    window.Navigate(AppPage.Home);
                    PumpDispatcher();
                    Assert.False(foreground.IsVisible);

                    window.Navigate(AppPage.Player);
                    PumpDispatcher();
                    Assert.True(foreground.IsVisible);
                }
            }
            finally
            {
                window.Close();
                PumpDispatcher();
                app.Shutdown();
            }
        });
    }

    private static void RemoveLoadedHandler(FrameworkElement element, string methodName)
    {
        var method = element.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var handler = method.CreateDelegate<RoutedEventHandler>(element);
        element.Loaded -= handler;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF navigation test timed out.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
