using System.Reflection;

namespace Medical.Core;

/// <summary>Product information, credit line and the clinical-use disclaimer shown by every Medical.Net app.</summary>
public static class MedicalInfo
{
    public const string Name = "Medical.Net";
    public const string Tagline = "Comprehensive Medical Platform for .NET";
    public const string TaglineId = "Platform Medis Komprehensif untuk .NET";
    public const string Credit = "Made by Gravicode Studios, led by Kang Fadhil";
    public const string CreditId = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";
    public const string Repository = "https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/Medical.Net";

    public const string Disclaimer =
        "Medical.Net is a software toolkit, not a certified medical device. Clinical scores, rules and AI outputs support — never replace — the judgement of a qualified clinician.";

    public const string DisclaimerId =
        "Medical.Net adalah perangkat lunak pengembang, bukan alat kesehatan bersertifikat. Skor klinis, aturan, dan keluaran AI membantu — bukan menggantikan — penilaian tenaga medis yang berwenang.";

    public static string Version { get; } =
        typeof(MedicalInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string CreditFor(string lang) => lang == "id" ? CreditId : Credit;
    public static string DisclaimerFor(string lang) => lang == "id" ? DisclaimerId : Disclaimer;
}

/// <summary>Argument validation helpers.</summary>
public static class Guard
{
    public static string NotEmpty(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} must not be empty.", name) : value;

    public static double InRange(double value, double min, double max, string name) =>
        double.IsNaN(value) || value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"{name} must be within [{min}, {max}].") : value;

    public static double Positive(double value, string name) =>
        !(value > 0) ? throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.") : value;
}
