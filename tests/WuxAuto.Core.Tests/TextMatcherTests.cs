using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

public class TextMatcherTests
{
    static IReadOnlyList<OcrWord> Words(string text) =>
        [new OcrWord(text, new PixelRect(0, 0, 10, 10), text, new PixelRect(0, 0, 10, 10))];

    [Fact] public void Levenshtein_Classic() => Assert.Equal(3, TextMatcher.Levenshtein("kitten", "sitting"));
    [Fact] public void FindBest_MatchesDestinationInsideBrackets() => Assert.NotNull(TextMatcher.FindBest(Words("[The Labyrinth (Adept)]"), "Labyrinth", 0.72));
    [Fact] public void FindBest_ToleratesDroppedLetter() => Assert.NotNull(TextMatcher.FindBest(Words("SanctifiedUnicor"), "Sanctified Unicorn", 0.72));
    [Fact] public void Similarity_ShortPieceGetsNoContainmentBonus() => Assert.True(TextMatcher.Similarity("Rag", "Ragesoul Heathen") < 0.72);
}
