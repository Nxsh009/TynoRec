using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TynoRec
{
    // Petits effets d'interface : transition entre onglets, apparition de la fenêtre, pastille d'état qui pulse.
    public partial class MainWindow
    {
        string dotFxState = "";

        // le contenu d'un onglet glisse légèrement vers le haut en apparaissant
        void Enter(FrameworkElement p)
        {
            try
            {
                var tt = new TranslateTransform(0, 0); p.RenderTransform = tt;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            }
            catch { }
        }

        // apparition en fondu au lancement
        void IntroFx()
        {
            try
            {
                if (Content is UIElement root)
                    root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            }
            catch { }
        }

        // pastille d'état : halo coloré + pulsation douce tant que la capture est active
        void DotFx(string state, Color c)
        {
            if (state == dotFxState) return;
            dotFxState = state;
            try
            {
                if (state == "off") { dot.BeginAnimation(OpacityProperty, null); dot.Effect = null; return; }
                dot.Effect = new DropShadowEffect { Color = c, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 };
                var pulse = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(state == "rec" ? 600 : 1000)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
                Timeline.SetDesiredFrameRate(pulse, 12);   // une animation infinie force un rendu en continu : à 12 i/s la pulsation reste douce et coûte ~5x moins
                dot.BeginAnimation(OpacityProperty, pulse);
            }
            catch { }
        }
    }
}
