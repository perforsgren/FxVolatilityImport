using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using FxVolatilityImport.Views;
using FxVolatilityImport.ViewModels;
using Application = System.Windows.Application;

namespace FxVolatilityImport
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;
        private WidgetWindow? _widgetWindow;
        private MainWindow? _mainWindow;
        private MainViewModel? _viewModel;

        public ImageSource? AppIcon { get; private set; }
        public Icon? AppIconWinForms { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _viewModel = new MainViewModel();

            // Ladda ikon från app.ico – väljer bästa ram för varje användningsfall
            AppIcon = LoadBestIconFrame(preferredSize: 32);
            AppIconWinForms = LoadSystemDrawingIcon();

            _trayIcon = new TaskbarIcon
            {
                Icon = AppIconWinForms,
                ToolTipText = "FX Volatility Import"
            };
            _trayIcon.TrayMouseDoubleClick += TrayIcon_TrayMouseDoubleClick;
            _trayIcon.ContextMenu = CreateTrayContextMenu();

            _mainWindow = new MainWindow { DataContext = _viewModel, Icon = AppIcon };
            _mainWindow.Show();
        }

        /// <summary>
        /// Läser app.ico och väljer den ram som är närmast önskad storlek.
        /// Undviker att WPF automatiskt väljer minsta ramen och skalar upp den.
        /// </summary>
        private static BitmapSource LoadBestIconFrame(int preferredSize = 32)
        {
            var decoder = new IconBitmapDecoder(
                new Uri("pack://application:,,,/FxVolatilityImport;component/app.ico"),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            return decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - preferredSize))
                .First();
        }

        /// <summary>
        /// Laddar app.ico som System.Drawing.Icon för tray-ikonen.
        /// Windows väljer automatiskt rätt ram baserat på DPI/kontext.
        /// </summary>
        private static Icon LoadSystemDrawingIcon()
        {
            var streamInfo = Application.GetResourceStream(
                new Uri("pack://application:,,,/FxVolatilityImport;component/app.ico"));

            var ms = new MemoryStream();
            streamInfo!.Stream.CopyTo(ms);
            ms.Position = 0;
            return new Icon(ms);
        }

        private ContextMenu CreateTrayContextMenu()
        {
            var menu = new ContextMenu();

            var showMainItem = new MenuItem { Header = "Show Main Window" };
            showMainItem.Click += (s, e) => ShowMainWindow();

            var showWidgetItem = new MenuItem { Header = "Show Widget" };
            showWidgetItem.Click += (s, e) => ShowWidget();

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => ExitApplication();

            menu.Items.Add(showMainItem);
            menu.Items.Add(showWidgetItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            return menu;
        }

        public void ShowMainWindow()
        {
            _widgetWindow?.Hide();
            _mainWindow?.Show();
            _mainWindow?.Activate();
            if (_mainWindow != null)
                _mainWindow.WindowState = WindowState.Normal;
        }

        public void ShowWidget()
        {
            _mainWindow?.Hide();

            if (_widgetWindow == null || !_widgetWindow.IsLoaded)
            {
                _widgetWindow = new WidgetWindow(_viewModel!) { Icon = AppIcon };
                _widgetWindow.WidgetClicked += Widget_Clicked;
            }

            _widgetWindow.Show();
            _widgetWindow.Activate();
        }

        private void Widget_Clicked(object? sender, EventArgs e)
        {
            ShowMainWindow();
        }

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
            => ShowMainWindow();

        public void ExitApplication()
        {
            _viewModel?.Dispose();
            _trayIcon?.Dispose();
            Shutdown();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            _trayIcon?.Dispose();
        }
    }
}
