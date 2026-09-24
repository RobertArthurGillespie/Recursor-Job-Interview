using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public enum BridgeJsonKind
{
    String,
    Integer,
    NonIntegerNumber,
    Boolean,
    Object,
}

public sealed class BridgeJsonValue
{
    private BridgeJsonValue(BridgeJsonKind kind) => Kind = kind;

    public BridgeJsonKind Kind { get; }
    public string StringValue { get; private set; }
    public long IntegerValue { get; private set; }
    public bool BooleanValue { get; private set; }
    public IReadOnlyDictionary<string, BridgeJsonValue> ObjectValue { get; private set; }

    internal static BridgeJsonValue FromString(string value) =>
        new BridgeJsonValue(BridgeJsonKind.String) { StringValue = value };

    internal static BridgeJsonValue FromInteger(long value) =>
        new BridgeJsonValue(BridgeJsonKind.Integer) { IntegerValue = value };

    internal static BridgeJsonValue FromNonIntegerNumber() =>
        new BridgeJsonValue(BridgeJsonKind.NonIntegerNumber);

    internal static BridgeJsonValue FromBoolean(bool value) =>
        new BridgeJsonValue(BridgeJsonKind.Boolean) { BooleanValue = value };

    internal static BridgeJsonValue FromObject(IReadOnlyDictionary<string, BridgeJsonValue> value) =>
        new BridgeJsonValue(BridgeJsonKind.Object) { ObjectValue = value };
}

// A deliberately small, strict JSON reader and writer for the interview bridge.
// JsonUtility cannot tell a missing key from a default value or report an extra key, and
// the bridge must fail closed on both, so inbound messages are read here instead.
// Accepted: objects, strings, numbers, true/false. Rejected: arrays, null, duplicate keys,
// nesting deeper than an envelope plus its payload, trailing content, and oversized input.
// Parsing never throws and never reports input text back to the caller.
public static class InterviewBridgeJson
{
    private const int MaxDepth = 2;

    public static bool TryParseObject(
        string json,
        int maxLength,
        out IReadOnlyDictionary<string, BridgeJsonValue> result)
    {
        result = null;

        if (json == null || json.Length == 0 || json.Length > maxLength)
            return false;

        var reader = new Reader(json);

        try
        {
            reader.SkipWhitespace();
            if (!reader.TryReadObject(1, out var obj))
                return false;

            reader.SkipWhitespace();
            if (!reader.AtEnd)
                return false;

            result = obj;
            return true;
        }
        catch (Exception)
        {
            // Defensive: a reader bug must fail closed, never surface input.
            result = null;
            return false;
        }
    }

    public static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == '\u2028' || c == '\u2029')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        builder.Append('"');
    }

    private sealed class Reader
    {
        private readonly string text;
        private int position;

        public Reader(string text) => this.text = text;

        public bool AtEnd => position >= text.Length;

        public void SkipWhitespace()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    position++;
                else
                    break;
            }
        }

        public bool TryReadObject(int depth, out IReadOnlyDictionary<string, BridgeJsonValue> result)
        {
            result = null;

            if (depth > MaxDepth || !TryConsume('{'))
                return false;

            var members = new Dictionary<string, BridgeJsonValue>(StringComparer.Ordinal);
            SkipWhitespace();

            if (TryConsume('}'))
            {
                result = members;
                return true;
            }

            while (true)
            {
                SkipWhitespace();
                if (!TryReadString(out string key))
                    return false;

                SkipWhitespace();
                if (!TryConsume(':'))
                    return false;

                SkipWhitespace();
                if (!TryReadValue(depth, out BridgeJsonValue value))
                    return false;

                if (members.ContainsKey(key))
                    return false;

                members.Add(key, value);
                SkipWhitespace();

                if (TryConsume(','))
                    continue;

                if (TryConsume('}'))
                {
                    result = members;
                    return true;
                }

                return false;
            }
        }

        private bool TryReadValue(int depth, out BridgeJsonValue value)
        {
            value = null;

            if (AtEnd)
                return false;

            char c = text[position];

            if (c == '"')
            {
                if (!TryReadString(out string s))
                    return false;

                value = BridgeJsonValue.FromString(s);
                return true;
            }

            if (c == '{')
            {
                if (!TryReadObject(depth + 1, out var obj))
                    return false;

                value = BridgeJsonValue.FromObject(obj);
                return true;
            }

            if (c == '-' || (c >= '0' && c <= '9'))
                return TryReadNumber(out value);

            if (TryConsumeLiteral("true"))
            {
                value = BridgeJsonValue.FromBoolean(true);
                return true;
            }

            if (TryConsumeLiteral("false"))
            {
                value = BridgeJsonValue.FromBoolean(false);
                return true;
            }

            // Arrays, null, and anything else are not part of the bridge protocol.
            return false;
        }

        private bool TryReadNumber(out BridgeJsonValue value)
        {
            value = null;
            int start = position;

            if (text[position] == '-')
                position++;

            if (AtEnd)
                return false;

            if (text[position] == '0')
            {
                position++;
            }
            else if (text[position] >= '1' && text[position] <= '9')
            {
                while (!AtEnd && IsDigit(text[position]))
                    position++;
            }
            else
            {
                return false;
            }

            bool integral = true;

            if (!AtEnd && text[position] == '.')
            {
                integral = false;
                position++;
                if (AtEnd || !IsDigit(text[position]))
                    return false;
                while (!AtEnd && IsDigit(text[position]))
                    position++;
            }

            if (!AtEnd && (text[position] == 'e' || text[position] == 'E'))
            {
                integral = false;
                position++;
                if (!AtEnd && (text[position] == '+' || text[position] == '-'))
                    position++;
                if (AtEnd || !IsDigit(text[position]))
                    return false;
                while (!AtEnd && IsDigit(text[position]))
                    position++;
            }

            if (integral &&
                long.TryParse(
                    text.Substring(start, position - start),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out long parsed))
            {
                value = BridgeJsonValue.FromInteger(parsed);
                return true;
            }

            // Fractions, exponents, and out-of-range integers are never valid bridge integers.
            value = BridgeJsonValue.FromNonIntegerNumber();
            return true;
        }

        private bool TryReadString(out string value)
        {
            value = null;

            if (!TryConsume('"'))
                return false;

            var builder = new StringBuilder();

            while (!AtEnd)
            {
                char c = text[position++];

                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c < 0x20)
                    return false;

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd)
                    return false;

                char escape = text[position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length ||
                            !int.TryParse(
                                text.Substring(position, 4),
                                NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture,
                                out int code))
                        {
                            return false;
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        private bool TryConsume(char expected)
        {
            if (!AtEnd && text[position] == expected)
            {
                position++;
                return true;
            }

            return false;
        }

        private bool TryConsumeLiteral(string literal)
        {
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) == 0)
            {
                position += literal.Length;
                return true;
            }

            return false;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
