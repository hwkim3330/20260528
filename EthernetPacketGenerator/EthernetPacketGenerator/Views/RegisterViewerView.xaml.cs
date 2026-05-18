using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class RegisterViewerView : UserControl
{
    public RegisterViewerView() => InitializeComponent();

    private RegisterViewerViewModel? VM => DataContext as RegisterViewerViewModel;

    // ── Reset 확인 다이얼로그 ────────────────────────────────────────────
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Window.GetWindow(this),
            "모든 레지스터를 초기값으로 복원합니다.\n\n계속 하시겠습니까?",
            "레지스터 초기화 (Reset)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            VM?.SysCtrl.LoadDefaultsCommand.Execute(null);
    }

    // ── TOC 네비게이션 ────────────────────────────────────────────────────
    private void Toc_SysCtrl(object s,   RoutedEventArgs e) => FlashSection(NavSysCtrlSection);
    private void Toc_Version(object s,   RoutedEventArgs e) => Flash(NavVersion);
    private void Toc_System(object s,    RoutedEventArgs e) => Flash(NavSystem);
    private void Toc_Ahb(object s,       RoutedEventArgs e) => Flash(NavAhb);

    private void Toc_Interrupt(object s, RoutedEventArgs e) => FlashSection(NavInterruptSection);
    private void Toc_IntrCtrl(object s,  RoutedEventArgs e) => Flash(NavIntrCtrl);
    private void Toc_IntrRaw(object s,   RoutedEventArgs e) => Flash(NavIntrRaw);
    private void Toc_IntrMask(object s,  RoutedEventArgs e) => Flash(NavIntrMask);
    private void Toc_IntrSw(object s,    RoutedEventArgs e) => Flash(NavIntrSw);

    private void Toc_Timestamp(object s, RoutedEventArgs e) => FlashSection(NavTimestampSection);
    private void Toc_TsTime(object s,    RoutedEventArgs e) => Flash(NavTsTime);
    private void Toc_TsAdj(object s,     RoutedEventArgs e) => Flash(NavTsAdj);
    private void Toc_TsClk(object s,    RoutedEventArgs e) => Flash(NavTsClk);

    private void Toc_LedClock(object s, RoutedEventArgs e) => FlashSection(NavLedClockSection);
    private void Toc_Led(object s,       RoutedEventArgs e) => Flash(NavLed);
    private void Toc_ExtSw(object s,     RoutedEventArgs e) => Flash(NavExtSw);
    private void Toc_ClkLim(object s,    RoutedEventArgs e) => Flash(NavClkLim);

    private void Toc_Test(object s,      RoutedEventArgs e) => FlashSection(NavTestSection);

    private void Toc_Fdb(object s,        RoutedEventArgs e) => FlashSection(NavFdbSection);
    private void Toc_FdbControl(object s, RoutedEventArgs e) => Flash(NavFdbControl);
    private void Toc_FdbCommand(object s, RoutedEventArgs e) => Flash(NavFdbCommand);
    private void Toc_FdbResult(object s,  RoutedEventArgs e) => Flash(NavFdbResult);

    private void Toc_Count(object s, RoutedEventArgs e) => FlashSection(NavCountSection);

    private void Toc_Mdio(object s,      RoutedEventArgs e) => FlashSection(NavMdioSection);
    private void Toc_MdioSetup(object s, RoutedEventArgs e) => Flash(NavMdioSetup);
    private void Toc_MdioLink(object s,  RoutedEventArgs e) => Flash(NavMdioLink);
    private void Toc_MdioAcc(object s,   RoutedEventArgs e) => Flash(NavMdioAcc);

    // ── 섹션 상단 스크롤 + 전체 하이라이트 ──────────────────────────────
    private void FlashSection(Border section)
    {
        section.UpdateLayout();
        var pt = section.TranslatePoint(new Point(0, 0), MainScroller);
        MainScroller.ScrollToVerticalOffset(MainScroller.VerticalOffset + pt.Y);

        section.ClearValue(Border.BackgroundProperty);
        var brush = new SolidColorBrush(_flashColor);
        section.Background = brush;

        var anim = new ColorAnimation
        {
            From           = _flashColor,
            To             = _clearColor,
            Duration       = new Duration(TimeSpan.FromMilliseconds(900)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior   = FillBehavior.Stop
        };
        anim.Completed += (_, _) => section.ClearValue(Border.BackgroundProperty);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // ── 하이라이트 플래시 애니메이션 ─────────────────────────────────────
    private static readonly Color _flashColor = Color.FromArgb(120, 100, 160, 255);
    private static readonly Color _clearColor = Color.FromArgb(0,   100, 160, 255);

    private static void Flash(FrameworkElement element)
    {
        element.BringIntoView();

        DependencyProperty? bgProp = element switch
        {
            Border    => Border.BackgroundProperty,
            TextBlock => TextBlock.BackgroundProperty,
            _         => null
        };
        if (bgProp == null) return;

        element.ClearValue(bgProp);
        var brush = new SolidColorBrush(_flashColor);
        element.SetValue(bgProp, brush);

        var anim = new ColorAnimation
        {
            From           = _flashColor,
            To             = _clearColor,
            Duration       = new Duration(TimeSpan.FromMilliseconds(900)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior   = FillBehavior.Stop
        };
        anim.Completed += (_, _) => element.ClearValue(bgProp);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
