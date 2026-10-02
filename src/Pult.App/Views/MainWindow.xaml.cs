using System.Windows;

namespace Pult.App.Views;

/// Окно Пульта: одна страница «Обслуживание». Закрытие окна = выход из программы.
public partial class MainWindow : Window
{
    private readonly ViewModels.MaintenanceVm _vm;

    public MainWindow()
    {
        InitializeComponent();
        // Окно не выше рабочей области экрана (иначе низ уходит под панель задач)
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 20); Height = Math.Min(Height, wa.Height - 20);
        MinHeight = Math.Min(MinHeight, Height); MinWidth = Math.Min(MinWidth, Width);
        Theme.Backdrop.Apply(this);
        // Компактный режим: клиентская область уже 1100 или ниже 720 DIP (боковая панель 204, поля и шрифты меньше)
        Root.SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width < 1100 || e.NewSize.Height < 720);
        _vm = new ViewModels.MaintenanceVm();
        DataContext = _vm;
        PageHost.Content = new MaintenancePage { DataContext = _vm };
        if (!string.IsNullOrEmpty(App.StartupBanner)) _vm.ShowBanner(App.StartupBanner);
        // Крестик = выход: если в разделе идёт работа, спрашиваем (иначе «Переустановить» может остаться на полпути — программа удалена, а не поставлена).
        Closing += (_, e) => { if (!App.Preview && _vm.AnyBusy && !ConfirmDialog.Ask(string.Format(Strings.CloseWhileBusy, _vm.BusyTitle))) e.Cancel = true; };
        Loaded += (_, _) => _ = _vm.OpenedAsync();                       // дешёвое чтение при открытии; поиск и сканы — только по кнопке
    }

    private void ApplyCompact(bool compact)
    {
        if (Layout.GetIsCompact(this) == compact) return;
        Layout.SetIsCompact(this, compact);
        NavColumn.Width = new GridLength(compact ? 204 : 236);
        PageHost.Margin = compact ? new Thickness(20, 16, 20, 12) : new Thickness(24, 20, 24, 24);
    }

    public ViewModels.MaintenanceVm Maintenance => _vm;
    internal DependencyObject PageElement() => PageHost;
}
