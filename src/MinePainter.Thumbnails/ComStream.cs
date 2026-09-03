using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MinePainter.Thumbnails;

/// <summary>
/// 外殼傳進來的 IStream。方法順序＝vtable 順序，用不到的也得佔位。
/// </summary>
[GeneratedComInterface, Guid("0000000c-0000-0000-C000-000000000046")]
internal partial interface IStreamCom
{
    void Read(nint buffer, uint count, nint bytesRead);                 // 1
    void Write_Unused();                                                // 2
    void Seek(long move, uint origin, nint newPosition);                // 3
    void SetSize_Unused();                                              // 4
    void CopyTo_Unused();                                               // 5
    void Commit_Unused();                                               // 6
    void Revert_Unused();                                               // 7
    void LockRegion_Unused();                                           // 8
    void UnlockRegion_Unused();                                         // 9
    void Stat_Unused();                                                 // 10
    void Clone_Unused();                                                // 11
}

/// <summary>
/// 把 IStream 包成 .NET 的 Stream，ZipArchive 就能直接讀 —— 只會讀到中央目錄與
/// thumbnail.png 那一段，不必把整個 .mpp 讀進記憶體（專案檔可以很大）。
/// </summary>
internal sealed unsafe class ComStream : Stream
{
    private const uint StreamSeekSet = 0;
    private const uint StreamSeekCurrent = 1;
    private const uint StreamSeekEnd = 2;

    private readonly IStreamCom _stream;

    public ComStream(IStreamCom stream) => _stream = stream;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            var current = Position;
            var end = SeekCore(0, StreamSeekEnd);
            SeekCore(current, StreamSeekSet);
            return end;
        }
    }

    public override long Position
    {
        get => SeekCore(0, StreamSeekCurrent);
        set => SeekCore(value, StreamSeekSet);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        uint read = 0;
        fixed (byte* p = &buffer[offset])
        {
            _stream.Read((nint)p, (uint)count, (nint)(&read));
        }
        return (int)read;
    }

    public override long Seek(long offset, SeekOrigin origin) => SeekCore(offset, origin switch
    {
        SeekOrigin.Begin => StreamSeekSet,
        SeekOrigin.Current => StreamSeekCurrent,
        _ => StreamSeekEnd,
    });

    private long SeekCore(long move, uint origin)
    {
        ulong position = 0;
        _stream.Seek(move, origin, (nint)(&position));
        return (long)position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
