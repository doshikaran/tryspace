using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tryscape.Core.Canonical
{
    /// <summary>
    /// Restricted canonical JSON writer (technical-spec S09.1), serializer version <see cref="SerializerVersion"/>.
    /// Rules: object keys sorted ordinally; arrays keep the order the caller gives (callers pre-sort entity/route
    /// arrays); integers in base 10 without leading zeros; strings NFC-normalised; only <c>"</c>, <c>\</c> and
    /// U+0000-U+001F are escaped (short escapes for \b \f \n \r \t, otherwise lowercase <c>\u00xx</c>); no
    /// whitespace; UTF-8 output. Not RFC 8785 (no floats are ever emitted). The Python reference in
    /// scripts/check_contracts.py implements the same rules, and contracts/v1/fixtures/canonical pins the bytes.
    /// </summary>
    public static class CanonicalJson
    {
        public const string SerializerVersion = "1.0.0";

        static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        public static CanonicalObject Object() => new CanonicalObject();
        public static CanonicalArray Array() => new CanonicalArray();

        public static byte[] ToBytes(CanonicalValue value)
        {
            var builder = new StringBuilder(1024);
            value.WriteTo(builder);
            return Utf8Strict.GetBytes(builder.ToString()); // throws on lone surrogates
        }

        public static string ToText(CanonicalValue value) => Utf8Strict.GetString(ToBytes(value));

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(64);
                foreach (var b in hash)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        internal static void WriteString(StringBuilder sb, string value)
        {
            var text = Nfc(value);
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static string Nfc(string value)
        {
            // ASCII is already NFC; skip the normaliser for the common case (and for runtimes where its tables
            // are a size/behaviour risk). Non-ASCII labels go through the framework normaliser.
            foreach (var c in value)
                if (c > 0x7f)
                    return value.Normalize(NormalizationForm.FormC);
            return value;
        }
    }

    public abstract class CanonicalValue
    {
        internal abstract void WriteTo(StringBuilder sb);

        public static implicit operator CanonicalValue(string value) => value == null ? (CanonicalValue)CanonicalNull.Instance : new CanonicalString(value);
        public static implicit operator CanonicalValue(long value) => new CanonicalInteger(value);
        public static implicit operator CanonicalValue(int value) => new CanonicalInteger(value);
        public static implicit operator CanonicalValue(bool value) => value ? CanonicalBool.True : CanonicalBool.False;
    }

    public sealed class CanonicalNull : CanonicalValue
    {
        public static readonly CanonicalNull Instance = new CanonicalNull();
        CanonicalNull() { }
        internal override void WriteTo(StringBuilder sb) => sb.Append("null");
    }

    sealed class CanonicalString : CanonicalValue
    {
        readonly string _value;
        public CanonicalString(string value) => _value = value;
        internal override void WriteTo(StringBuilder sb) => CanonicalJson.WriteString(sb, _value);
    }

    sealed class CanonicalInteger : CanonicalValue
    {
        readonly long _value;
        public CanonicalInteger(long value) => _value = value;
        internal override void WriteTo(StringBuilder sb) => sb.Append(_value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    sealed class CanonicalBool : CanonicalValue
    {
        public static readonly CanonicalBool True = new CanonicalBool(true);
        public static readonly CanonicalBool False = new CanonicalBool(false);
        readonly bool _value;
        CanonicalBool(bool value) => _value = value;
        internal override void WriteTo(StringBuilder sb) => sb.Append(_value ? "true" : "false");
    }

    public sealed class CanonicalArray : CanonicalValue
    {
        readonly List<CanonicalValue> _items = new List<CanonicalValue>();

        public CanonicalArray Add(CanonicalValue value)
        {
            _items.Add(value ?? CanonicalNull.Instance);
            return this;
        }

        internal override void WriteTo(StringBuilder sb)
        {
            sb.Append('[');
            for (var i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                _items[i].WriteTo(sb);
            }
            sb.Append(']');
        }
    }

    public sealed class CanonicalObject : CanonicalValue
    {
        readonly SortedDictionary<string, CanonicalValue> _members =
            new SortedDictionary<string, CanonicalValue>(StringComparer.Ordinal);

        public CanonicalObject Set(string key, CanonicalValue value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (_members.ContainsKey(key))
                throw new ArgumentException($"Duplicate canonical key '{key}'", nameof(key));
            _members.Add(key, value ?? CanonicalNull.Instance);
            return this;
        }

        internal override void WriteTo(StringBuilder sb)
        {
            sb.Append('{');
            var first = true;
            foreach (var pair in _members)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                CanonicalJson.WriteString(sb, pair.Key);
                sb.Append(':');
                pair.Value.WriteTo(sb);
            }
            sb.Append('}');
        }
    }
}
