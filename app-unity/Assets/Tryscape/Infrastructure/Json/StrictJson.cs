using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tryscape.Core.Validation;

namespace Tryscape.Infrastructure.Json
{
    /// <summary>Shape-layer error codes for wire/file input (contracts/v1 layer 1 and 2).</summary>
    public static class ShapeCodes
    {
        public const string TooLarge = "too_large";
        public const string MalformedJson = "malformed_json";
        public const string DuplicateKey = "duplicate_key";
        public const string NonStandardJson = "non_standard_json";
        public const string UnknownField = "unknown_field";
        public const string MissingField = "missing_field";
        public const string WrongType = "wrong_type";
        public const string OutOfRange = "out_of_range";
        public const string InvalidFormat = "invalid_format";
        public const string InvalidEnum = "invalid_enum";
        public const string UnsupportedVersion = "unsupported_version";
        public const string TooFewItems = "too_few_items";
        public const string TooManyItems = "too_many_items";
    }

    /// <summary>
    /// Strict JSON loading. Newtonsoft accepts comments, single quotes, unquoted names, NaN and duplicate keys by
    /// default; all of those are rejected here before any field is read (technical-spec S06).
    /// </summary>
    public static class StrictJson
    {
        public static bool TryParse(string text, int maxChars, out JToken token, out ValidationError error)
        {
            token = null;
            error = default;
            if (text == null || text.Length > maxChars)
            {
                error = new ValidationError("", ShapeCodes.TooLarge);
                return false;
            }
            try
            {
                // Pass 1: lexical strictness and structural duplicate-key detection (independent of exception text).
                var names = new Stack<HashSet<string>>();
                using (var reader = CreateReader(text))
                {
                    while (reader.Read())
                    {
                        switch (reader.TokenType)
                        {
                            case JsonToken.Comment:
                                error = new ValidationError(reader.Path, ShapeCodes.NonStandardJson);
                                return false;
                            case JsonToken.StartObject:
                                names.Push(new HashSet<string>(StringComparer.Ordinal));
                                break;
                            case JsonToken.EndObject:
                                names.Pop();
                                break;
                            case JsonToken.PropertyName:
                                if (reader.QuoteChar != '"')
                                {
                                    error = new ValidationError(reader.Path, ShapeCodes.NonStandardJson);
                                    return false;
                                }
                                if (!names.Peek().Add((string)reader.Value))
                                {
                                    error = new ValidationError(reader.Path, ShapeCodes.DuplicateKey);
                                    return false;
                                }
                                break;
                            case JsonToken.String:
                                if (reader.QuoteChar != '"')
                                {
                                    error = new ValidationError(reader.Path, ShapeCodes.NonStandardJson);
                                    return false;
                                }
                                break;
                        }
                    }
                }

                // Pass 2: build the token tree with the same settings (no date or float coercion of strings).
                using (var reader = CreateReader(text))
                {
                    token = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore,
                    });
                    if (reader.Read() && reader.TokenType != JsonToken.None)
                    {
                        error = new ValidationError("", ShapeCodes.MalformedJson); // trailing content
                        token = null;
                        return false;
                    }
                }
                return true;
            }
            catch (JsonException e)
            {
                token = null;
                error = new ValidationError((e as JsonReaderException)?.Path ?? "", ShapeCodes.MalformedJson);
                return false;
            }
        }

        static JsonTextReader CreateReader(string text) => new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal, // NaN/Infinity become read errors
            MaxDepth = 32,
        };
    }

    /// <summary>Accumulating, path-aware field reader over a parsed <see cref="JToken"/>.</summary>
    public sealed class ShapeReader
    {
        static readonly Regex UuidPattern = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);
        static readonly Regex SlugPattern = new Regex("^[a-z][a-z0-9_]{0,47}$", RegexOptions.CultureInvariant);
        static readonly Regex SemverPattern = new Regex(@"^(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})$", RegexOptions.CultureInvariant);
        static readonly Regex TimestampPattern = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$", RegexOptions.CultureInvariant);

        readonly List<ValidationError> _errors = new List<ValidationError>();

        public IReadOnlyList<ValidationError> Errors => _errors;
        public bool HasErrors => _errors.Count > 0;

        public void Fail(string path, string code) => _errors.Add(new ValidationError(path, code));

        /// <summary>Returns the object if it is one with exactly the allowed keys (all required); otherwise records errors.</summary>
        public JObject Object(JToken token, string path, params string[] requiredKeys)
        {
            if (!(token is JObject obj))
            {
                Fail(path, ShapeCodes.WrongType);
                return null;
            }
            var allowed = new HashSet<string>(requiredKeys, StringComparer.Ordinal);
            foreach (var property in obj.Properties())
                if (!allowed.Contains(property.Name))
                    Fail(Join(path, property.Name), ShapeCodes.UnknownField);
            var ok = true;
            foreach (var key in requiredKeys)
                if (!obj.ContainsKey(key))
                {
                    Fail(Join(path, key), ShapeCodes.MissingField);
                    ok = false;
                }
            return ok ? obj : null;
        }

        public JArray Array(JObject parent, string key, string path, int minItems, int maxItems)
        {
            var token = parent[key];
            var fieldPath = Join(path, key);
            if (!(token is JArray array))
            {
                Fail(fieldPath, ShapeCodes.WrongType);
                return null;
            }
            if (array.Count < minItems)
                Fail(fieldPath, ShapeCodes.TooFewItems);
            if (array.Count > maxItems)
            {
                Fail(fieldPath, ShapeCodes.TooManyItems);
                return null;
            }
            return array;
        }

        public int Int(JObject parent, string key, string path, long min, long max)
        {
            var token = parent[key];
            var fieldPath = Join(path, key);
            if (token == null || token.Type != JTokenType.Integer)
            {
                Fail(fieldPath, ShapeCodes.WrongType);
                return 0;
            }
            long value;
            try
            {
                value = token.Value<long>();
            }
            catch (OverflowException)
            {
                Fail(fieldPath, ShapeCodes.OutOfRange);
                return 0;
            }
            if (value < min || value > max)
            {
                Fail(fieldPath, ShapeCodes.OutOfRange);
                return 0;
            }
            return (int)value;
        }

        public long Long(JObject parent, string key, string path, long min, long max)
        {
            var token = parent[key];
            if (token == null || token.Type != JTokenType.Integer)
            {
                Fail(Join(path, key), ShapeCodes.WrongType);
                return 0;
            }
            try
            {
                var value = token.Value<long>();
                if (value >= min && value <= max)
                    return value;
            }
            catch (OverflowException)
            {
            }
            Fail(Join(path, key), ShapeCodes.OutOfRange);
            return 0;
        }

        public bool Bool(JObject parent, string key, string path)
        {
            var token = parent[key];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                Fail(Join(path, key), ShapeCodes.WrongType);
                return false;
            }
            return token.Value<bool>();
        }

        public string String(JObject parent, string key, string path)
        {
            var token = parent[key];
            if (token == null || token.Type != JTokenType.String)
            {
                Fail(Join(path, key), ShapeCodes.WrongType);
                return null;
            }
            return token.Value<string>();
        }

        public string Exact(JObject parent, string key, string path, string expected, string code)
        {
            var value = String(parent, key, path);
            if (value != null && value != expected)
                Fail(Join(path, key), code);
            return value;
        }

        public string Enum(JObject parent, string key, string path, params string[] allowed)
        {
            var value = String(parent, key, path);
            if (value != null && System.Array.IndexOf(allowed, value) < 0)
            {
                Fail(Join(path, key), ShapeCodes.InvalidEnum);
                return null;
            }
            return value;
        }

        public int IntEnum(JObject parent, string key, string path, params int[] allowed)
        {
            var token = parent[key];
            if (token == null || token.Type != JTokenType.Integer)
            {
                Fail(Join(path, key), ShapeCodes.WrongType);
                return 0;
            }
            long value;
            try
            {
                value = token.Value<long>();
            }
            catch (OverflowException)
            {
                Fail(Join(path, key), ShapeCodes.InvalidEnum);
                return 0;
            }
            if (!allowed.Contains((int)value) || value != (int)value)
            {
                Fail(Join(path, key), ShapeCodes.InvalidEnum);
                return 0;
            }
            return (int)value;
        }

        public Guid Uuid(JObject parent, string key, string path)
        {
            var value = String(parent, key, path);
            if (value == null)
                return Guid.Empty;
            if (!UuidPattern.IsMatch(value))
            {
                Fail(Join(path, key), ShapeCodes.InvalidFormat);
                return Guid.Empty;
            }
            return Guid.ParseExact(value, "D");
        }

        public string Slug(JObject parent, string key, string path) => Matching(parent, key, path, SlugPattern);
        public string Semver(JObject parent, string key, string path) => Matching(parent, key, path, SemverPattern);

        public string Timestamp(JObject parent, string key, string path)
        {
            var value = Matching(parent, key, path, TimestampPattern, maxLength: 40);
            if (value != null && !DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
            {
                Fail(Join(path, key), ShapeCodes.InvalidFormat);
                return null;
            }
            return value;
        }

        /// <summary>Plain text: length in Unicode code points within bounds, no control characters.</summary>
        public string PlainText(JObject parent, string key, string path, int minCodePoints, int maxCodePoints)
        {
            var value = String(parent, key, path);
            if (value == null)
                return null;
            var codePoints = CountCodePoints(value);
            if (codePoints < minCodePoints || codePoints > maxCodePoints)
            {
                Fail(Join(path, key), ShapeCodes.OutOfRange);
                return null;
            }
            foreach (var c in value)
                if (c <= '\u001f' || c == '\u007f')
                {
                    Fail(Join(path, key), ShapeCodes.InvalidFormat);
                    return null;
                }
            return value;
        }

        string Matching(JObject parent, string key, string path, Regex pattern, int maxLength = 64)
        {
            var value = String(parent, key, path);
            if (value == null)
                return null;
            if (value.Length > maxLength || !pattern.IsMatch(value))
            {
                Fail(Join(path, key), ShapeCodes.InvalidFormat);
                return null;
            }
            return value;
        }

        static int CountCodePoints(string s)
        {
            var count = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    i++;
                count++;
            }
            return count;
        }

        public static string Join(string path, string key) => string.IsNullOrEmpty(path) ? key : path + "." + key;
        public static string Index(string path, int index) => $"{path}[{index}]";
    }
}
