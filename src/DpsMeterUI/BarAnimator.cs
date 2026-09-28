using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DpsMeterUI;

/// Attached property buat bar damage ala LOA Logs: bar selebar penuh baris,
/// lalu di-scale horizontal (0..1) dari kiri, dengan animasi 400ms ease-out
/// tiap nilainya berubah. Nggak tergantung lebar jendela, jadi aman di-resize.
public static class BarAnimator
{
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(BarAnimator), new PropertyMetadata(0.0, OnScaleChanged));

    public static double GetScale(DependencyObject o) => (double)o.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject o, double v) => o.SetValue(ScaleProperty, v);

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(0, 1);
            el.RenderTransform = st;
            el.RenderTransformOrigin = new Point(0, 0.5);
        }
        double to = Math.Clamp((double)e.NewValue, 0, 1);
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ease });
    }
}
