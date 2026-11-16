using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class RecolorTests
    {
        private static readonly RecolorSpec R = new RecolorSpec();   // white on black tiles, alpha mode, floor 0.35

        private static int Alpha(string hex) => Convert.ToInt32(hex.Substring(7, 2), 16);
        private static string Rgb(string hex) => hex.Substring(0, 7);
        private static double V(double lum) => 0.35 + 0.65 * lum;

        [Fact]
        public void MapWhiteFull() => Assert.Equal("#ffffffff", Recolor.Map("#ffffff", R));

        [Fact]
        public void MapBlackFloor()
        {
            var m = Recolor.Map("#000000", R);
            Assert.Equal("#ffffff", Rgb(m));
            Assert.Equal((int)Math.Round(0.35 * 255), Alpha(m));
        }

        [Fact]
        public void MapKeepsAlpha()
        {
            double redL = 0.5324;   // CIE L* of pure red, 0..1
            Assert.Equal((int)Math.Round(0x80 * V(redL)), Alpha(Recolor.Map("#ff000080", R)));
        }

        [Fact]
        public void MapNamedAndRgb()
        {
            Assert.Equal(Recolor.Map("#ffa500", R), Recolor.Map("orange", R));
            Assert.Equal(Recolor.Map("#03a9f4", R), Recolor.Map("rgb(3, 169, 244)", R));
        }

        [Fact]
        public void MapIsPerColor()
        {
            var a = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffffb0") });
            var b = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffffb0"), P("type", "text", "color", "#000000") });
            Recolor.Card(a, R); Recolor.Card(b, R);
            Assert.Equal(Json.S((Dictionary<string, object>)Json.A(a, "children")[0], "color"), Json.S((Dictionary<string, object>)Json.A(b, "children")[0], "color"));
        }

        [Fact]
        public void RampMode()
        {
            var r = new RecolorSpec { Mode = "ramp", TileColor = "#000000" };
            var m = Recolor.Map("#000000", r);
            Assert.Equal("#595959ff", m);   // 0.35 of the way from black to white, opaque
        }

        [Fact]
        public void CardTile()
        {
            var card = P("type", "box", "bg", "#121216b8", "radius", 12, "children", new List<object> { P("type", "box", "bg", "#ff980038", "radius", 20) });
            Recolor.Card(card, R);
            Assert.Equal("#00000059", Json.S(card, "bg"));
            Assert.Equal(16.0, Json.N(card, "radius", 0));
            var badge = Json.S((Dictionary<string, object>)Json.A(card, "children")[0], "bg");
            Assert.Equal("#ffffff", Rgb(badge));
        }

        [Fact]
        public void PhotosUntouched()
        {
            var photo = P("type", "image", "src", "http://ha.invalid/api/camera_proxy/camera.porch", "bg", "#ff0000");
            var jpeg = P("type", "image", "src", "data:image/jpeg;base64,AAAA");
            var vid = P("type", "video", "src", "http://x/v.mp4", "bg", "#00ff00");
            var mj = P("type", "mjpeg", "src", "http://x/m", "bg", "#0000ff");
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { photo, jpeg, vid, mj });
            var before = Json.Write(Json.A(card, "children"));
            Recolor.Card(card, R);
            Assert.Equal(before, Json.Write(Json.A(card, "children")));
        }

        [Fact]
        public void SvgRewrite()
        {
            string svg = "<svg><path fill=\"#f44336\" d=\"M0 0L1 1\"/><path fill=\"none\" stroke=\"rgb(3,169,244)\" stroke-width=\"2\"/>"
                       + "<stop offset=\"0\" stop-color=\"orange\"/><rect style=\"fill:#4caf50;stroke-width:3\"/><path fill=\"url(#g)\"/>"
                       + "<path fill=\"#ffffff\" fill-opacity=\"0.5\"/></svg>";
            var o = Recolor.Svg(svg, R);
            Assert.Contains("d=\"M0 0L1 1\"", o);
            Assert.Contains("fill=\"none\"", o);
            Assert.Contains("fill=\"url(#g)\"", o);
            Assert.Contains("stroke-width=\"2\"", o);
            Assert.Contains("stroke-width:3", o);
            Assert.DoesNotContain("#f44336", o);
            Assert.DoesNotContain("rgb(3,169,244)", o);
            Assert.DoesNotContain("orange", o);
            Assert.DoesNotContain("#4caf50", o);
            Assert.Contains("fill-opacity=\"0.5\"", o);   // white maps to full strength: 0.5 * 1
            Assert.Contains("stop-color=\"#ffffff\"", o);
            Assert.Contains("stop-opacity=", o);
        }

        [Fact]
        public void SvgDataUriInCard()
        {
            var uri = Svg.DataUri("<svg><path fill=\"#f44336\"/></svg>");
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "image", "src", uri) });
            Recolor.Card(card, R);
            var src = Json.S((Dictionary<string, object>)Json.A(card, "children")[0], "src");
            var svg = Encoding.UTF8.GetString(Convert.FromBase64String(src.Substring("data:image/svg+xml;base64,".Length)));
            Assert.Contains("fill=\"#ffffff\"", svg);
            Assert.Contains("fill-opacity=", svg);
        }

        [Fact]
        public void GradientStops()
        {
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "box", "gradient", P("colors", new List<object> { "#ff0000", "#0000ff00" })) });
            Recolor.Card(card, R);
            var cols = Json.A(Json.O((Dictionary<string, object>)Json.A(card, "children")[0], "gradient"), "colors").Cast<string>().ToList();
            Assert.All(cols, c => Assert.Equal("#ffffff", Rgb(c)));
            Assert.Equal(0, Alpha(cols[1]));
        }

        [Fact]
        public void TextOnFilledShapeKnocksOut()
        {
            var bar = P("type", "box", "bg", "#44739e", "children", new List<object> { P("type", "text", "color", "#1b1b1b") });
            var today = P("type", "box", "bg", "#03a9f4", "children", new List<object> { P("type", "text", "color", "#ffffff") });
            var faint = P("type", "box", "bg", "#ff980038", "children", new List<object> { P("type", "text", "color", "#ffffff") });
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { bar, today, faint });
            Recolor.Card(card, R);
            string T(Dictionary<string, object> b) => Json.S((Dictionary<string, object>)Json.A(b, "children")[0], "color");
            Assert.Equal("#000000ff", T(bar));     // dark text on a bar: the tile color, opaque
            Assert.Equal("#000000ff", T(today));   // light text on a filled circle: knocked out too
            Assert.Equal("#ffffffff", T(faint));   // a faint tint isn't a filled shape: mapped as usual
        }

        [Fact]
        public void SurfacesBecomeTiles()
        {
            // a badge chip or sub-panel drawn in the card color is a surface, not a filled shape
            var chip = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffff") });
            var holder = P("type", "box", "children", new List<object> { chip });
            Recolor.Card(holder, new RecolorSpec { Surface = "#121216b8" }, null, tile: false);
            Assert.Equal("#00000059", Json.S(chip, "bg"));
            Assert.Equal("#ffffffff", Json.S((Dictionary<string, object>)Json.A(chip, "children")[0], "color"));
        }

        [Fact]
        public void SurfaceGradientFadesToTile()
        {
            // the media card's fade from the card color to transparent becomes a fade from the tile
            var fade = P("type", "box", "gradient", P("colors", new List<object> { "#121216b8", "#12121600" }));
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { fade });
            Recolor.Card(card, new RecolorSpec { Surface = "#121216b8" });
            var cols = Json.A(Json.O(fade, "gradient"), "colors").Cast<string>().ToList();
            Assert.Equal(new[] { "#00000059", "#00000000" }, cols);
        }

        [Fact]
        public void HolderWithoutBackgroundGetsNoTile()
        {
            // a stack or chips row draws no card of its own: its inner cards are the tiles
            var inner = P("type", "box", "bg", "#121216b8");
            var stack = P("type", "box", "children", new List<object> { inner });
            Recolor.Card(stack, new RecolorSpec { Surface = "#121216b8" });
            Assert.False(Json.Has(stack, "bg"));
            Assert.Equal("#00000059", Json.S(inner, "bg"));
        }

        [Fact]
        public void TextIsOpaqueAndOutlined()
        {
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> {
                P("type", "text", "color", "#ffffffb0"), P("type", "text", "color", "#9e9e9e") });
            Recolor.Card(card, new RecolorSpec());
            foreach (Dictionary<string, object> t in Json.A(card, "children"))
            {
                Assert.Equal("#ffffffff", Json.S(t, "color"));   // secondary text no longer fades out
                var o = Json.O(t, "outline");
                Assert.Equal("#000000ff", Json.S(o, "color"));
                Assert.Equal(2.0, Json.N(o, "width", -1));
            }
        }

        [Fact]
        public void TextOpacityAndOutlineAreSettings()
        {
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffff") });
            Recolor.Card(card, new RecolorSpec { TextOpacity = 0.8, Outline = 0 });
            var t = (Dictionary<string, object>)Json.A(card, "children")[0];
            Assert.Equal("#ffffffcc", Json.S(t, "color"));
            Assert.False(Json.Has(t, "outline"));
            var tree = Render("fixture-sections", P("theme", P("name", "ambient", "text_opacity", 0.8, "outline", 3, "outline_color", "#202020")));
            var text = Nodes(tree).First(n => Json.S(n, "type") == "text");
            Assert.Equal("#ffffffcc", Json.S(text, "color"));
            Assert.Equal("#202020ff", Json.S(Json.O(text, "outline"), "color"));
            Assert.Equal(3.0, Json.N(Json.O(text, "outline"), "width", -1));
        }

        [Fact]
        public void ShadowIsASetting()
        {
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffff") });
            Recolor.Card(card, new RecolorSpec());
            Assert.False(Json.Has((Dictionary<string, object>)Json.A(card, "children")[0], "text_shadow"));   // off by default
            var tree = Render("fixture-sections", P("theme", P("name", "ambient", "shadow", 4, "shadow_color", "#000000", "shadow_x", 1, "shadow_y", 3)));
            var ts = Json.O(Nodes(tree).First(n => Json.S(n, "type") == "text"), "text_shadow");
            Assert.Equal("#000000ff", Json.S(ts, "color"));
            Assert.Equal(4.0, Json.N(ts, "blur", -1));
            Assert.Equal(1.0, Json.N(ts, "x", -1));
            Assert.Equal(3.0, Json.N(ts, "y", -1));
        }

        [Fact]
        public void MinWeightRaisesLightText()
        {
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> {
                P("type", "text", "color", "#ffffff"), P("type", "text", "color", "#ffffff", "weight", "light"),
                P("type", "text", "color", "#ffffff", "weight", "bold") });
            Recolor.Card(card, new RecolorSpec());
            var w = Json.A(card, "children").Cast<Dictionary<string, object>>().Select(t => Json.S(t, "weight")).ToList();
            Assert.Equal(new[] { "medium", "medium", "bold" }, w);
            Recolor.Card(card = P("type", "box", "bg", "#121216b8", "children", new List<object> { P("type", "text", "color", "#ffffff") }), new RecolorSpec { MinWeight = "regular" });
            Assert.False(Json.Has((Dictionary<string, object>)Json.A(card, "children")[0], "weight"));
        }

        [Fact]
        public void KnockoutUsesItsOwnColor()
        {
            // a light tile: text on a filled bar must not take the tile's white
            var bar = P("type", "box", "bg", "#44739e", "children", new List<object> { P("type", "text", "color", "#1b1b1b") });
            var card = P("type", "box", "bg", "#121216b8", "children", new List<object> { bar });
            Recolor.Card(card, new RecolorSpec { TileColor = "#ffffff", TileOpacity = 0.18 });
            Assert.Equal("#000000ff", Json.S((Dictionary<string, object>)Json.A(bar, "children")[0], "color"));
        }

        [Fact]
        public void TranslucentFillsHaveAFloor()
        {
            // a graph's shaded area (translucent fill) stays visible on a light tile; solid fills and strokes are untouched
            var o = Recolor.Svg("<svg><path fill=\"#ff9800\" fill-opacity=\"0.15\" d=\"M0 0\"/><path fill=\"#ffffff\" d=\"M1 1\"/>"
                              + "<path fill=\"none\" stroke=\"#ff9800\" stroke-opacity=\"0.15\" d=\"M2 2\"/></svg>", new RecolorSpec { MinFill = 0.45 });
            Assert.Contains("fill-opacity=\"0.45\" d=\"M0 0\"", o);
            Assert.DoesNotContain("fill-opacity", o.Substring(o.IndexOf("M0 0") + 4, o.IndexOf("M1 1") - o.IndexOf("M0 0") - 4).Replace("fill=\"#ffffff\" d=", ""));
            Assert.Contains("stroke-opacity=\"0.", o);
            Assert.Equal(0.45, Json.N(Eff(), "fill_opacity", 0));
        }

        private static Dictionary<string, object> Eff()
        {
            var (s, _) = Session("fixture-sections", P("theme", P("name", "ambient")));
            Render(s);
            return s.Eff.ThemeParams;
        }

        [Fact]
        public void AmbientRenderIsMonochrome()
        {
            var tree = Render("fixture-sections", P("theme", P("name", "ambient")));
            foreach (var n in Nodes(tree))
            {
                if (Json.S(n, "type") == "image" && !(Json.S(n, "src") ?? "").StartsWith("data:image/svg")) continue;
                foreach (var k in new[] { "bg", "color" })
                    if (Json.S(n, k) is string c && c != "#000000ff") Assert.Equal("#ffffff", Rgb(c));
            }
        }
    }
}
