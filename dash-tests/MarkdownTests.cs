using System.Collections.Generic;
using System.Linq;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class MarkdownTests
    {
        private static List<string> Texts(string content)
        {
            var cfg = P("views", new List<object> { P("title", "M", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "markdown", "content", content) }) }) });
            var (s, _) = Session("fixture-sections", P("scale", 1, "theme", P("name", "default")), cfg);
            return Nodes(Render(s)).Select(n => Json.S(n, "text")).Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        [Fact]
        public void BrBreaksTheLine() => Assert.Contains("Thai Place\nPreparing - 7:40 PM", Texts("Thai Place<br>Preparing - 7:40 PM"));

        [Fact]
        public void BrSelfClosing() => Assert.Contains("a\nb", Texts("a<BR />b"));

        [Fact]
        public void SingleNewlineBreaksTheLine() => Assert.Contains("one\ntwo", Texts("one\ntwo"));

        [Fact]
        public void NoSpaceUnderTheLastLine()
        {
            // the card padding (12) above and below; one line of 14 px is 14 * 1.35 tall
            var cfg = P("views", new List<object> { P("title", "M", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "markdown", "content", "one line") }) }) });
            var (s, _) = Session("fixture-sections", P("scale", 1, "theme", P("name", "ambient")), cfg);
            var card = Nodes(Render(s)).First(n => Json.S(n, "name") == "d.s0.c0");
            Assert.InRange(Json.N(card, "h", -1), 12 + 14 * 1.35 + 12 - 0.5, 12 + 14 * 1.35 + 12 + 0.5);
        }

        [Fact]
        public void TextSpaceEqualAboveAndBelow()
        {
            var cfg = P("views", new List<object> { P("title", "M", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "markdown", "content", "one\ntwo\n\nthree") }) }) });
            var (s, _) = Session("fixture-sections", P("scale", 1, "theme", P("name", "ambient")), cfg);
            var nodes = Nodes(Render(s));
            var card = nodes.First(n => Json.S(n, "name") == "d.s0.c0");
            var texts = (Json.A(card, "children") ?? new List<object>()).Cast<Dictionary<string, object>>().Where(n => Json.S(n, "type") == "text").ToList();
            double top = Json.N(texts.First(), "y", -1), last = Json.N(texts.Last(), "y", -1) + Json.N(texts.Last(), "h", -1);
            Assert.Equal(top, Json.N(card, "h", -1) - last, 6);
        }

        [Fact]
        public void BlankLineStartsAParagraph()
        {
            var t = Texts("one\n\ntwo");
            Assert.Contains("one", t);
            Assert.Contains("two", t);
        }
    }
}
