using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>A static fingerspelling letter described by which fingers are extended (thumb, index, middle, ring, pinky).</summary>
public sealed record SignLetter(string Letter, bool[] Fingers, string Hint, Func<HandLandmarks, double>? Extra = null);

/// <summary>
/// A small, static subset of the ASL/BISINDO-style fingerspelling alphabet that can be told apart from finger states
/// (letters with motion such as J and Z are left out). Learners can record their own reference shapes to refine matching.
/// </summary>
public static class SignAlphabet
{
    public static readonly IReadOnlyList<SignLetter> Letters =
    [
        new("A", [true, false, false, false, false], "Fist with the thumb along the side", h => Near(h, 4, 6) ? 1 : 0.6),
        new("B", [false, true, true, true, true], "Four fingers up together, thumb across the palm"),
        new("D", [false, true, false, false, false], "Index up, other fingers touch the thumb"),
        new("I", [false, false, false, false, true], "Only the little finger up"),
        new("L", [true, true, false, false, false], "Thumb and index make an L", h => Perpendicular(h, 4, 2, 8, 5)),
        new("V", [false, true, true, false, false], "Index and middle up in a V", h => Spread(h, 8, 12)),
        new("W", [false, true, true, true, false], "Index, middle and ring up"),
        new("Y", [true, false, false, false, true], "Thumb and little finger out"),
        new("5", [true, true, true, true, true], "Open hand, fingers spread", h => Spread(h, 8, 20)),
    ];

    public static SignLetter Get(string letter) => Letters.First(l => l.Letter == letter);

    /// <summary>Similarity 0–1 between a hand and a letter: finger-state agreement, refined by the letter's extra check.</summary>
    public static double Match(HandLandmarks hand, SignLetter letter, IReadOnlyList<float[]>? references = null)
    {
        var f = VisionMath.ExtendedFingers(hand);
        var agree = 0.0;
        for (var i = 0; i < 5; i++)
        {
            agree += f[i] == letter.Fingers[i] ? (i == 0 ? 0.6 : 1.1) : 0;
        }

        var score = agree / 5.0;
        if (letter.Extra is not null)
        {
            score = score * 0.8 + letter.Extra(hand) * 0.2;
        }

        if (references is { Count: > 0 })
        {
            var shape = VisionSolutions.HandShape(hand);
            var nearest = references.Min(r => VisionSolutions.ShapeDistance(r, shape));
            score = score * 0.4 + Math.Clamp(1 - nearest / 0.6, 0, 1) * 0.6;
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>The best-matching letter for a hand.</summary>
    public static (SignLetter Letter, double Score) Recognize(HandLandmarks hand, IReadOnlyDictionary<string, List<float[]>>? references = null) =>
        Letters.Select(l => (l, Match(hand, l, references?.GetValueOrDefault(l.Letter)))).MaxBy(x => x.Item2);

    private static bool Near(HandLandmarks h, int a, int b) => VisionMath.Distance(h.Landmarks[a], h.Landmarks[b]) < VisionMath.Distance(h.Landmarks[0], h.Landmarks[9]) * 0.6;

    private static double Spread(HandLandmarks h, int a, int b) =>
        Math.Clamp(VisionMath.Distance(h.Landmarks[a], h.Landmarks[b]) / Math.Max(1e-6, VisionMath.Distance(h.Landmarks[0], h.Landmarks[9])) * 1.4, 0, 1);

    private static double Perpendicular(HandLandmarks h, int a1, int a0, int b1, int b0)
    {
        var l = h.Landmarks;
        var (ax, ay) = (l[a1].X - l[a0].X, l[a1].Y - l[a0].Y);
        var (bx, by) = (l[b1].X - l[b0].X, l[b1].Y - l[b0].Y);
        var cos = (ax * bx + ay * by) / Math.Max(1e-6, Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by));
        return 1 - Math.Abs(cos);
    }
}
