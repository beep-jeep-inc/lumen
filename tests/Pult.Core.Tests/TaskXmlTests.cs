using System.Xml.Linq;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

public class TaskXmlTests
{
    [Fact]
    public void Build_HighestPrivileges_InteractiveUser_NoArguments()
    {
        var x = XDocument.Parse(TaskXml.Build(@"PC\User", @"C:\Program Files\Pult\PultAdmin.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("HighestAvailable", x.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", x.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(@"PC\User", x.Descendants(ns + "UserId").Single().Value);
        Assert.Equal("\"C:\\Program Files\\Pult\\PultAdmin.exe\"", x.Descendants(ns + "Command").Single().Value);
        Assert.Empty(x.Descendants(ns + "Arguments"));   // запрос — только из файла-данных
    }

    [Fact]
    public void Build_PriorityNormal_NotSchedulerDefault7()    // итоговое ревью, п.3: 7 = BELOW_NORMAL, наследуют winws и запущенные программы
    {
        var x = XDocument.Parse(TaskXml.Build(@"PC\User", @"C:\Program Files\Pult\PultAdmin.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("5", x.Descendants(ns + "Priority").Single().Value);
    }
}
