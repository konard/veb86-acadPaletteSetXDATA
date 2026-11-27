using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

public enum NavigatorNodeKind
{
    /// <summary>A value of a group level of the expression; not an AutoCAD object.</summary>
    Group,
    /// <summary>Leaves with the same name under one parent are collected into an automatic group (ZCAD <c>TNMAutoGroup</c>).</summary>
    AutoGroup,
    /// <summary>An object: it carries the handle only.</summary>
    Leaf
}

/// <summary>
/// A node of the navigator tree (issue #66). Groups are values of the expression, not AutoCAD objects; a leaf
/// refers to its object by <see cref="NavigatorObject.Handle"/>, checked against the drawing when it is selected.
/// </summary>
public sealed class NavigatorNode
{
    private readonly List<NavigatorNode> children = new List<NavigatorNode>();
    private Dictionary<string, NavigatorNode>? groups;
    private Dictionary<string, NavigatorNode>? leaves;

    internal NavigatorNode(NavigatorNodeKind kind, string name, NavigatorNode? parent, NavigatorObject? target = null)
    {
        Kind = kind; Name = name; Parent = parent; Object = target;
        Depth = parent is null ? -1 : parent.Depth + 1;
        Key = parent is null ? "" : parent.Key + "/" + (kind == NavigatorNodeKind.Leaf ? "o:" + target!.Handle : "g:" + name);
    }

    public NavigatorNodeKind Kind { get; }
    public string Name { get; }
    public NavigatorNode? Parent { get; }
    /// <summary>The object of a leaf; null for groups.</summary>
    public NavigatorObject? Object { get; }
    public string? Handle => Object?.Handle;
    public bool IsGroup => Kind != NavigatorNodeKind.Leaf;
    /// <summary>The depth from the top level (0).</summary>
    public int Depth { get; }
    /// <summary>
    /// The path of group names (and the handle of a leaf) from the root: the expanded state is kept by it across
    /// refreshes, which make new node objects.
    /// </summary>
    public string Key { get; }
    public IReadOnlyList<NavigatorNode> Children => children;
    /// <summary>The value of the group could not be computed (the «#ОШИБКА» group) or the name of the leaf failed.</summary>
    public bool IsError { get; internal set; }
    /// <summary>The group of objects without the value (<see cref="NavigatorTreeBuilder.EmptyText"/>).</summary>
    public bool IsEmptyValue { get; internal set; }
    /// <summary>Diagnostics of the node: the errors of the values that put objects into it.</summary>
    public string? Error { get; internal set; }
    /// <summary>The objects under the node.</summary>
    public int LeafCount { get; internal set; }
    /// <summary>The text of the tree column: groups show the number of objects.</summary>
    public string Caption => IsGroup ? Name + " (" + LeafCount.ToString(CultureInfo.InvariantCulture) + ")" : Name;

    internal NavigatorNode Group(string name, bool error, bool empty)
    {
        groups ??= new Dictionary<string, NavigatorNode>(StringComparer.Ordinal);
        var key = (error ? "e:" : empty ? "n:" : "v:") + name;
        if (groups.TryGetValue(key, out var group)) return group;
        group = new NavigatorNode(NavigatorNodeKind.Group, name, this) { IsError = error, IsEmptyValue = empty };
        groups[key] = group;
        children.Add(group);
        return group;
    }

    /// <summary>
    /// Adds a leaf; a second leaf with the same name turns the first one into an automatic group holding both
    /// (ZCAD <c>ConvertNameNodeToGroupNode</c>).
    /// </summary>
    internal NavigatorNode Leaf(string name, NavigatorObject target)
    {
        leaves ??= new Dictionary<string, NavigatorNode>(StringComparer.Ordinal);
        if (!leaves.TryGetValue(name, out var existing))
        {
            var leaf = new NavigatorNode(NavigatorNodeKind.Leaf, name, this, target);
            leaves[name] = leaf;
            children.Add(leaf);
            return leaf;
        }
        if (existing.Kind == NavigatorNodeKind.Leaf)
        {
            var auto = new NavigatorNode(NavigatorNodeKind.AutoGroup, name, this) { IsError = existing.IsError, Error = existing.Error };
            children[children.IndexOf(existing)] = auto;
            leaves[name] = auto;
            var moved = new NavigatorNode(NavigatorNodeKind.Leaf, name, auto, existing.Object) { IsError = existing.IsError, Error = existing.Error };
            auto.children.Add(moved);
            existing = auto;
        }
        var added = new NavigatorNode(NavigatorNodeKind.Leaf, name, existing, target);
        existing.children.Add(added);
        return added;
    }

