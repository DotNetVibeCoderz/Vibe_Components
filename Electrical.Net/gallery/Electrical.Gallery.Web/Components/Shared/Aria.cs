namespace Electrical.Gallery.Web.Components.Shared;

/// <summary>Blazor renders bool attributes as present/absent; ARIA state attributes need the literal strings "true"/"false".</summary>
public static class Aria
{
    public static string B(bool value) => value ? "true" : "false";
}
