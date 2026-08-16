using System.Buffers.Binary;

namespace ADBControl.Desktop.Services;

public static class ScrcpyControlMessageEncoder
{
    private const ulong GenericFingerPointerId = ulong.MaxValue - 1;

    public static byte[] EncodeTouch(int action, ProjectionTouchPosition position, uint pointerId)
    {
        if (action is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(action));
        if (!position.Space.IsValid || position.Space.Width > ushort.MaxValue || position.Space.Height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(position), "scrcpy 视频坐标空间无效。");
        // Coordinates and advertised video size are one protocol snapshot; clamping would hide stale-space bugs.
        if (position.X < 0 || position.X >= position.Space.Width)
            throw new ArgumentOutOfRangeException(nameof(position), "scrcpy 触控 X 坐标超出视频范围。");
        if (position.Y < 0 || position.Y >= position.Space.Height)
            throw new ArgumentOutOfRangeException(nameof(position), "scrcpy 触控 Y 坐标超出视频范围。");

        var message = new byte[32];
        message[0] = 2;
        message[1] = checked((byte)action);
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2, 8), GenericFingerPointerId - pointerId);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10, 4), position.X);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14, 4), position.Y);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18, 2), checked((ushort)position.Space.Width));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20, 2), checked((ushort)position.Space.Height));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22, 2), action == 1 ? (ushort)0 : ushort.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(24, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(28, 4), 0);
        return message;
    }
}