    internal void Sort(Comparison<NavigatorNode> comparison)
    {
        // A stable sort: equal values keep the order of the drawing.
        var ordered = children.Select((node, index) => (node, index)).ToList();
        ordered.Sort((left, right) => { var result = comparison(left.node, right.node); return result != 0 ? result : left.index.CompareTo(right.index); });
        children.Clear();
        children.AddRange(ordered.Select(item => item.node));
        foreach (var child in children) if (child.IsGroup) child.Sort(comparison);
    }

    internal int Count()
    {
        if (!IsGroup) return 1;
        LeafCount = children.Sum(child => child.Count());
        return LeafCount;
    }

    /// <summary>The node and every node under it, depth first.</summary>
    public IEnumerable<NavigatorNode> Descendants()
    {
        foreach (var child in children)
        {
            yield return child;
            foreach (var item in child.Descendants()) yield return item;
        }
    }

    public override string ToString() => Caption;
}

/// <summary>The tree built from the objects of the drawing with one expression.</summary>
public sealed class NavigatorTree
{
    internal NavigatorTree(NavigatorNode root, NavigatorTreeExpression expression, int objects, int errors)
    {
        Root = root; Expression = expression; ObjectCount = objects; ErrorCount = errors;
    }

    /// <summary>The invisible root: its children are the top level.</summary>
    public NavigatorNode Root { get; }
    public NavigatorTreeExpression Expression { get; }
    public int ObjectCount { get; }
    /// <summary>Objects whose group or name could not be computed.</summary>
    public int ErrorCount { get; }
    public IEnumerable<NavigatorNode> Nodes => Root.Descendants();
    public IEnumerable<NavigatorNode> Leaves => Nodes.Where(node => node.Kind == NavigatorNodeKind.Leaf);

    /// <summary>
    /// Sorts the children of every node, keeping the groups: groups come first, ordered by name (the empty and
    /// error groups last), then leaves by <paramref name="key"/>. Sorting by the tree column orders groups by
    /// <paramref name="descending"/> too; sorting by a column keeps groups ascending, since they have no value in it.
    /// </summary>
    public void Sort(Func<NavigatorNode, string>? key = null, bool descending = false)
    {
        var compare = NaturalComparer.Instance;
        int Rank(NavigatorNode node) => !node.IsGroup ? 3 : node.IsError ? 2 : node.IsEmptyValue ? 1 : 0;
        Root.Sort((left, right) =>
        {
            var leftGroup = left.IsGroup; var rightGroup = right.IsGroup;
            if (leftGroup != rightGroup) return leftGroup ? -1 : 1;
            if (leftGroup)
            {
                if (Rank(left) != Rank(right)) return Rank(left).CompareTo(Rank(right));
                var byName = compare.Compare(left.Name, right.Name);
                return key is null && descending ? -byName : byName;
            }
            var result = key is null ? compare.Compare(left.Name, right.Name) : compare.Compare(key(left), key(right));
            return descending ? -result : result;
        });
    }
}

