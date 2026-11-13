using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VoxelEngine.Commands;

/// <summary>
/// The command trees and the one walk over them that runs a line, completes a line being typed, and writes the usage
/// and help text, so none of them can drift from the others. A line is split on spaces; from the root its tokens
/// are matched against each node's children in the order they were added, the first that parses winning, and only
/// nodes the source's rights allow (a node's permission holds for its subtree) are matched, offered or shown.
/// GL-free and side-free: the effects happen in the handlers, through the source's host.
/// </summary>
public sealed class CommandDispatcher
{
    /// <summary>The answer to a node the source may not use.</summary>
    public const string OperatorsOnly = "That command is for operators only";

    private readonly List<LiteralNode> _roots = [];

    public IReadOnlyList<LiteralNode> Roots => _roots;

    /// <summary>Adds a command; a name or alias another command already answers to is a programming error.</summary>
    public void Register(LiteralNode root)
    {
        foreach (var name in root.Names)
            if (FindRoot(name) != null)
                throw new InvalidOperationException($"The command /{name} is registered twice.");
        _roots.Add(root);
    }

    /// <summary>The command answering to <paramref name="word"/> (a name or an alias, any case), or null.</summary>
    public LiteralNode? FindRoot(string word) => _roots.Find(root => root.Matches(word));

    /// <summary>The commands <paramref name="source"/> may run, in registration order.</summary>
    public IEnumerable<LiteralNode> Visible(CommandSource source)
    {
        foreach (var root in _roots)
            if (source.Allows(root.Permission))
                yield return root;
    }

    /// <summary>
    /// The usage line of <paramref name="root"/> as <paramref name="source"/> may use it, from its tree: branches
    /// joined by <c>|</c>, a step after a place the line may already end in brackets
    /// (<c>/give &lt;item&gt; [&lt;count&gt; [&lt;target&gt;]]</c>).
    /// </summary>
    public string Usage(LiteralNode root, CommandSource source)
    {
        var shape = Shape(root, source, root.Permission, nested: false);
        return shape.Length == 0 ? "/" + root.Name : $"/{root.Name} {shape}";
    }

    /// <summary>
    /// Runs one command line (its leading <c>/</c> included) for <paramref name="source"/>. Every outcome that is not
    /// the command's own answer is a reply too: an unknown command, missing rights, the first argument error met, or
    /// the command's usage when the line stops short or runs on; a <see cref="CommandException"/> from the handler is
    /// answered with its message.
    /// </summary>
    public void Execute(string line, CommandSource source)
    {
        var tokens = Tokenize(line.AsSpan().Trim().TrimStart('/'));
        if (tokens.Count == 0)
        {
            source.Reply("Type /help for the list of commands", null);
            return;
        }
        var root = FindRoot(tokens[0]);
        if (root == null)
        {
            source.Reply($"Unknown command /{tokens[0]}. Type /help for the list", null);
            return;
        }
        if (!source.Allows(root.Permission))
        {
            source.Reply(OperatorsOnly, null);
            return;
        }

        var context = new CommandContext(source);
        CommandNode node = root;
        var permission = root.Permission;
        var index = 1;
        while (index < tokens.Count)
        {
            var next = Step(node, tokens, ref index, source, ref permission, context, out var error);
            if (next == null)
            {
                source.Reply(error ?? "Usage: " + Usage(root, source), null);
                return;
            }
            node = next;
        }
        if (node.Handler == null)
        {
            source.Reply("Usage: " + Usage(root, source), null);
            return;
        }
        try
        {
            node.Handler(context);
        }
        catch (CommandException e)
        {
            source.Reply(e.Message, null);
        }
    }

