namespace fuseraft.Server.Services;

public enum DiffKind { Same, Added, Removed, Skipped }

/// <summary>One line of a diff; for <see cref="DiffKind.Skipped"/>, <c>Text</c> says how many unchanged lines were folded.</summary>
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>A line diff for showing a pending file write, with long unchanged stretches folded.</summary>
public static class LineDiff
{
    // LCS needs old×new cells; past this, list the whole new file as added instead.
    internal const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Compute(string? oldText, string? newText, int context = 3)
    {
        var a = Split(oldText);
        var b = Split(newText);
        if ((long)a.Length * b.Length > MaxCells)
            return [.. a.Select(l => new DiffLine(DiffKind.Removed, l)), .. b.Select(l => new DiffLine(DiffKind.Added, l))];

        // lcs[i,j] = length of the longest common subsequence of a[i..] and b[j..].
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var lines = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { lines.Add(new(DiffKind.Same, a[x])); x++; y++; }
            // On a tie, removals go first so a changed line reads "- old" then "+ new".
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) lines.Add(new(DiffKind.Removed, a[x++]));
            else lines.Add(new(DiffKind.Added, b[y++]));
        }
        return Fold(lines, context);
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private static List<DiffLine> Fold(List<DiffLine> lines, int context)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same) continue;
            for (var k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++) keep[k] = true;
        }

        var folded = new List<DiffLine>();
        for (var i = 0; i < lines.Count;)
        {
            if (keep[i]) { folded.Add(lines[i++]); continue; }
            var start = i;
            while (i < lines.Count && !keep[i]) i++;
            var count = i - start;
            folded.Add(new(DiffKind.Skipped, $"{count} unchanged line{(count == 1 ? "" : "s")}"));
        }
        return folded;
    }
}
