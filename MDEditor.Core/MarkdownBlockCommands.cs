using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownBlockCommand { Quote, BulletList, OrderedList, TaskList, ToggleTask }

/// <summary>Source-only structural edits. Every command is one undoable replacement.</summary>
public static class MarkdownBlockCommands
{
    public static MarkdownSourceEdit? Enter(SourceTextSnapshot source, SourceRange selection)
    {
        var blocks = MarkdownBlockStructure.Create(source);
        if (!source.FullRange.Contains(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        if (selection.Length != 0) return null;
        var block = blocks.At(selection.Start);
        if (block.Kind == MarkdownBlockKind.None || selection.Start < block.Prefix.End) return null;
        var content = source.GetText(block.Content);
        if (string.IsNullOrWhiteSpace(content))
        {
            var keep = block.Kind == MarkdownBlockKind.Quote
                ? QuoteWithoutLastLevel(source.Text, block.QuotePrefix)
                : source.GetText(block.QuotePrefix);
            return new(block.Prefix, keep, new(block.Prefix.Start + keep.Length, 0));
        }
        var marker = block.Kind switch
        {
            MarkdownBlockKind.BulletList => block.Bullet.ToString() + " ",
            MarkdownBlockKind.OrderedList =>
                (block.OrderedNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                block.OrderedDelimiter + " ",
            MarkdownBlockKind.TaskList => (block.ListPrefix.Length == 0 ? "- " :
                source.GetText(block.ListPrefix).TrimStart(' ')) + "[ ] ",
            _ => ""
        };
        var continuation = source.GetText(block.QuotePrefix) +
            (block.ListDepth > 0 ? new string(' ', block.ListIndent) + marker : "");
        var inserted = LineEnding(blocks.Lines, block.LineIndex) + continuation;
        if (block.Kind == MarkdownBlockKind.OrderedList)
        {
            var renumbered = RenumberFollowingItems(source, blocks, block, selection.Start, inserted);
            if (renumbered is not null) return renumbered;
        }
        return new(new(selection.Start, 0), inserted, new(selection.Start + inserted.Length, 0));
    }

    public static MarkdownSourceEdit? Backspace(SourceTextSnapshot source, int caret)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (caret < 0 || caret > source.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        if (caret == 0 || source.Text[caret - 1] is not
            (' ' or '\t' or '>' or '-' or '+' or '*' or '.' or ')' or ']')) return null;
        var blocks = MarkdownBlockStructure.Create(source);
        var block = blocks.At(caret);
        if (block.Kind == MarkdownBlockKind.None || caret != block.Prefix.End) return null;
        if (block.Kind == MarkdownBlockKind.TaskList)
            return new(block.TaskMarker, "", new(block.TaskMarker.Start, 0));
        if (block.ListDepth > 0)
        {
            if (block.ListIndent >= 2)
                return new(new(block.QuotePrefix.End, 2), "",
                    new(caret - 2, 0));
            return new(new(block.ListPrefix.Start, block.Prefix.End - block.ListPrefix.Start), "",
                new(block.ListPrefix.Start, 0));
        }
        var shorter = QuoteWithoutLastLevel(source.Text, block.QuotePrefix);
        return new(block.QuotePrefix, shorter, new(block.QuotePrefix.Start + shorter.Length, 0));
    }

    public static MarkdownSourceEdit? Indent(SourceTextSnapshot source, int caret, bool outdent)
    {
        var blocks = MarkdownBlockStructure.Create(source);
        var block = blocks.At(caret);
        if (block.ListDepth == 0)
        {
            if (block.Kind != MarkdownBlockKind.Quote) return null;
            if (outdent)
            {
                var shorter = QuoteWithoutLastLevel(source.Text, block.QuotePrefix);
                return new(block.QuotePrefix, shorter,
                    new(Math.Max(block.Line.Start + shorter.Length, caret - (block.QuotePrefix.Length - shorter.Length)), 0));
            }
            return new(new(block.QuotePrefix.End, 0), "> ", new(caret + 2, 0));
        }
        if (outdent && block.ListIndent < 2) return null;
        var last = block.LineIndex;
        for (var index = last + 1; index < blocks.Blocks.Length; index++)
        {
            var child = blocks.Blocks[index];
            if (child.Kind == MarkdownBlockKind.None || child.QuoteDepth != block.QuoteDepth ||
                child.ListDepth == 0 || child.ListIndent <= block.ListIndent) break;
            last = index;
        }
        var replace = new SourceRange(block.Line.Start,
            blocks.Blocks[last].Line.End - block.Line.Start);
        var builder = new StringBuilder(source.GetText(replace));
        var changes = new List<int>();
        for (var index = block.LineIndex; index <= last; index++)
        {
            var child = blocks.Blocks[index];
            var position = child.QuotePrefix.End - replace.Start;
            changes.Add(position);
        }
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            if (outdent) builder.Remove(changes[index], 2);
            else builder.Insert(changes[index], "  ");
        }
        var movement = changes.Count(position => position <= caret - replace.Start) * (outdent ? -2 : 2);
        return new(replace, builder.ToString(), new(caret + movement, 0));
    }

    /// <summary>Indent every selected physical line once, without doubling a selected subtree.</summary>
    public static MarkdownSourceEdit? Indent(SourceTextSnapshot source, SourceRange selection, bool outdent)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.FullRange.Contains(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        if (selection.Length == 0) return Indent(source, selection.Start, outdent);
        var blocks = MarkdownBlockStructure.Create(source);
        var first = blocks.Lines.FindLine(selection.Start);
        var last = blocks.Lines.FindLine(selection.End - 1);
        var changes = new List<(int Start, int Remove, string Insert)>();
        for (var index = first; index <= last; index++)
        {
            var block = blocks.Blocks[index];
            if (block.ListDepth > 0)
            {
                if (outdent && block.ListIndent >= 2)
                    changes.Add((block.QuotePrefix.End, 2, ""));
                else if (!outdent)
                    changes.Add((block.QuotePrefix.End, 0, "  "));
            }
            else if (block.Kind == MarkdownBlockKind.Quote)
            {
                if (outdent)
                    changes.Add((block.QuotePrefix.Start, block.QuotePrefix.Length,
                        QuoteWithoutLastLevel(source.Text, block.QuotePrefix)));
                else changes.Add((block.QuotePrefix.End, 0, "> "));
            }
            else if (outdent)
            {
                var count = 0;
                while (count < 2 && block.Line.Start + count < block.Line.End &&
                    source.Text[block.Line.Start + count] == ' ') count++;
                if (count > 0) changes.Add((block.Line.Start, count, ""));
            }
            else changes.Add((block.Line.Start, 0, "  "));
        }
        if (changes.Count == 0) return null;
        var replace = new SourceRange(blocks.Blocks[first].Line.Start,
            blocks.Blocks[last].Line.End - blocks.Blocks[first].Line.Start);
        var builder = new StringBuilder(source.GetText(replace));
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            var change = changes[index];
            builder.Remove(change.Start - replace.Start, change.Remove);
            builder.Insert(change.Start - replace.Start, change.Insert);
        }
        int Move(int position)
        {
            var shift = 0;
            foreach (var change in changes)
            {
                if (position < change.Start) break;
                if (position < change.Start + change.Remove)
                    return change.Start + shift + change.Insert.Length;
                shift += change.Insert.Length - change.Remove;
            }
            return position + shift;
        }
        var start = Move(selection.Start);
        var end = Move(selection.End);
        return new(replace, builder.ToString(), new(start, Math.Max(0, end - start)));
    }

    public static MarkdownSourceEdit? Format(SourceTextSnapshot source, SourceRange selection,
        MarkdownBlockCommand command)
    {
        if (!source.FullRange.Contains(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        var block = MarkdownBlockStructure.Create(source).At(selection.Start);
        if (command == MarkdownBlockCommand.ToggleTask)
        {
            if (block.Kind != MarkdownBlockKind.TaskList) return null;
            var checkOffset = block.TaskMarker.Start + 1;
            return new(new(checkOffset, 1), block.TaskChecked ? " " : "x", selection);
        }
        if (command == MarkdownBlockCommand.Quote)
        {
            if (block.QuoteDepth > 0)
            {
                var shorter = QuoteWithoutLastLevel(source.Text, block.QuotePrefix);
                return ShiftSelection(block.QuotePrefix, shorter, selection);
            }
            return ShiftSelection(new(block.Line.Start, 0), "> ", selection);
        }
        var wanted = command switch
        {
            MarkdownBlockCommand.BulletList => MarkdownBlockKind.BulletList,
            MarkdownBlockCommand.OrderedList => MarkdownBlockKind.OrderedList,
            MarkdownBlockCommand.TaskList => MarkdownBlockKind.TaskList,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        var listStart = block.QuotePrefix.End;
        var listRange = new SourceRange(listStart, block.Prefix.End - listStart);
        var replacement = block.Kind == wanted ? "" :
            new string(' ', block.ListIndent) + (command switch
            {
                MarkdownBlockCommand.BulletList => "- ",
                MarkdownBlockCommand.OrderedList => "1. ",
                _ => "- [ ] "
            });
        return ShiftSelection(listRange, replacement, selection);
    }

    private static MarkdownSourceEdit ShiftSelection(SourceRange range, string replacement,
        SourceRange selection)
    {
        var change = replacement.Length - range.Length;
        int Shift(int position) => position < range.Start ? position :
            position >= range.End ? position + change : range.Start + replacement.Length;
        var start = Shift(selection.Start);
        var end = Shift(selection.End);
        return new(range, replacement, new(start, Math.Max(0, end - start)));
    }

    private static MarkdownSourceEdit? RenumberFollowingItems(SourceTextSnapshot source,
        MarkdownBlockStructure blocks, MarkdownBlockLine current, int caret, string inserted)
    {
        var siblings = new List<MarkdownBlockLine>();
        for (var index = current.LineIndex + 1; index < blocks.Blocks.Length; index++)
        {
            var next = blocks.Blocks[index];
            if (next.QuoteDepth != current.QuoteDepth) break;
            if (next.Kind == MarkdownBlockKind.None)
            {
                if (string.IsNullOrWhiteSpace(source.GetText(next.Line))) continue;
                break;
            }
            if (next.ListDepth > 0 && next.ListIndent > current.ListIndent) continue;
            if (next.ListIndent != current.ListIndent || next.Kind != MarkdownBlockKind.OrderedList ||
                next.OrderedDelimiter != current.OrderedDelimiter) break;
            siblings.Add(next);
        }
        if (siblings.Count == 0) return null;
        var replace = new SourceRange(caret, siblings[^1].ListPrefix.End - caret);
        var tail = new StringBuilder(source.GetText(replace));
        for (var index = siblings.Count - 1; index >= 0; index--)
        {
            var sibling = siblings[index];
            var start = sibling.ListPrefix.Start + sibling.ListIndent;
            var count = 0;
            while (start + count < source.Length && char.IsAsciiDigit(source.Text[start + count])) count++;
            tail.Remove(start - replace.Start, count);
            tail.Insert(start - replace.Start,
                (sibling.OrderedNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new(replace, inserted + tail, new(caret + inserted.Length, 0));
    }

    private static string QuoteWithoutLastLevel(string text, SourceRange prefix)
    {
        var last = text.LastIndexOf('>', prefix.End - 1, prefix.Length);
        return last < prefix.Start ? "" : text[prefix.Start..last];
    }

    private static string LineEnding(DocumentLineMap lines, int index)
    {
        var ending = lines.Lines[index].Ending;
        if (ending == DocumentLineEnding.None)
            for (var previous = index - 1; previous >= 0; previous--)
                if (lines.Lines[previous].Ending != DocumentLineEnding.None)
                { ending = lines.Lines[previous].Ending; break; }
        return ending switch
        {
            DocumentLineEnding.CrLf => "\r\n",
            DocumentLineEnding.Cr => "\r",
            _ => "\n"
        };
    }
}
