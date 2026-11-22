namespace ClipNest.Core.Search;

public abstract record QueryNode;
public sealed record MatchAllNode : QueryNode;
public sealed record AndNode(IReadOnlyList<QueryNode> Children) : QueryNode;
public sealed record OrNode(IReadOnlyList<QueryNode> Children) : QueryNode;
public sealed record NotNode(QueryNode Child) : QueryNode;
public sealed record TagNode(string Path, bool IncludeDescendants) : QueryNode;
public sealed record TextNode(string Text) : QueryNode;
public sealed record FieldNode(string Field, string Operator, string Value) : QueryNode;
public sealed record SemanticNode(string Text) : QueryNode;
