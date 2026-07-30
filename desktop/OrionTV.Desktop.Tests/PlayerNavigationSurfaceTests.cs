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
    public void Sidebar_navigation_restores_each_section_and_the_player_repeatedly()
    {
        RunOnStaThread(() =>
        {
            var app = new OrionTV.Desktop.App.App();
            app.InitializeComponent();
            var window = new MainWindow();
            try
            {
                RemoveLoadedHandler(window, "MainWindow_Loaded");

                window.Navigate(AppPage.Live);
                var live = Assert.IsType<LiveView>(GetCurrentView(window));
                var liveController = new PlaybackController(window.Services.LibVlc, Dispatcher.CurrentDispatcher);
                typeof(LiveView)
                    .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(live, liveController);
                var generationField = typeof(PlaybackController)
                    .GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var generationBeforeLeaving = (int)generationField.GetValue(liveController)!;

                window.Navigate(AppPage.Home);
                Assert.Equal(generationBeforeLeaving + 1, generationField.GetValue(liveController));
                window.Navigate(AppPage.Live);
                Assert.Same(live, GetCurrentView(window));

                window.Navigate(AppPage.Home);
                var home = Assert.IsType<HomeView>(GetCurrentView(window));
                window.Navigate(AppPage.Detail);
                var homeDetail = Assert.IsType<DetailView>(GetCurrentView(window));

                window.Navigate(AppPage.Search);
                var search = Assert.IsType<SearchView>(GetCurrentView(window));
                window.Navigate(AppPage.Detail);
                var searchDetail = Assert.IsType<DetailView>(GetCurrentView(window));

                window.Navigate(AppPage.Home);
                Assert.Same(homeDetail, GetCurrentView(window));
                window.GoBack();
                Assert.Same(home, GetCurrentView(window));

                window.Navigate(AppPage.Search);
                Assert.Same(searchDetail, GetCurrentView(window));
                window.GoBack();
                Assert.Same(search, GetCurrentView(window));

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

                window.GoBack();
                Assert.Same(home, GetCurrentView(window));
            }
            finally
            {
                window.Close();
                PumpDispatcher();
                app.Shutdown();
            }
        });
    }

    private static UIElement? GetCurrentView(MainWindow window) =>
        (UIElement?)typeof(MainWindow)
            .GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window);

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF navigation test timed out.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
