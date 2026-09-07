using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace Courier.App.Controls;

/// <summary>Route parameters, as a plain 24px-row table. Values only; names are fixed by the URL.</summary>
public sealed partial class PathParamGrid : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<PathParamGrid, IEnumerable?>(nameof(ItemsSource));

    public PathParamGrid()
    {
        InitializeComponent();

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
