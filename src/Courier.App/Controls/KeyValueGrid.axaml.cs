using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace Courier.App.Controls;

/// <summary>Params and headers, as a plain 24px-row table. CORE-01.</summary>
public sealed partial class KeyValueGrid : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<KeyValueGrid, IEnumerable?>(nameof(ItemsSource));

    public KeyValueGrid()
    {
        InitializeComponent();

        // Forwarded rather than bound in XAML so the control exposes one obvious property.
        this.GetObservable(ItemsSourceProperty).Subscribe(new AnonymousObserver(value => Rows.ItemsSource = value));
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private sealed class AnonymousObserver(Action<IEnumerable?> onNext) : IObserver<IEnumerable?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(IEnumerable? value) => onNext(value);
    }
}
