using XBear.Core.Diagnostics;

namespace XBear.Core.Qmp;

/// <summary>QMP 消息分帧器：按 JSON 对象的括号配对把 TCP 字节流切分成一条条完整消息。</summary>
/// <remarks>
/// 切出的消息是内部缓冲区的视图，调用方必须在再次写入前完成解析。
/// </remarks>
internal sealed class QmpFrameReader
{
    private byte[] _buffer;
    private int _start;
    private int _end;

    /// <summary>创建分帧器。</summary>
    /// <param name="initialCapacity">初始缓冲容量。</param>
    public QmpFrameReader(int initialCapacity = 8192)
    {
        _buffer = new byte[initialCapacity];
    }

    /// <summary>把新读到的字节追加到缓冲区。</summary>
    /// <param name="data">追加的字节。</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>尝试切出一条完整消息，未收全时返回 false。</summary>
    /// <param name="message">切出的消息字节，未收全时为默认值。</param>
    /// <returns>缓冲区中已有完整 JSON 对象时返回 true。</returns>
    /// <exception cref="XBearException">缓冲区首字节不是 JSON 对象起始时抛出。</exception>
    public bool TryTakeMessage(out ReadOnlyMemory<byte> message)
    {
        int start = SkipWhitespace();
        if (start == _end)
        {
            _start = start;
            message = default;
            return false;
        }

        if (_buffer[start] != (byte)'{')
        {
            throw new XBearException(ErrorCategory.Protocol, "QMP 消息不是以 JSON 对象起始。");
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int index = start; index < _end; index++)
        {
            byte current = _buffer[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == (byte)'\\')
                {
                    escaped = true;
                }
                else if (current == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    if (depth == 0)
                    {
                        message = new ReadOnlyMemory<byte>(_buffer, start, index - start + 1);
                        _start = index + 1;
                        return true;
                    }

                    break;
                default:
                    break;
            }
        }

        _start = start;
        message = default;
        return false;
    }

    private int SkipWhitespace()
    {
        int index = _start;
        while (index < _end)
        {
            byte current = _buffer[index];
            if (current != (byte)' ' && current != (byte)'\t' && current != (byte)'\r' && current != (byte)'\n')
            {
                break;
            }

            index++;
        }

        return index;
    }

    private void EnsureCapacity(int additional)
    {
        if (_buffer.Length - _end >= additional)
        {
            return;
        }

        int used = _end - _start;
        if (_start > 0 && used + additional <= _buffer.Length)
        {
            Array.Copy(_buffer, _start, _buffer, 0, used);
            _start = 0;
            _end = used;
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < used + additional)
        {
            capacity *= 2;
        }

        var grown = new byte[capacity];
        Array.Copy(_buffer, _start, grown, 0, used);
        _buffer = grown;
        _start = 0;
        _end = used;
    }
}