/// <summary>
/// Builds the tree from the expression only (issue #66): columns and their order do not change the tree.
/// <list type="bullet">
/// <item>A missing parameter or an empty string puts the object into the <see cref="EmptyText"/> group.</item>
/// <item>A parameter that is not in the schema is unknown: its objects go to the <see cref="EmptyText"/> group too,
/// and <see cref="NavigatorProfile.Validate"/> reports it.</item>
/// <item>A calculation error puts the object into the «#ОШИБКА» group, marked as an error with the diagnostics.</item>
/// <item>Objects with the same value share one group; leaves with the same name form an automatic group.</item>
/// <item>The leaf is named by the last part; when it is disabled or empty, by the type of the object.</item>
/// <item>An exception on one object makes it an error leaf; the other objects are not affected.</item>
/// </list>
/// </summary>
public static class NavigatorTreeBuilder
{
    public const string DefaultEmptyText = "Не задано";

    public static NavigatorTree Build(IEnumerable<NavigatorObject> objects, NavigatorTreeExpression expression, string? emptyText = null)
    {
        if (objects is null) throw new ArgumentNullException(nameof(objects));
        if (expression is null) throw new ArgumentNullException(nameof(expression));
        var empty = string.IsNullOrWhiteSpace(emptyText) ? DefaultEmptyText : emptyText!;
        var root = new NavigatorNode(NavigatorNodeKind.Group, "", null);
        var groups = expression.Groups;
        var leaf = expression.Leaf;
        var count = 0;
        var errors = 0;
        foreach (var target in objects)
        {
            count++;
            var parent = root;
            var failed = false;
            try
            {
                foreach (var level in groups)
                {
                    var value = target.Value(level.Source);
                    if (value.IsError)
                    {
                        failed = true;
                        parent = parent.Group(FormulaExpression.ErrorText, true, false);
                        parent.Error = Append(parent.Error, target.Handle + ": " + value.Error);
                    }
                    else if (value.IsEmpty) parent = parent.Group(empty, false, true);
                    else parent = parent.Group(value.Text, false, false);
                }
                var name = leaf is null ? NavigatorValue.Empty : target.Value(leaf.Source);
                var node = parent.Leaf(name.IsError ? FormulaExpression.ErrorText : name.IsEmpty ? target.TypeName : name.Text, target);
                if (name.IsError) { node.IsError = true; node.Error = name.Error; failed = true; }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failed = true;
                var node = root.Group(FormulaExpression.ErrorText, true, false).Leaf(target.TypeName, target);
                node.IsError = true;
                node.Error = failure.Message;
            }
            if (failed) errors++;
        }
        root.Count();
        var tree = new NavigatorTree(root, expression, count, errors);
        tree.Sort();
        return tree;
    }

    private static string Append(string? text, string line)
    {
        const int limit = 20;
        if (text is null) return line;
        var lines = text.Split('\n');
        if (lines.Length > limit) return text;
        return lines.Length == limit ? text + "\n…" : text + "\n" + line;
    }
}

/// <summary>
/// The natural order of ZCAD <c>AnsiNaturalCompare</c>: runs of digits compare as numbers («Щ2» before «Щ10»),
/// the rest case-insensitively by the current culture.
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static NaturalComparer Instance { get; } = new NaturalComparer(CultureInfo.CurrentCulture);

    private readonly CompareInfo compare;

    public NaturalComparer(CultureInfo culture) { compare = (culture ?? CultureInfo.InvariantCulture).CompareInfo; }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                var leftStart = i; var rightStart = j;
                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;
                var leftNumber = left.Substring(leftStart, i - leftStart).TrimStart('0');
                var rightNumber = right.Substring(rightStart, j - rightStart).TrimStart('0');
                if (leftNumber.Length != rightNumber.Length) return leftNumber.Length.CompareTo(rightNumber.Length);
                var digits = string.CompareOrdinal(leftNumber, rightNumber);
                if (digits != 0) return digits;
                continue;
            }
            var leftEnd = i; var rightEnd = j;
            while (leftEnd < left.Length && !char.IsDigit(left[leftEnd])) leftEnd++;
            while (rightEnd < right.Length && !char.IsDigit(right[rightEnd])) rightEnd++;
            var result = compare.Compare(left, i, leftEnd - i, right, j, rightEnd - j, CompareOptions.IgnoreCase);
            if (result != 0) return result;
            i = leftEnd; j = rightEnd;
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }
}
