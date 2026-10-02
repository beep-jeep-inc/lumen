using Pult.Core.Diagnostics;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

public class TileCaptionTests
{
    [Theory]
    [InlineData(1, "1 обновление доступно"), InlineData(2, "2 обновления доступны"), InlineData(5, "5 обновлений доступно"),
     InlineData(11, "11 обновлений доступно"), InlineData(14, "14 обновлений доступно"), InlineData(21, "21 обновление доступно"), InlineData(22, "22 обновления доступны")]
    public void Programs_Declension(int n, string expected) => Assert.Equal(expected, $"{n} {TileCaptions.Programs(n)}");

    [Theory]
    [InlineData(1, "1 драйвер можно поставить"), InlineData(3, "3 драйвера можно поставить"), InlineData(12, "12 драйверов можно поставить")]
    public void Drivers_Declension(int n, string expected) => Assert.Equal(expected, $"{n} {TileCaptions.Drivers(n)}");

    [Theory]
    [InlineData(1, "1 диск требует внимания"), InlineData(2, "2 диска требуют внимания"), InlineData(5, "5 дисков требуют внимания")]
    public void Disks_Declension(int n, string expected) => Assert.Equal(expected, $"{n} {TileCaptions.Disks(n)}");

    [Theory]
    [InlineData(1, "1 запись включена в автозагрузке"), InlineData(4, "4 записи включены в автозагрузке"), InlineData(18, "18 записей включено в автозагрузке")]
    public void Startup_Declension(int n, string expected) => Assert.Equal(expected, $"{n} {TileCaptions.Startup(n)}");

    [Fact]
    public void Count_JoinsNumberAndWord() => Assert.Equal("3 файла", RuText.Count(3, "файл", "файла", "файлов"));

    [Fact]
    public void Approx_And_Biggest_Format()
    {
        Assert.Equal("≈1,0 ГБ", TileCaptions.Approx(1L << 30));
        Assert.Equal("Больше всего: Users", TileCaptions.Biggest("Users"));
        Assert.Equal("и 14 скрыто", TileCaptions.Hidden(14));
        Assert.Equal("", TileCaptions.Hidden(0));
    }
}

public class ExceptionReportTests
{
    [Fact]
    public void Describe_IncludesContextTypeMessageStackAndInner()
    {
        Exception ex;
        try { try { throw new InvalidOperationException("внутри"); } catch (Exception inner) { throw new ArgumentException("снаружи", inner); } }
        catch (Exception e) { ex = e; }
        var text = ExceptionReport.Describe(ex, "раздел «Очистка», действие «Сканировать»");
        Assert.StartsWith("[раздел «Очистка», действие «Сканировать»] System.ArgumentException: снаружи", text);
        Assert.Contains("System.InvalidOperationException: внутри", text);
        Assert.Contains("внутреннее", text);
        Assert.Contains("Describe_IncludesContextTypeMessageStackAndInner", text);   // кадр стека теста
    }

    [Fact]
    public void Describe_Aggregate_ListsEveryBranch()
    {
        var ag = new AggregateException(new IOException("первое"), new TimeoutException("второе"));
        var text = ExceptionReport.Describe(ag);
        Assert.Contains("System.IO.IOException: первое", text);
        Assert.Contains("System.TimeoutException: второе", text);
    }

    [Fact]
    public void Describe_NoContext_NoBrackets() => Assert.StartsWith("System.Exception: x", ExceptionReport.Describe(new Exception("x")));
}
