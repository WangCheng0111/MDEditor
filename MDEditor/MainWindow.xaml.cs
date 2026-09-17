using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using Windows.Graphics;

namespace MDEditor
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();

            _captionButtons = new[] { MinimizeButton, MaximizeButton, CloseButton };

            _appWindow = this.AppWindow;
            _appWindow.SetIcon("Assets/Tiles/GalleryIcon.ico");
            InitializeNonClientInput();
            InitializeWindowSubclass();
            Activated += MainWindow_Activated;
            AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
            AppTitleBar.Loaded += AppTitleBar_Loaded;

            ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

            TitleBarTextBlock.Text = AppInfo.Current.DisplayInfo.DisplayName;

            CenterWindow();
        }

        private void CenterWindow()
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var width = (int)(workArea.Width * 0.60);
            var height = (int)(workArea.Height * 0.64);
            var winX = workArea.X + (workArea.Width - width) / 2;
            var winY = workArea.Y + (workArea.Height - height) / 2;
            AppWindow.MoveAndResize(new RectInt32(winX, winY, width, height));
        }
    }
}
