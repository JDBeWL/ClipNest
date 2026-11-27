using System.Text;

namespace ClipNest.Core.Search;

public sealed class SearchQueryParser
{
    public QueryNode Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new MatchAllNode();
        var tokens = Tokenize(input);
        var index = 0;
        var node = ParseOr(tokens, ref index);
        if (index != tokens.Count) throw new SearchParseException($"无法识别的标记：{tokens[index]}");
        return node;
    }

    private static QueryNode ParseOr(List<string> tokens, ref int index)
    {
        var nodes = new List<QueryNode> { ParseAnd(tokens, ref index) };
        while (index < tokens.Count && tokens[index] == "|")
        {
            index++;
            nodes.Add(ParseAnd(tokens, ref index));
        }
        return nodes.Count == 1 ? nodes[0] : new OrNode(nodes);
    }

    private static QueryNode ParseAnd(List<string> tokens, ref int index)
    {
        var nodes = new List<QueryNode>();
        while (index < tokens.Count && tokens[index] != ")" && tokens[index] != "|")
            nodes.Add(ParseUnary(tokens, ref index));

        if (nodes.Count == 0) return new MatchAllNode();
        return nodes.Count == 1 ? nodes[0] : new AndNode(nodes);
    }

    private static QueryNode ParseUnary(List<string> tokens, ref int index)
    {
        var token = tokens[index];
        if (token == "-")
        {
            index++;
            return new NotNode(ParseUnary(tokens, ref index));
        }
        if (token.StartsWith('-') && token.Length > 1)
        {
            index++;
            return new NotNode(ParseToken(token[1..]));
        }
        if (token == "(")
        {
            index++;
            var node = ParseOr(tokens, ref index);
            if (index >= tokens.Count || tokens[index] != ")")
                throw new SearchParseException("缺少右括号");
            index++;
            return node;
        }
        index++;
        return ParseToken(token);
    }

    private static QueryNode ParseToken(string token)
    {
        if (token.StartsWith('#'))
        {
            var path = token[1..];
            if (path.Length == 0) throw new SearchParseException("标签不能为空");
            return new TagNode(path, !path.Contains('/'));
        }

        var colon = token.IndexOf(':');
        if (colon > 0)
        {
            var field = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..];
            if (value.Length == 0) throw new SearchParseException($"字段 {field} 缺少值");
            if (field == "sim") return new SemanticNode(value);

            var op = "eq";
            if (value.StartsWith(">=")) { op = "gte"; value = value[2..]; }
            else if (value.StartsWith("<=")) { op = "lte"; value = value[2..]; }
            else if (value.StartsWith(">")) { op = "gt"; value = value[1..]; }
            else if (value.StartsWith("<")) { op = "lt"; value = value[1..]; }
            else if (value.StartsWith("=")) { value = value[1..]; }

            if (value.Contains("..")) op = "range";
            return new FieldNode(field, op, value);
        }

        return new TextNode(token);
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var buffer = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && (char.IsWhiteSpace(ch) || ch is '(' or ')' or '|'))
            {
                Flush();
                if (ch is '(' or ')' or '|') tokens.Add(ch.ToString());
                continue;
            }
            buffer.Append(ch);
        }

        if (inQuotes) throw new SearchParseException("引号未闭合");
        Flush();
        return tokens;

        void Flush()
        {
            if (buffer.Length == 0) return;
            tokens.Add(buffer.ToString());
            buffer.Clear();
        }
    }
}
