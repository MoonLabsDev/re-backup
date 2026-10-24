using System.Text;

namespace ReBackup.Control;

/// <summary>A line exceeded <see cref="BoundedLineReader.MaxLineBytes"/>.</summary>
internal sealed class LineTooLongException() : IOException("A control message exceeded the maximum line size.");

/// <summary>Reads '\n'-terminated UTF-8 lines from a stream and refuses lines longer than the limit.</summary>
internal sealed class BoundedLineReader(Stream stream, int maxLineBytes = BoundedLineReader.MaxLineBytes)
{
    public const int MaxLineBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly byte[] _buffer = new byte[4096];
    private readonly MemoryStream _line = new();
    private int _pos;
    private int _len;

    /// <summary>Returns the next line, or null at end of stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_pos == _len)
            {
                _len = await stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                {
                    if (_line.Length == 0) return null;
                    return TakeLine(); // unterminated last line
                }
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _pos, _len - _pos);
            var take = (newline < 0 ? _len : newline) - _pos;
            if (_line.Length + take > maxLineBytes) throw new LineTooLongException();
            _line.Write(_buffer, _pos, take);
            _pos += take;
            if (newline >= 0)
            {
                _pos++; // skip '\n'
                return TakeLine();
            }
        }
    }

    private string TakeLine()
    {
        var text = Utf8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
        _line.SetLength(0);
        return text.TrimEnd('\r');
    }
}
