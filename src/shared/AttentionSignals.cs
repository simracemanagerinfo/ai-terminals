using System.Text;

namespace ClaudeTerminal.Host;

/// <summary>
/// Codex emits terminal-native OSC 9 notifications for blocking questions
/// and approvals. Its per-process TUI settings select only these events.
/// Consume complete OSC sequences across arbitrary ConPTY read boundaries;
/// do not infer questions from ordinary screen text or BEL alone.
/// </summary>
public sealed class AttentionSignals
{
    private readonly StringBuilder _osc = new();
    private int _state;
    private bool _overflow;

    public int Read(string chunk)
    {
        var requests = 0;
        foreach (var ch in chunk)
        {
            switch (_state)
            {
                case 0:
                    if (ch == '\x1b') _state = 1;
                    else if (ch == '\u009d') StartOsc();
                    break;
                case 1:
                    if (ch == ']') StartOsc();
                    else _state = ch == '\x1b' ? 1 : 0;
                    break;
                case 2:
                    if (ch is '\a' or '\u009c')
                    {
                        if (FinishOsc()) requests++;
                    }
                    else if (ch == '\x1b') _state = 3;
                    else Append(ch);
                    break;
                case 3:
                    if (ch == '\\')
                    {
                        if (FinishOsc()) requests++;
                    }
                    else if (ch == ']') StartOsc();
                    else
                    {
                        _osc.Clear();
                        _state = ch == '\x1b' ? 1 : 0;
                    }
                    break;
            }
        }
        return requests;
    }

    private void StartOsc()
    {
        _osc.Clear();
        _overflow = false;
        _state = 2;
    }

    private void Append(char ch)
    {
        if (_osc.Length < 8192) _osc.Append(ch);
        else _overflow = true;
    }

    private bool FinishOsc()
    {
        var payload = _osc.ToString();
        _state = 0;
        _osc.Clear();
        if (_overflow || !payload.StartsWith("9;", StringComparison.Ordinal))
            return false;
        var message = payload[2..].Trim();
        // OSC 9;4;... is the Windows Terminal progress protocol, not
        // a notification. Other numeric subcommands are not alerts either.
        var separator = message.IndexOf(';');
        if (separator >= 0 && int.TryParse(message[..separator], out _))
            return false;
        return message.Length > 0;
    }

    // No question text is persisted or forwarded into a Windows popup.
    public static bool IsAttentionMarker(string marker)
        => marker.StartsWith("input:", StringComparison.Ordinal) &&
           Guid.TryParseExact(marker[6..], "N", out _);

    public static bool IsResponseInput(string data)
        => data.Contains('\r') || data.Contains('\n') || data.Contains('\x03');
}
