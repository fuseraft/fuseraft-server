using fuseraft.Web.Services;

namespace FuseraftWeb.Tests;

public sealed class LineDiffTests
{
    private static string Render(IEnumerable<DiffLine> lines) =>
        string.Join("|", lines.Select(l => l.Kind switch
        {
            DiffKind.Added   => "+" + l.Text,
            DiffKind.Removed => "-" + l.Text,
            DiffKind.Skipped => "~" + l.Text,
            _                => " " + l.Text,
        }));

    [Fact]
    public void NewFile_IsAllAdded()
    {
        Assert.Equal("+a|+b", Render(LineDiff.Compute(null, "a\nb\n")));
    }

    [Fact]
    public void ChangedLine_IsRemovedThenAdded()
    {
        Assert.Equal(" a|-b|+B| c", Render(LineDiff.Compute("a\nb\nc", "a\nB\nc")));
    }

    [Fact]
    public void LongUnchangedRuns_AreFolded()
    {
        var old = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"l{i}"));
        var @new = old.Replace("l10", "X");

        var lines = LineDiff.Compute(old, @new, context: 1);

        Assert.Equal("~8 unchanged lines| l9|-l10|+X| l11|~9 unchanged lines", Render(lines));
    }

    [Fact]
    public void LineEndings_DontCountAsChanges()
    {
        Assert.Equal("~2 unchanged lines", Render(LineDiff.Compute("a\r\nb\r\n", "a\nb\n", context: 0)));
    }

    [Fact]
    public void HugeInputs_FallBackWithoutTheTable()
    {
        var big = string.Join('\n', Enumerable.Range(0, 2100).Select(i => $"x{i}"));

        var lines = LineDiff.Compute(big, big + "\ny");

        Assert.Equal(4201, lines.Count);
        Assert.Equal(DiffKind.Removed, lines[0].Kind);
        Assert.Equal(DiffKind.Added, lines[^1].Kind);
    }
}
