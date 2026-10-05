using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MDEditor.Typesetting.Hyphenation;

/// <summary>Liang maximum-weight pattern trie. Higher even weights suppress lower odd weights.</summary>
public sealed class LiangHyphenator
{
    private sealed class Node
    {
        public readonly Dictionary<char, Node> Children = new();
        public byte[]? Weights;
    }
    private readonly Node _root = new();
    private readonly Dictionary<string, ImmutableArray<int>> _exceptions = new(StringComparer.Ordinal);
    private static readonly Lazy<LiangHyphenator> _english = new(LoadEnglish);
    public static LiangHyphenator EnglishUs => _english.Value;
    public int PatternCount { get; }
    public int ExceptionCount => _exceptions.Count;

    public LiangHyphenator(string patterns, string exceptions = "")
    {
        ArgumentNullException.ThrowIfNull(patterns); ArgumentNullException.ThrowIfNull(exceptions);
        foreach (var token in patterns.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = new StringBuilder(); var weights = new List<byte> { 0 }; var digit = false;
            foreach (var c in token)
            {
                if (c is >= '0' and <= '9')
                {
                    if (digit) throw new ArgumentException("Adjacent digits in a pattern.", nameof(patterns));
                    weights[^1] = (byte)(c - '0'); digit = true;
                }
                else if (c is >= 'a' and <= 'z' or '.')
                { letters.Append(c); weights.Add(0); digit = false; }
                else throw new ArgumentException("Patterns must contain lowercase ASCII letters, dots and digits.", nameof(patterns));
            }
            if (letters.Length == 0) throw new ArgumentException("Empty pattern.", nameof(patterns));
            var node = _root;
            foreach (var c in letters.ToString())
            {
                if (!node.Children.TryGetValue(c, out var next)) node.Children[c] = next = new();
                node = next;
            }
            if (node.Weights is null) node.Weights = weights.ToArray();
            else for (var i = 0; i < weights.Count; i++) node.Weights[i] = Math.Max(node.Weights[i], weights[i]);
            PatternCount++;
        }
        foreach (var token in exceptions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new StringBuilder(); var points = new List<int>();
            foreach (var c in token)
            {
                if (c == '-')
                {
                    if (word.Length == 0 || points.LastOrDefault(-1) == word.Length)
                        throw new ArgumentException("Invalid exception.", nameof(exceptions));
                    points.Add(word.Length);
                }
                else if (c is >= 'a' and <= 'z') word.Append(c);
                else throw new ArgumentException("Invalid exception.", nameof(exceptions));
            }
            if (word.Length == 0 || points.LastOrDefault(-1) == word.Length) throw new ArgumentException("Invalid exception.", nameof(exceptions));
            _exceptions[word.ToString()] = points.ToImmutableArray();
        }
    }

    public ImmutableArray<int> GetBreakOffsets(string word, int leftMinimum = 2, int rightMinimum = 3)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (leftMinimum < 1 || rightMinimum < 1 || leftMinimum > 128 || rightMinimum > 128)
            throw new ArgumentOutOfRangeException(nameof(leftMinimum));
        // ASCII policy deliberately does not transliterate accents, ligature code points or other languages.
        if (word.Length > 128 || word.Length < leftMinimum + rightMinimum ||
            word.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z'))) return [];
        word = word.ToLowerInvariant();
        if (_exceptions.TryGetValue(word, out var exception))
            return exception.Where(i => i >= leftMinimum && i <= word.Length - rightMinimum).ToImmutableArray();
        var padded = "." + word + "."; var scores = new byte[padded.Length + 1];
        for (var start = 0; start < padded.Length; start++)
        {
            var node = _root;
            for (var end = start; end < padded.Length && node.Children.TryGetValue(padded[end], out node!); end++)
                if (node.Weights is { } values)
                    for (var i = 0; i < values.Length; i++) scores[start + i] = Math.Max(scores[start + i], values[i]);
        }
        return Enumerable.Range(leftMinimum, word.Length - leftMinimum - rightMinimum + 1)
            .Where(i => (scores[i + 1] & 1) != 0).ToImmutableArray();
    }

    private static LiangHyphenator LoadEnglish()
    {
        using var stream = typeof(LiangHyphenator).Assembly.GetManifestResourceStream("MDEditor.Hyphenation.en-US")
            ?? throw new InvalidOperationException("Missing offline en-US patterns.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes.ToArray())) != "F4FFCD96C5CBC886BDAD23F95DCAE8EDC3CD3620EAE62F7946ECEDA97C4E68F8")
            throw new InvalidOperationException("The pinned en-US resource hash has changed; review data and license before updating it.");
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8);
        var tex = Regex.Replace(reader.ReadToEnd(), @"%[^\r\n]*", "");
        var patterns = Regex.Match(tex, @"\\patterns\s*\{([^}]*)\}", RegexOptions.Singleline);
        var exceptions = Regex.Match(tex, @"\\hyphenation\s*\{([^}]*)\}", RegexOptions.Singleline);
        if (!patterns.Success || !exceptions.Success) throw new InvalidOperationException("Malformed embedded pattern resource.");
        // Correct the upstream documented 'democrat' issue explicitly, without altering the licensed data.
        return new(patterns.Groups[1].Value, exceptions.Groups[1].Value + " dem-o-crat");
    }
}
