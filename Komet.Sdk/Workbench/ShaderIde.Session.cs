using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// What the workbench holds only while it is open: the buffer view's GL objects, the pinned uniforms, the render settings
// changed this session. EndSession (closing the screen, leaving the world) gives every one of them back, so a closed
// workbench costs nothing.
internal sealed partial class ShaderIde
{
    private const int SnapshotFrames = 45; // a project switched off: frames until the vanilla programs draw (no signal)
    private const int SnapshotPatience = 120; // polls without a drawn preview before the snapshot gives up

    private int _snapCountdown = -1, _snapWaited;
    private bool _snapToggled;

    public BufferView Buffers { get; } = new();

    public bool TakingSnapshot => _snapCountdown >= 0;

    // IDevTool: the preview's texture this frame
    public int Picture(int sharp)
    {
        if (LiveModel) return sharp;

        if (Model3D.On)
        {
            var picture = ModelPicture(sharp);
            TestCapture();
            return picture;
        }

        try
        {
            Buffers.Inspect(Api);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            AddLog(L("buffer-inspect-failed", e.Message)); // a debug read never takes the game down
        }

        var texture = Buffers.Picture(Api, sharp);
        SnapshotStep();
        return texture;
    }

    // "Compare with vanilla": the project goes off for a moment, the shown target's picture is kept, it comes back on
    public void CompareWithVanilla()
    {
        if (Project is not { } project || _snapCountdown >= 0) return;

        Buffers.ForgetSnapshot();
        Buffers.Split = 0.5;
        _snapToggled = !Side.ShowsOriginal; // "Game draws: Vanilla" already: nothing to switch
        if (_snapToggled) SetProjectOn(project, false, false);

        (_snapCountdown, _snapWaited) = (_snapToggled ? SnapshotFrames : 1, 0);
        AddLog(L("buffer-snapshot-taking"));
    }

    // A left-click menu (the preview's target list): filled by the caller, shown at the place
    public void Dropdown(double x, double y, Action<ContextMenu> fill)
    {
        Context.Begin(x, y);
        fill(Context);
        _ = Context.End();
    }

    private void SnapshotStep()
    {
        if (_snapCountdown < 0 || --_snapCountdown > 0) return;

        Buffers.TakeSnapshot();
        SnapshotEnd();
    }

    private void SnapshotEnd()
    {
        _snapCountdown = -1;
        if (_snapToggled && Project is { } project) SetProjectOn(project, true, false);

        _snapToggled = false;
    }

    // Per poll: a snapshot whose preview stopped drawing (folded, scrolled away, covered) gives up and the project
    // comes back on
    private void SnapshotWatch()
    {
        if (_snapCountdown < 0 || Live is not null || ++_snapWaited < SnapshotPatience)
        {
            _snapWaited = Live is not null ? 0 : _snapWaited;
            return;
        }

        SnapshotEnd();
        Buffers.Split = -1;
        AddLog(L("buffer-snapshot-gave-up"));
    }

    private void EndSession()
    {
        if (_snapCountdown >= 0) SnapshotEnd();

        Buffers.Large = false;
        Buffers.Split = -1;
        Buffers.Dispose();
        Model3D.Dispose();
        _modelStamp = -1;
    }
}