    /// <summary>
    /// What to offer while <paramref name="text"/> is typed with the caret at <paramref name="caret"/>: the
    /// completions for the word the caret ends (the command names while it is the first word, then whatever the
    /// steps reached by the words before it accept), ranked by <see cref="SuggestionRanking"/>, and the usage of the
    /// command once its name is typed. A line that is not a command, or names one the source may not run, gets
    /// <see cref="CommandSuggestions.None"/>.
    /// </summary>
    public CommandSuggestions Suggest(string text, int caret, CommandSource source)
    {
        if (text.Length == 0 || text[0] != '/') return CommandSuggestions.None;
        caret = Math.Clamp(caret, 1, text.Length);
        var wordStart = Math.Max(1, text.LastIndexOf(' ', caret - 1) + 1);
        var typed = text[wordStart..caret];
        var tokens = Tokenize(text.AsSpan(1, wordStart - 1));
        var items = new List<Suggestion>();
        if (tokens.Count == 0)
        {
            foreach (var visible in Visible(source))
                foreach (var name in visible.Names)
                    items.Add(new Suggestion(name));
            return new CommandSuggestions(wordStart, SuggestionRanking.Rank(items, typed), null);
        }

        var root = FindRoot(tokens[0]);
        if (root == null || !source.Allows(root.Permission)) return CommandSuggestions.None;
        var usage = Usage(root, source);
        CommandNode node = root;
        var permission = root.Permission;
        var index = 1;
        while (index < tokens.Count)
        {
            // A multi-token argument the words so far only begin: the word being typed is its next token
            if (Unfinished(node, tokens, index, source, permission) is { } unfinished)
            {
                unfinished.Type.Suggest(source, tokens.Count - index, typed, items);
                return new CommandSuggestions(wordStart, SuggestionRanking.Rank(items, typed), usage);
            }
            var next = Step(node, tokens, ref index, source, ref permission, null, out _);
            if (next == null) return new CommandSuggestions(wordStart, [], usage);
            node = next;
        }

        foreach (var child in node.Children)
        {
            if (!source.Allows(Max(permission, child.Permission))) continue;
            if (child is LiteralNode literal)
                items.Add(new Suggestion(literal.Name));
            else if (child is ArgumentNode argument)
                argument.Type.Suggest(source, 0, typed, items);
        }
        return new CommandSuggestions(wordStart, SuggestionRanking.Rank(items, typed), usage);
    }

    // Matches the token at index against node's children in order: the first that parses wins, moving index past
    // what it read and folding its permission in. Null when none does, with the first argument error met, unless the
    // tokens begin an argument they are too few to finish, where the usage says more than an error about a token
    // another branch wanted.
    private static CommandNode? Step(CommandNode node, List<string> tokens, ref int index, CommandSource source,
        ref CommandPermission permission, CommandContext? context, out string? error)
    {
        error = null;
        var cutShort = false;
        foreach (var child in node.Children)
        {
            var required = Max(permission, child.Permission);
            if (!source.Allows(required)) continue;
            if (child is LiteralNode literal)
            {
                if (!literal.Matches(tokens[index])) continue;
                index++;
                permission = required;
                return child;
            }
            if (child is not ArgumentNode argument) continue;
            var count = argument.Type.TokenCount;
            if (index + count > tokens.Count)
            {
                cutShort |= argument.Type.CouldStart(CollectionsMarshal.AsSpan(tokens)[index..]);
                continue;
            }
            if (!argument.Type.TryParse(CollectionsMarshal.AsSpan(tokens).Slice(index, count), source, out var value, out var parseError))
            {
                error ??= parseError;
                continue;
            }
            context?.Set(argument.Name, value);
            index += count;
            permission = required;
            return child;
        }
        if (cutShort) error = null;
        return null;
    }

    // The multi-token argument among node's children that the tokens from index on begin but do not finish
    private static ArgumentNode? Unfinished(CommandNode node, List<string> tokens, int index, CommandSource source,
        CommandPermission permission)
    {
        foreach (var child in node.Children)
        {
            if (child is not ArgumentNode { Type.TokenCount: > 1 } argument) continue;
            if (!source.Allows(Max(permission, child.Permission))) continue;
            if (index + argument.Type.TokenCount <= tokens.Count) continue;
            if (argument.Type.CouldStart(CollectionsMarshal.AsSpan(tokens)[index..]))
                return argument;
        }
        return null;
    }

    private static string Shape(CommandNode node, CommandSource source, CommandPermission permission, bool nested)
    {
        var branches = new List<string>();
        foreach (var child in node.Children)
        {
            var required = Max(permission, child.Permission);
            if (!source.Allows(required)) continue;
            var rest = Shape(child, source, required, nested: true);
            branches.Add(rest.Length == 0 ? child.Syntax : $"{child.Syntax} {rest}");
        }
        if (branches.Count == 0) return "";
        var joined = string.Join(" | ", branches);
        if (node.Handler != null) return $"[{joined}]";
        return nested && branches.Count > 1 ? $"({joined})" : joined;
    }

    private static CommandPermission Max(CommandPermission a, CommandPermission b) => a >= b ? a : b;

    private static List<string> Tokenize(ReadOnlySpan<char> text)
    {
        var tokens = new List<string>();
        foreach (var range in text.Split(' '))
        {
            var token = text[range];
            if (token.Length > 0)
                tokens.Add(token.ToString());
        }
        return tokens;
    }
}
