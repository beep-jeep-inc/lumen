namespace Pult.Core.Tests;
public class SmokeTests
{
    [Fact] public void CoreAssemblyLoads() => Assert.NotNull(typeof(Pult.Core.Config.PultConfig).Assembly);
}
