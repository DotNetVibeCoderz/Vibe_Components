using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.MediaPipe;

/// <summary>Five basic expressions derived from MediaPipe's 52 ARKit-style blendshapes (each 0–1).</summary>
public sealed record FaceExpression(double Smile, double Angry, double Sad, double Surprised, double Neutral)
{
    public static readonly string[] Names = ["Smile", "Angry", "Sad", "Surprised", "Neutral"];

    public double this[int i] => i switch { 0 => Smile, 1 => Angry, 2 => Sad, 3 => Surprised, _ => Neutral };

    /// <summary>The strongest expression.</summary>
    public string Dominant => Names[Enumerable.Range(0, 5).MaxBy(i => this[i])];

    public static FaceExpression Empty { get; } = new(0, 0, 0, 0, 1);
}

/// <summary>
/// Maps blendshapes to expressions. Formulas (all clamped to 0–1):
/// <list type="bullet">
/// <item>Smile = mean(mouthSmileLeft, mouthSmileRight)</item>
/// <item>Angry = 0.6·mean(browDownLeft, browDownRight) + 0.2·mean(mouthPressLeft, mouthPressRight) + 0.2·mean(noseSneerLeft, noseSneerRight)</item>
/// <item>Sad = 0.6·mean(mouthFrownLeft, mouthFrownRight) + 0.4·browInnerUp·(1 − Smile)</item>
/// <item>Surprised = (browInnerUp + mean(eyeWideLeft, eyeWideRight) + jawOpen) / 3, scaled ×1.4</item>
/// <item>Neutral = 1 − max(Smile, Angry, Sad, Surprised)</item>
/// </list>
/// Thresholds were tuned on frontal faces; treat the values as relative signals, not diagnoses.
/// </summary>
public static class FaceExpressions
{
    public static FaceExpression From(FaceLandmarks face)
    {
        double B(string name) => face.Blendshapes is null ? 0 : face.GetBlendshape(name);
        double Mean(string a, string b) => (B(a) + B(b)) / 2;
        static double C(double v) => Math.Clamp(v, 0, 1);

        var smile = C(Mean("mouthSmileLeft", "mouthSmileRight"));
        var angry = C(0.6 * Mean("browDownLeft", "browDownRight") + 0.2 * Mean("mouthPressLeft", "mouthPressRight") + 0.2 * Mean("noseSneerLeft", "noseSneerRight"));
        var sad = C(0.6 * Mean("mouthFrownLeft", "mouthFrownRight") + 0.4 * B("browInnerUp") * (1 - smile));
        var surprised = C((B("browInnerUp") + Mean("eyeWideLeft", "eyeWideRight") + B("jawOpen")) / 3 * 1.4);
        var neutral = C(1 - Math.Max(Math.Max(smile, angry), Math.Max(sad, surprised)));
        return new FaceExpression(smile, angry, sad, surprised, neutral);
    }
}

/// <summary>Optional demographic attributes for a face.</summary>
public sealed record FaceAttributes(string? AgeRange, string? Gender, double Confidence);

/// <summary>
/// Integration point for age/gender estimation in <see cref="FaceAnalyticsDashboard"/>. MediaPipe has no such model, so
/// there is no default: plug in your own (an ONNX model, a Hugging Face classifier, a cloud API) and respect local law on
/// demographic inference.
/// </summary>
public interface IFaceAttributeEstimator
{
    FaceAttributes? Estimate(VisionFrame frame, FaceLandmarks face);
}
