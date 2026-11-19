using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// The object in the 3D view (an effect's portal, a shape) moves as in Blender: G grabs, R rotates, S scales; X, Y or Z
// then holds the change to that axis (Blender's: Z is up), the mouse sets the amount, a left click or Enter keeps it,
// Esc or a right click puts it back. Home resets the object. The scene around it never moves.
internal enum TransformMode
{
    None,
    Grab,
    Rotate,
    Scale
}

internal sealed partial class ModelView
{
    private int _axis = -1; // -1 free, 0 x, 1 up (Blender's Z), 2 depth (Blender's Y)
    private (Vector3 Position, Vector3 Rotation, Vector3 Scale) _before;
    private Vector3 _position, _rotation, _scale = Vector3.One;
    private double _startX, _startY;

    public TransformMode Transforming { get; private set; }

    // "X", "Y", "Z" or "" for the hint line
    public string Axis => _axis switch
    {
        0 => "X",
        1 => "Z",
        2 => "Y",
        _ => ""
    };

    // The object's matrix: scaled and turned about its foot, then moved
    private Matrix4x4 ObjectMatrix(Vector3 pivot) =>
        Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(_scale) *
        Matrix4x4.CreateFromYawPitchRoll(_rotation.Y, _rotation.X, _rotation.Z) *
        Matrix4x4.CreateTranslation(pivot + _position);

    public void Begin(TransformMode mode, double x, double y)
    {
        if (Transforming != TransformMode.None) Cancel();

        (Transforming, _axis, _startX, _startY) = (mode, -1, x, y);
        _before = (_position, _rotation, _scale);
    }

    // X, Y or Z while transforming: that axis only; the same key again frees it
    public void HoldAxis(int blenderAxis, double x, double y)
    {
        var axis = blenderAxis switch
        {
            0 => 0,
            1 => 2,
            _ => 1
        };
        _axis = _axis == axis ? -1 : axis;
        Move(x, y);
    }

    // The mouse while transforming: the change from where it began
    public void Move(double x, double y)
    {
        if (Transforming == TransformMode.None) return;

        (_position, _rotation, _scale) = _before;
        var (dx, dy) = ((float)(x - _startX), (float)(y - _startY));
        switch (Transforming)
        {
            case TransformMode.Grab:
                _position += Grabbed(dx, dy);
                break;
            case TransformMode.Rotate:
                var angle = dx * 0.01f;
                _rotation += _axis switch
                {
                    0 => new Vector3(angle, 0, 0),
                    2 => new Vector3(0, 0, angle),
                    _ => new Vector3(0, angle, 0)
                };
                break;
            case TransformMode.Scale:
                var factor = MathF.Exp((dx - dy) * 0.005f);
                _scale *= _axis switch
                {
                    0 => new Vector3(factor, 1, 1),
                    1 => new Vector3(1, factor, 1),
                    2 => new Vector3(1, 1, factor),
                    _ => new Vector3(factor)
                };
                _scale = Vector3.Clamp(_scale, new Vector3(0.05f), new Vector3(20f));
                break;
        }
    }

    // Screen pixels to the world: along the camera's right and up, scaled by its distance; an axis keeps its part only
    private Vector3 Grabbed(float dx, float dy)
    {
        var step = _distance * 0.0025f;
        var right = new Vector3(MathF.Cos(_yaw), 0, -MathF.Sin(_yaw));
        var up = new Vector3(-MathF.Sin(_pitch) * MathF.Sin(_yaw), MathF.Cos(_pitch),
            -MathF.Sin(_pitch) * MathF.Cos(_yaw));
        var move = (right * dx - up * dy) * step;
        return _axis switch
        {
            0 => new Vector3(move.X, 0, 0),
            1 => new Vector3(0, move.Y, 0),
            2 => new Vector3(0, 0, move.Z),
            _ => move
        };
    }

    public void Confirm() => Transforming = TransformMode.None;

    public void Cancel()
    {
        (_position, _rotation, _scale) = _before;
        Transforming = TransformMode.None;
    }

    // The last picture's pixels (RGBA, first row on top: the picture is stored upside down for the screen), for the
    // shader test; null while there is none. Render thread.
    public (byte[] Rgba, int Width, int Height)? Read()
    {
        if (_picture == 0 || _height <= 0) return null;

        var pixels = new byte[Width * _height * 4];
        GL.GetInteger(GetPName.TextureBinding2D, out var bound);
        GL.BindTexture(TextureTarget.Texture2D, _picture);
        GL.GetTexImage(TextureTarget.Texture2D, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.BindTexture(TextureTarget.Texture2D, bound);
        return (pixels, Width, _height);
    }

    public void ResetObject()
    {
        (_position, _rotation, _scale) = (Vector3.Zero, Vector3.Zero, Vector3.One);
        Transforming = TransformMode.None;
    }
}
