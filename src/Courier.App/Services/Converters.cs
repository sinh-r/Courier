using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Courier.App.ViewModels;
using Courier.Core.Collections;

namespace Courier.App.Services;

/// <summary>
/// Base for the small, single-purpose converters the views bind to.
/// </summary>
/// <remarks>
/// Declared as classes rather than static fields because an Avalonia resource dictionary
/// instantiates elements; a static field would need markup-extension syntax a dictionary entry
/// cannot use. The point of having them at all is that exactly one place maps a verb, a status or a
/// trust flag to a colour — UI_SPEC 3.1 only holds if that mapping is never duplicated inline.
/// </remarks>
public abstract class OneWayConverter<TIn, TOut> : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TIn input ? Convert(input) : Fallback;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{GetType().Name} is one-way.");

    protected abstract TOut Convert(TIn value);

    /// <summary>Used when the bound value is null or of an unexpected type.</summary>
    protected virtual object? Fallback => null;
}

/// <summary>HTTP verb to its badge colour.</summary>
public sealed class VerbBrushConverter : OneWayConverter<string, IBrush>
{
    protected override IBrush Convert(string value) => VerbBrushes.For(value);

    protected override object Fallback => VerbBrushes.For("OPTIONS");
}

/// <summary>Status code to its status-class colour.</summary>
public sealed class StatusBrushConverter : OneWayConverter<int, IBrush>
{
    protected override IBrush Convert(int value) => VerbBrushes.ForStatus(value);

    protected override object Fallback => VerbBrushes.ForStatus(0);
}

/// <summary>
/// Status code to its glyph. Colour never travels alone: UI_SPEC 3.3 requires every semantic colour
/// to be paired with a glyph or a label, for a colour-blind reader and for the greyscale screenshot
/// that ends up in a bug report.
/// </summary>
public sealed class StatusGlyphConverter : OneWayConverter<int, string>
{
    protected override string Convert(int value) =>
        VerbBrushes.StatusGlyph(value, transportFailed: value == 0);

    protected override object Fallback => "⊘";
}

/// <summary>True for a secret or local value; false for a shareable, committed one.</summary>
public sealed class TrustBrushConverter : OneWayConverter<bool, IBrush>
{
    protected override IBrush Convert(bool value) => VerbBrushes.ForTrust(value);
}

public sealed class ProvenanceGlyphConverter : OneWayConverter<Provenance, string>
{
    protected override string Convert(Provenance value) => VerbBrushes.ProvenanceGlyph(value);

    protected override object Fallback => string.Empty;
}

public sealed class ProvenanceLabelConverter : OneWayConverter<Provenance, string>
{
    protected override string Convert(Provenance value) => VerbBrushes.ProvenanceLabel(value);

    protected override object Fallback => string.Empty;
}

/// <summary>Byte count to "2.4 KB" or "40.2 MB", the form the response header uses.</summary>
public sealed class SizeConverter : OneWayConverter<long, string>
{
    protected override string Convert(long value) => ResponseViewModel.FormatSize(value);

    protected override object Fallback => "0 B";
}

/// <summary>A count to a visibility flag, so an empty group collapses rather than showing a zero.</summary>
public sealed class IsNotZeroConverter : OneWayConverter<int, bool>
{
    protected override bool Convert(int value) => value != 0;

    protected override object Fallback => false;
}

/// <summary>Timestamps in tables are local wall-clock, to the minute.</summary>
public sealed class ShortTimeConverter : OneWayConverter<DateTimeOffset, string>
{
    protected override string Convert(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    protected override object Fallback => string.Empty;
}

/// <summary>Elapsed time as the response header shows it: "184 ms" or "2.9 s".</summary>
public sealed class ElapsedConverter : OneWayConverter<TimeSpan, string>
{
    protected override string Convert(TimeSpan value) => value.TotalSeconds >= 1
        ? $"{value.TotalSeconds:0.#} s"
        : $"{(int)value.TotalMilliseconds} ms";

    protected override object Fallback => string.Empty;
}

/// <summary>True when history is placed as a fourth pane rather than in the inspector.</summary>
public sealed class HistoryIsBottomConverter : OneWayConverter<HistoryPlacement, bool>
{
    protected override bool Convert(HistoryPlacement value) => value == HistoryPlacement.BottomPane;

    protected override object Fallback => false;
}

/// <summary>The other half of the same choice. UI_SPEC 8 leaves the placement to the user.</summary>
public sealed class HistoryIsInInspectorConverter : OneWayConverter<HistoryPlacement, bool>
{
    protected override bool Convert(HistoryPlacement value) => value == HistoryPlacement.Inspector;

    protected override object Fallback => true;
}

/// <summary>
/// Matches the active dialog against a name, so the dialog host is a flat panel of siblings rather
/// than a switch in code-behind. Each surface is one line and the whole set is visible at a glance.
/// </summary>
public sealed class DialogIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DialogKind kind
        && parameter is string name
        && Enum.TryParse<DialogKind>(name, out var expected)
        && kind == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The settings surfaces share one shell with a left nav, exactly as the mock does: Environments,
/// Auth profiles, Trust and network, Storage, Keyboard.
/// </summary>
public sealed class DialogIsSettingsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DialogKind.Environments
            or DialogKind.AuthProfile
            or DialogKind.TrustAndNetwork
            or DialogKind.Storage
            or DialogKind.Keyboard
            or DialogKind.Appearance
            or DialogKind.DefaultHeaders;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
