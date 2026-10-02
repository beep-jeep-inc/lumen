using Pult.Core.Config;
using Pult.Core.Monitoring;

namespace Pult.Core.Tests;

/// Регрессии по отчёту QA 452341: config.json с null, занятый файл, снимок процессов.
public class QaFixTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-qa-" + Guid.NewGuid().ToString("N"));
    public QaFixTests() => Directory.CreateDirectory(_d);
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    [Theory]
    [InlineData("{\"NoUpdate\":null}")]
    [InlineData("{\"NoUpdate\":[null]}")]
    [InlineData("{\"DevRoots\":null}")]
    [InlineData("{\"DevRoots\":[null,\"\"]}")]
    [InlineData("{\"Programs\":null,\"Profiles\":[null],\"Helper\":null}")]      // старые поля игнорируются
    public void Load_NullsInConfig_NormalizedNoCrash(string json)
    {
        var path = Path.Combine(_d, "config.json"); File.WriteAllText(path, json);
        var c = new ConfigStore(path).Load();
        Assert.NotNull(c.NoUpdate); Assert.NotNull(c.DevRoots);
        Assert.All(c.NoUpdate, x => Assert.NotNull(x));
        Assert.All(c.DevRoots, x => Assert.False(string.IsNullOrWhiteSpace(x)));
    }

    [Fact]
    public void Load_FileTemporarilyLocked_RetriesAndReads()
    {
        var path = Path.Combine(_d, "config.json");
        File.WriteAllText(path, "{\"Language\":\"en\"}");
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(200); fs.Dispose(); });
        Assert.Equal("en", new ConfigStore(path).Load().Language);
    }

    [Fact]
    public void ProcessSnapshot_FindsOwnProcess_AndIgnoresCase()
    {
        var q = new SystemProcessQuery();
        q.Snapshot();
        var me = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.True(q.IsRunning(me)); Assert.True(q.IsRunning(me.ToUpperInvariant())); Assert.True(q.IsRunning(me + ".exe"));
        Assert.False(q.IsRunning("no-such-process-" + Guid.NewGuid().ToString("N")));
    }
}
