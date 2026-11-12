namespace AquaCulture.Core;

/// <summary>Product information and the attribution line shown in apps and docs.</summary>
public static class AquaInfo
{
    public const string Name = "AquaCulture.Net";
    public const string Version = "1.2.0";
    public const string Tagline = "The Ultimate Open Source Aquaculture Platform for .NET";
    public const string TaglineId = "Platform Akuakultur Open Source Terlengkap untuk .NET";
    public const string CreditEn = "Made by Gravicode Studios, led by Kang Fadhil";
    public const string CreditId = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";
    public const string ProjectUrl = "https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/AquaCulture.Net";

    /// <summary>Credit line in the requested language ("en" or "id").</summary>
    public static string Credit(string lang = "en") => lang == "id" ? CreditId : CreditEn;
}

/// <summary>Bilingual text (English / Bahasa Indonesia).</summary>
public readonly record struct Text2(string En, string Id)
{
    public string this[string lang] => lang == "id" ? Id : En;
    public override string ToString() => En;
}

/// <summary>Argument guards.</summary>
public static class Guard
{
    public static double Positive(double value, string name)
    {
        if (!(value > 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        return value;
    }

    public static double NonNegative(double value, string name)
    {
        if (!(value >= 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, value, "Must be a non-negative finite number.");
        return value;
    }

    public static double InRange(double value, double min, double max, string name)
    {
        if (double.IsNaN(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name, value, $"Must be in [{min}, {max}].");
        return value;
    }

    public static T NotNull<T>(T? value, string name) where T : class => value ?? throw new ArgumentNullException(name);
}
