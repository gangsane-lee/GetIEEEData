using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SemiconductorLoader.Behaviors;

/// <summary>
/// ListBox에 부착하면 아이템 추가 시 맨 아래 스크롤을 자동으로 예약한다.
/// DispatcherPriority.Background로 지연 실행해 ItemsGenerator 재진입 문제를 방지한다.
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;

        var items = (INotifyCollectionChanged)lb.Items;

        if ((bool)e.NewValue)
            items.CollectionChanged += Handler;
        else
            items.CollectionChanged -= Handler;

        void Handler(object? _, NotifyCollectionChangedEventArgs __)
            => lb.Dispatcher.BeginInvoke(
                () => { if (lb.Items.Count > 0) lb.ScrollIntoView(lb.Items[^1]); },
                DispatcherPriority.Background);
    }
}
