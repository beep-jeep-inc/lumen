using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pult.App.Views;

public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Отступ слева у вложенных строк списка.
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => new Thickness(v is double d ? d : 0, 0, 0, 0);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Подпись раздела: ЗАГЛАВНЫМИ и с разрядкой (в WPF у TextBlock нет межбуквенного интервала — вставляем тонкие пробелы).
public sealed class SpacedCapsConverter : IValueConverter
{
    public static string Spaced(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        foreach (var ch in s.ToUpper(CultureInfo.GetCultureInfo("ru-RU")))
        {
            sb.Append(ch);
            if (ch != ' ') sb.Append(' ');   // узкий неразрывный пробел: разрядка без переноса посреди слова
        }
        return sb.ToString().TrimEnd();
    }
    public object Convert(object v, Type t, object p, CultureInfo c) => v is string s ? Spaced(s) : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// «Выбран ли пункт меню»: "1" — да (для триггера стиля NavButton).
public sealed class BoolToOneConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? "1" : "0";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Компактный режим окна (клиентская область уже 1100 или ниже 720 DIP): наследуется всеми элементами окна, стили смотрят на него триггерами.
public static class Layout
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(Layout), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsCompact(DependencyObject o) => (bool)o.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject o, bool v) => o.SetValue(IsCompactProperty, v);
}

/// Первая строка текста (до перевода строки): главная подпись плитки.
public sealed class FirstLineConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) { var s = v as string ?? ""; var i = s.IndexOf('\n'); return i < 0 ? s : s[..i].TrimEnd('\r'); }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Остальные строки текста после первой (пояснение плитки); пусто, если строка одна.
public sealed class RestLinesConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) { var s = v as string ?? ""; var i = s.IndexOf('\n'); return i < 0 ? "" : s[(i + 1)..].Trim(); }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Число × параметр (доля ширины для правой части строки списка).
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v is double d && double.TryParse(p as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? Math.Max(0, d * k) : double.PositiveInfinity;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}


/// Поля плитки «Обзора» в сетке с равными плитками и промежутком g (16, в компактном режиме 12): параметр «n,i,v» —
/// плиток в ряду, номер плитки, есть ли промежуток снизу (1/0). Значение — Layout.IsCompact.
public sealed class TileGapConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var g = v is true ? 12.0 : 16.0;
        var a = ((p as string) ?? "1,0,0").Split(',');
        int n = int.Parse(a[0], CultureInfo.InvariantCulture), i = int.Parse(a[1], CultureInfo.InvariantCulture); var bottom = a[2] == "1" ? g : 0;
        return new Thickness(i * g / n, 0, (n - 1 - i) * g / n, bottom);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// Короткий (до 3 знаков) ли текст: значение плитки, рядом с которым подпись помещается справа.
public sealed class IsShortConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is string s && s.Length <= 3;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
