using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dispatch
{
    /// <summary>
    /// Small, quick animations. Everything is skipped when Windows' "Show animations" setting is off.
    /// </summary>
    public static class Motion
    {
        public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

        public static bool Enabled => SystemParameters.ClientAreaAnimation;

        public static void Fade(UIElement element, double from, double to, int ms, Action done = null)
        {
            if (!Enabled)
            {
                element.Opacity = to;
                done?.Invoke();
                return;
            }

            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
            if (done != null)
            {
                a.Completed += (s, e) => done();
            }

            element.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>Grows or shrinks an element's layout height (via a vertical scale), moving what's below.</summary>
        public static void ScaleHeight(FrameworkElement element, double from, double to, int ms, Action done = null)
        {
            var scale = element.LayoutTransform as ScaleTransform;
            if (scale == null || scale.IsFrozen)
            {
                scale = new ScaleTransform(1, from);
                element.LayoutTransform = scale;
            }

            if (!Enabled)
            {
                scale.ScaleY = to;
                done?.Invoke();
                return;
            }

            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
            a.Completed += (s, e) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = to;
                done?.Invoke();
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        /// <summary>Slides an element from a vertical offset back to its place.</summary>
        public static void SlideFrom(UIElement element, double offsetY, int ms)
        {
            if (!Enabled || Math.Abs(offsetY) < 1)
            {
                return;
            }

            var shift = element.RenderTransform as TranslateTransform;
            if (shift == null || shift.IsFrozen)
            {
                shift = new TranslateTransform();
                element.RenderTransform = shift;
            }

            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
        }

        // ---------- AnimateChanges: a value that changes in place briefly dims and comes back ----------

        public static readonly DependencyProperty AnimateChangesProperty = DependencyProperty.RegisterAttached(
            "AnimateChanges", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateChangesChanged));

        private static readonly DependencyProperty QuietUntilProperty = DependencyProperty.RegisterAttached(
            "QuietUntil", typeof(DateTime), typeof(Motion), new PropertyMetadata(DateTime.MinValue));

        public static bool GetAnimateChanges(DependencyObject d) => (bool)d.GetValue(AnimateChangesProperty);

        public static void SetAnimateChanges(DependencyObject d, bool value) => d.SetValue(AnimateChangesProperty, value);

        private static void OnAnimateChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock text) || !(bool)e.NewValue)
            {
                return;
            }

            // A row scrolled into view or reused for another server is new content, not a change.
            text.DataContextChanged += (s, a) => text.SetValue(QuietUntilProperty, DateTime.UtcNow.AddMilliseconds(150));
            text.Loaded += (s, a) => text.SetValue(QuietUntilProperty, DateTime.UtcNow.AddMilliseconds(150));
            text.AddHandler(Binding.TargetUpdatedEvent, new EventHandler<DataTransferEventArgs>((s, a) =>
            {
                if (a.Property == TextBlock.TextProperty && DateTime.UtcNow > (DateTime)text.GetValue(QuietUntilProperty))
                {
                    Fade(text, 0.25, 1, 450);
                }
            }));
        }

        private static T Freeze<T>(T freezable)
            where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
