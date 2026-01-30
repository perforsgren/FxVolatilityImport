using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using FxVolatilityImport.Views;
using FxVolatilityImport.ViewModels;
using Application = System.Windows.Application;
using FlowDirection = System.Windows.FlowDirection;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

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
            
            // Skapa ikoner
            AppIconWinForms = CreateAppIcon();
            AppIcon = CreateAppIconImageSource();
            
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

        private static ImageSource CreateAppIconImageSource()
        {
            int size = 32;
            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                var background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8));
                
                context.DrawRoundedRectangle(background, null,
                    new System.Windows.Rect(0, 0, size, size), 6, 6);

                var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

                var formattedText = new FormattedText("V",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    20,
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A)),
                    96);

                var textX = (size - formattedText.Width) / 2;
                var textY = (size - formattedText.Height) / 2;
                context.DrawText(formattedText, new System.Windows.Point(textX, textY));
            }

            var renderTarget = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(visual);
            renderTarget.Freeze();
            return renderTarget;
        }

        private static Icon CreateAppIcon()
        {
            int size = 32;
            
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            
            var backgroundColor = System.Drawing.Color.FromArgb(0x38, 0xBD, 0xF8);
            using var backgroundBrush = new SolidBrush(backgroundColor);
            
            var rect = new Rectangle(0, 0, size - 1, size - 1);
            int radius = 6;
            using var path = CreateRoundedRectangle(rect, radius);
            g.FillPath(backgroundBrush, path);
            
            var textColor = System.Drawing.Color.FromArgb(0x0F, 0x17, 0x2A);
            using var textBrush = new SolidBrush(textColor);
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold);
            
            var textSize = g.MeasureString("V", font);
            var textX = (size - textSize.Width) / 2;
            var textY = (size - textSize.Height) / 2;
            g.DrawString("V", font, textBrush, textX, textY);
            
            var hIcon = bitmap.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            
            return path;
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
