namespace Blazor.AI.MediaPipe;

/// <summary>A sample picture offered by <see cref="CameraViewer"/> so components work without a webcam.</summary>
public sealed record VisionSample(string Name, string Url);

/// <summary>Sample photos shipped in the package (generated with Nano Banana 2 for Blazor.AI).</summary>
public static class VisionSamples
{
    private const string Root = "_content/Blazor.AI.MediaPipe/samples/";

    public static readonly VisionSample Face = new("Portrait", Root + "face.jpg");
    public static readonly VisionSample ThumbsUp = new("Thumbs up", Root + "thumbs-up.jpg");
    public static readonly VisionSample Yoga = new("Yoga pose", Root + "yoga.jpg");
    public static readonly VisionSample Street = new("Jakarta street", Root + "street.jpg");
    public static readonly VisionSample Food = new("Nasi goreng", Root + "food.jpg");

    public static readonly VisionSample OpenPalms = new("Open palms", Root + "open-palms.jpg");
    public static readonly VisionSample DeskPosture = new("Desk posture", Root + "desk-posture.jpg");
    public static readonly VisionSample Fall = new("Fall (staged)", Root + "fall.jpg");
    public static readonly VisionSample Construction = new("Construction site", Root + "construction.jpg");
    public static readonly VisionSample Warehouse = new("Warehouse", Root + "warehouse.jpg");
    public static readonly VisionSample Shelf = new("Store shelf", Root + "shelf.jpg");
    public static readonly VisionSample LeafDisease = new("Tomato leaf", Root + "leaf-disease.jpg");
    public static readonly VisionSample Fruit = new("Mangoes & bananas", Root + "fruit.jpg");
    public static readonly VisionSample Livestock = new("Livestock", Root + "livestock.jpg");
    public static readonly VisionSample Fish = new("Fish market", Root + "fish.jpg");

    public static IReadOnlyList<VisionSample> People => [Face, ThumbsUp, Yoga];

    public static IReadOnlyList<VisionSample> Hands => [ThumbsUp, OpenPalms, Face];

    public static IReadOnlyList<VisionSample> Bodies => [Yoga, DeskPosture, Fall, Construction];

    public static IReadOnlyList<VisionSample> Scenes => [Street, Warehouse, Shelf, Construction, Livestock];

    public static IReadOnlyList<VisionSample> Agritech => [LeafDisease, Fruit, Livestock, Fish];

    public static IReadOnlyList<VisionSample> All =>
        [Face, ThumbsUp, OpenPalms, Yoga, DeskPosture, Fall, Street, Construction, Warehouse, Shelf, Food, LeafDisease, Fruit, Livestock, Fish];

    public static VisionSample Named(string fileName, string title) => new(title, Root + fileName);
}
