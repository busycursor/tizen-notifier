using System;
using System.IO;
using Xunit;

namespace TizenNotifier
{
    public class GoldenTests
    {
        public static readonly object[][] Names = { new object[] { "fixture-sections" }, new object[] { "fixture-masonry" }, new object[] { "fixture-conditional" } };

        private static string Golden(string f) => Path.Combine(Fixtures.Dir("golden"), f + ".json");

        [Theory, MemberData(nameof(Names))]
        public void DefaultUnchanged(string f)
        {
            var tree = Json.Write(Fixtures.Render(f));
            if (Environment.GetEnvironmentVariable("GOLDEN_WRITE") == "1") File.WriteAllText(Golden(f), tree);
            Assert.Equal(File.ReadAllText(Golden(f)), tree);
        }
    }
}
