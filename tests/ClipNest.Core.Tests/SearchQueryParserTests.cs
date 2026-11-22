using ClipNest.Core.Search;

namespace ClipNest.Core.Tests;

public sealed class SearchQueryParserTests
{
    private readonly SearchQueryParser _parser = new();

    [Fact]
    public void Parse_And_Not_Tags()
    {
        var node = Assert.IsType<AndNode>(_parser.Parse("#旅行 #夜景 -#人像"));
        Assert.Equal(3, node.Children.Count);
        Assert.IsType<TagNode>(node.Children[0]);
        Assert.IsType<TagNode>(node.Children[1]);
        var not = Assert.IsType<NotNode>(node.Children[2]);
        Assert.IsType<TagNode>(not.Child);
    }

    [Fact]
    public void Parse_Quoted_Tag_Keeps_Spaces()
    {
        var node = Assert.IsType<TagNode>(_parser.Parse("#\"fujita kotone\""));
        Assert.Equal("fujita kotone", node.Path);
    }

    [Fact]
    public void Parse_Or_Tags()
    {
        var node = Assert.IsType<OrNode>(_parser.Parse("#旅行|#美食"));
        Assert.Equal(2, node.Children.Count);
    }

    [Fact]
    public void Parse_Range_Field()
    {
        var node = Assert.IsType<FieldNode>(_parser.Parse("year:2020..2024"));
        Assert.Equal("year", node.Field);
        Assert.Equal("range", node.Operator);
        Assert.Equal("2020..2024", node.Value);
    }

    [Fact]
    public void Parse_Semantic_Quoted()
    {
        var node = Assert.IsType<SemanticNode>(_parser.Parse("sim:\"海边的日落\""));
        Assert.Equal("海边的日落", node.Text);
    }

    [Fact]
    public void Parse_Empty_Is_MatchAll()
    {
        Assert.IsType<MatchAllNode>(_parser.Parse("   "));
    }

    [Fact]
    public void Parse_Unclosed_Paren_Throws()
    {
        Assert.Throws<SearchParseException>(() => _parser.Parse("(#旅行"));
    }
}
