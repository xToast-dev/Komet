namespace Komet.Hud;

// A yes/no question in the HUD's look (the menu's band, logo and spaced title under a tabs style), centred, in place of
// the game's GuiDialogConfirm. Escape or the cross answer no; the answer is given once.
internal sealed class HudAsk : HudDialog
{
    private const double MaxWidth = 460, MaxHeight = 260;
    private const float Depth = 70;

    private readonly string _title, _text, _yes, _no;
    private Action<bool>? _answer;

    public HudAsk(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, (string Title, string Text) ask,
        Action<bool> answer) : base(capi, settings, fonts, snap, Depth)
    {
        (_title, _text, _answer) = (ask.Title, ask.Text, answer);
        (_yes, _no) = (Lang.Get("Yes"), Lang.Get("No"));
    }

    public override double DrawOrder => 0.95;

    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !IsOpened()) return;
        var size = Fitted(MaxWidth, MaxHeight);
        if (!Assert(size.W > 0 && size.H > 0)) return;
        if (Recompose(size, 1000)) Compose(size);
        if (!Canvas.Ready) return;
        Present(double.NaN, double.NaN);
        Hover();
    }

    private void Compose((double W, double H) size)
    {
        HudUi.Ground(Canvas, size.W, size.H, HudUi.Back, scaled(4));
        var ui = new HudUi(Canvas, Fonts, (scaled(14), size.W - scaled(28), 0, size.H, 0), Hits);
        var head = Fonts.StrongRow + ui.Px(16);
        if (HudUi.Framed) ui.Fill(0, 0, size.W, head, HudUi.Band);
        var title = _title.ToUpperInvariant();
        if (HudUi.Framed) _ = ui.Spaced(ui.X, ui.Px(8), Fonts.StrongRow, Fonts.Strong, title, ui.Px(2.5));
        else ui.Text(ui.X, ui.Px(8), Fonts.StrongRow, Fonts.Strong, _title);
        var cross = ui.Px(10);
        ui.Cross(size.W - ui.Px(12) - cross, (head - cross) / 2, cross, Rgba.White(0.85));
        ui.Click(size.W - ui.Px(16) - cross, 0, cross + ui.Px(16), head, () => Answer(false));
        ui.Fill(0, head, size.W, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Y = head + ui.Px(12);
        ui.Wrap(_text, HudUi.Soft);
        ui.Space(14);
        var no = ui.TextW(Fonts.Strong, _no) + ui.Px(24);
        var yes = ui.TextW(Fonts.Strong, _yes) + ui.Px(24);
        var right = ui.X + ui.W;
        _ = ui.Go(right - yes, ui.Y, _yes, () => Answer(true));
        _ = ui.Go(right - yes - ui.Px(8) - no, ui.Y, _no, () => Answer(false));
        Canvas.End(Hits);
    }

    private void Answer(bool yes)
    {
        if (_answer is not { } answer) return;
        _answer = null; // once, whatever closes the dialog next
        answer(yes);
        _ = TryClose();
    }

    public override bool OnEscapePressed()
    {
        Answer(false);
        return base.OnEscapePressed();
    }

    protected override bool Grabs(double x, double y, EnumMouseButton button) => false;

    protected override void Moved(double x, double y)
    {
        // a question stays where it opened
    }

    protected override void Scrolled(double notches)
    {
        // nothing to scroll
    }
}
