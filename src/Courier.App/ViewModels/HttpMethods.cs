namespace Courier.App.ViewModels;

/// <summary>The method combo box's fixed item list. CORE-01 allows an arbitrary method too, but these cover every common one.</summary>
public static class HttpMethods
{
    public static readonly string[] All = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
}
