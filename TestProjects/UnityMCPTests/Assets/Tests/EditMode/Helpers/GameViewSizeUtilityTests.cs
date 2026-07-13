using System.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnityTests.Editor.Helpers
{
    public class GameViewSizeUtilityTests
    {
        [Test]
        public void Available_And_ListSizes_NonEmpty()
        {
            Assert.IsTrue(GameViewSizeUtility.Available, "Game View size reflection should resolve in the editor.");
            var sizes = GameViewSizeUtility.ListSizes();
            Assert.IsNotEmpty(sizes, "The active Game View group should expose at least one size (Free Aspect).");
        }

        [Test]
        public void TryResolve_MatchesFixedResolution_ByDisplayText_And_ByWxH()
        {
            var sizes = GameViewSizeUtility.ListSizes();
            // Pick any concrete fixed-resolution preset (aspect-only entries report 0x0).
            var fixedSize = sizes.FirstOrDefault(s => s.Width > 0 && s.Height > 0);
            if (fixedSize.Width == 0)
            {
                Assert.Ignore("No fixed-resolution Game View preset available in this project's active group.");
            }

            Assert.IsTrue(
                GameViewSizeUtility.TryResolve(fixedSize.DisplayText, out var byText, out _, out var textErr),
                $"Exact display text should resolve: {textErr}");
            Assert.AreEqual(fixedSize.Index, byText.Index, "Display-text match should return the same preset.");

            string wxh = $"{fixedSize.Width}x{fixedSize.Height}";
            Assert.IsTrue(
                GameViewSizeUtility.TryResolve(wxh, out var byRes, out _, out var resErr),
                $"'{wxh}' should resolve: {resErr}");
            Assert.AreEqual(fixedSize.Width, byRes.Width);
            Assert.AreEqual(fixedSize.Height, byRes.Height);
        }

        [Test]
        public void TryResolve_UnknownSize_ReturnsError_WithAvailableList()
        {
            bool ok = GameViewSizeUtility.TryResolve("definitely-not-a-real-size-xyz", out _, out var available, out var error);
            Assert.IsFalse(ok, "An unknown size must not resolve.");
            Assert.IsFalse(string.IsNullOrEmpty(error), "A helpful error message is expected.");
            Assert.IsNotEmpty(available, "The available-sizes list should be returned for guidance.");
        }
    }
}
