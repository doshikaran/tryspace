using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tryscape.Content.MovingDay
{
    /// <summary>
    /// On-device port of the service's rule-based Moving Day 1.0.0 parser
    /// (services/api/src/tryscape_api/creation/intent.py, <c>DeterministicIntentExtractor</c>; decision D12).
    /// It is NOT an AI model, and its provider ID (<c>deterministic_rules</c>) says so on every proposal.
    /// Behaviour must match the Python implementation: the EditMode parity suite runs both against
    /// contracts/v1/fixtures/interpretation/moving_day_cases.json. Change both together.
    /// </summary>
    public static class MovingDayInterpreter
    {
        public const string ProviderId = "deterministic_rules";
        public const string Version = "1.0.0";
        public const int MinRoomMm = 5000;
        public const int MaxRoomMm = 8000;

        public const string AnswerExampleRoom = "Use 6 m by 5 m example";
        public const string AnswerExampleDepth = "Use 5 m example depth";
        public const string AnswerExampleWidth = "Use 6 m example width";
        public const string AnswerConsoleCenter = "Console in the way";
        public const string AnswerConsoleSouth = "Console parked south";
        public const string AnswerRouteDirect = "Direct approach";
        public const string AnswerRouteNorth = "Northern approach";
        public const string AnswerEnterDimensions = "Enter dimensions";

        static readonly string[] KnownAnswers =
        {
            AnswerExampleRoom, AnswerExampleDepth, AnswerExampleWidth, AnswerConsoleCenter, AnswerConsoleSouth,
            AnswerRouteDirect, AnswerRouteNorth,
        };

        const RegexOptions Opts = RegexOptions.CultureInvariant;

        static Regex R(string pattern) => new Regex(pattern, Opts);

        static readonly (string Code, Regex Pattern)[] UnsupportedPatterns =
        {
            ("unsupported_input_kind", R(@"\b(floor ?plans?|blue ?prints?|scans?|scanned|scanning|photos?|photographs?|pictures?|images?|pdfs?|" +
                                         @"meshe?s?|lidar|videos?|3d models?|cad|uploads?|files?|attachments?)\b")),
            ("unsupported_prediction", R(@"\b(predict\w*|forecast\w*|behaviou?rs?|where (?:the )?(?:people|movers|they) (?:will|would))\b")),
            ("unsupported_threshold", R(@"\b(doors?|doorways?|hallways?|corridors?|stairs?|staircases?|stairwells?|upstairs|downstairs|" +
                                        @"elevators?|lifts?|lifting|tilt\w*|pivot\w*|truck|van|building)\b")),
            ("unsupported_object", R(@"\b(pianos?|fridges?|refrigerators?|wardrobes?|beds?|mattress(?:es)?|dressers?|bookcases?|bookshel\w+|" +
                                     @"desks?|cabinets?|washing machines?|dishwashers?|armchairs?|chairs?|dining tables?|ovens?|tvs?|" +
                                     @"televisions?|boxes|crates?)\b")),
        };

        static readonly Regex ScenarioCue = R(@"\b(sofas?|couch(?:es)?|settees?|deliver\w*|move|moving|moved|move in|living ?rooms?|lounge|rooms?|console)\b");
        static readonly Regex Payload = R(@"\b(sofas?|couch(?:es)?|settees?)\b");
        static readonly Regex Console = R(@"\b(console|side table|table)\b");
        static readonly Regex CenterCue = R(@"\b(in the way|in the middle|in the cent(?:re|er)|blocking|blocks|obstruct\w*|on the route|on the path)\b");
        static readonly Regex SouthCue = R(@"\b(parked south|south|southern|out of the way|aside|moved away|pushed back)\b");
        static readonly Regex AbsentCue = R(@"\b(no (?:console|table|obstacle|obstruction)s?|without (?:a |the |any )?(?:console|table|obstacles?)|" +
                                            @"empty room|nothing in the way)\b");
        static readonly Regex NorthRouteCue = R(@"\b(northern (?:approach|route|path|way)|north (?:route|detour|approach|path|side route)|detour|" +
                                                @"via the north|around the north|go(?:es|ing)? north|the long way)\b");
        static readonly Regex DirectRouteCue = R(@"\b(direct\w*|straight (?:in|through|across|route|path)|shortest)\b");
        static readonly Regex ConsoleNorth = R(@"\b(?:console|table)\b[^.;]*?\bnorth(?:ern)?\b(?! (?:approach|route|detour|path))");
        static readonly Regex Token = R(@"\d+(?:\.\d+)?|[a-z]+|[×*]");
        static readonly Regex AnswerLine = new Regex(@"^\s*answer\s*:\s*(.*?)\s*$", Opts | RegexOptions.IgnoreCase);
        static readonly Regex ThousandsComma = R(@"(?<=\d),(?=\d{3}\b)");
        static readonly Regex Dashes = R(@"[‐-―\-_/]");
        static readonly Regex DigitLetter = R(@"(?<=\d)(?=[a-z])|(?<=[a-z])(?=\d)");
        static readonly Regex Spaces = R(@"\s+");

        static readonly Dictionary<string, int> Small = new Dictionary<string, int>
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
            ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
            ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        };

        static readonly Dictionary<string, int> Tens = new Dictionary<string, int>
        {
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80,
            ["ninety"] = 90,
        };

        static readonly Dictionary<string, int> Digits = Small.Where(kv => kv.Value < 10)
            .Concat(new[] { new KeyValuePair<string, int>("oh", 0) }).ToDictionary(kv => kv.Key, kv => kv.Value);

        static readonly Dictionary<string, string> Units = BuildUnits();

        static Dictionary<string, string> BuildUnits()
        {
            var d = new Dictionary<string, string>();
            foreach (var u in new[] { "m", "metre", "metres", "meter", "meters", "mtr", "mtrs" }) d[u] = "m";
            foreach (var u in new[] { "cm", "centimetre", "centimetres", "centimeter", "centimeters", "cms" }) d[u] = "cm";
            foreach (var u in new[] { "mm", "millimetre", "millimetres", "millimeter", "millimeters" }) d[u] = "mm";
            foreach (var u in new[] { "ft", "foot", "feet", "inch", "inches", "yard", "yards", "yd" }) d[u] = "unsupported";
            return d;
        }

        static readonly Dictionary<string, decimal> Scale = new Dictionary<string, decimal> { ["m"] = 1000m, ["cm"] = 10m, ["mm"] = 1m };
        static readonly HashSet<string> By = new HashSet<string> { "by", "x", "times", "×", "*" };
        static readonly HashSet<string> WidthWords = new HashSet<string> { "wide", "width", "across" };
        static readonly HashSet<string> DepthWords = new HashSet<string> { "deep", "depth", "long", "length" };

        // ---------------------------------------------------------------- extraction record

        public enum DimensionIssue
        {
            Missing, WidthMissing, DepthMissing, OutOfRange, UnsupportedUnit, UnitMissing, NotWholeMm, Conflicting, AreaOnly,
        }

        /// <summary>What was understood. Null means "not stated"; conflicts are flagged, never resolved by guessing.</summary>
        public sealed class Extraction
        {
            public IReadOnlyList<string> Unsupported = Array.Empty<string>();
            public bool ScenarioCue;
            public int? WidthMm;
            public int? DepthMm;
            public string WidthSource; // "user_request" | "example_default"
            public string DepthSource;
            public DimensionIssue? Issue;
            public string UnitInferred; // "m" | "cm" | "mm"
            public IReadOnlyList<string> SpokenDimensions = Array.Empty<string>();
            public string PayloadCatalogId;
            public string Obstruction; // "center" | "parked_south"
            public string ObstructionIssue; // "conflicting" | "unsupported_position" | "absent_requested"
            public string Route; // "direct" | "north_detour"
            public string RouteIssue; // "conflicting"
        }

        // ---------------------------------------------------------------- normalisation and tokens

        public static string Normalize(string transcript)
        {
            var text = transcript.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            text = ThousandsComma.Replace(text, "");
            text = Dashes.Replace(text, " ");
            text = DigitLetter.Replace(text, " ");
            return Spaces.Replace(text, " ").Trim();
        }

        /// <summary>Python <c>str.splitlines()</c> boundaries, so answer lines split identically on both runtimes.</summary>
        static IEnumerable<string> SplitLines(string text)
        {
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var isBreak = c == '\n' || c == '\r' || c == '\v' || c == '\f' || c == '\u001c' || c == '\u001d' ||
                              c == '\u001e' || c == '\u0085' || c == (char)0x2028 || c == (char)0x2029;
                if (!isBreak)
                    continue;
                yield return text.Substring(start, i - start);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
            if (start < text.Length)
                yield return text.Substring(start);
        }

        public static (string Description, HashSet<string> Answers) SplitAnswers(string transcript)
        {
            var description = new List<string>();
            var answers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in SplitLines(transcript))
            {
                var match = AnswerLine.Match(line);
                if (!match.Success)
                {
                    description.Add(line);
                    continue;
                }
                var label = match.Groups[1].Value;
                var known = KnownAnswers.FirstOrDefault(a => string.Equals(a, label, StringComparison.OrdinalIgnoreCase));
                if (known != null)
                    answers.Add(known);
            }
            return (string.Join("\n", description), answers);
        }

        static bool IsDigitToken(string t) => t.Length > 0 && char.IsDigit(t[0]);

        static decimal ParseNumber(string token)
        {
            // Python's Decimal accepts any Unicode decimal digit; map them to ASCII first.
            var sb = new StringBuilder(token.Length);
            foreach (var c in token)
                sb.Append(char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c);
            return decimal.Parse(sb.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }

        readonly struct Quantity
        {
            public Quantity(decimal value, int start, int end)
            {
                Value = value;
                Start = start;
                End = end;
            }

            public decimal Value { get; }
            public int Start { get; }
            public int End { get; }
        }

        static Quantity? ParseQuantity(IReadOnlyList<string> tokens, int i)
        {
            var tok = tokens[i];
            if (IsDigitToken(tok))
            {
                decimal value;
                try
                {
                    value = ParseNumber(tok);
                }
                catch (Exception)
                {
                    return null;
                }
                if (!tok.Contains(".") && i + 3 < tokens.Count && tokens[i + 1] == "and" && tokens[i + 2] == "a" && tokens[i + 3] == "half")
                    return new Quantity(value + 0.5m, i, i + 4);
                return new Quantity(value, i, i + 1);
            }
            if (!Small.ContainsKey(tok) && !Tens.ContainsKey(tok))
                return null;
            var total = 0m;
            var current = 0m;
            var j = i;
            while (j < tokens.Count)
            {
                var t = tokens[j];
                if (Small.TryGetValue(t, out var s))
                    current += s;
                else if (Tens.TryGetValue(t, out var tn))
                    current += tn;
                else if (t == "hundred" && j > i)
                    current = (current == 0 ? 1 : current) * 100;
                else if (t == "thousand" && j > i)
                {
                    total += (current == 0 ? 1 : current) * 1000;
                    current = 0;
                }
                else if (t == "and" && j + 1 < tokens.Count && (Small.ContainsKey(tokens[j + 1]) || Tens.ContainsKey(tokens[j + 1])))
                {
                }
                else
                    break;
                j++;
            }
            var v = total + current;
            if (j + 2 < tokens.Count && tokens[j] == "and" && tokens[j + 1] == "a" && tokens[j + 2] == "half")
                return new Quantity(v + 0.5m, i, j + 3);
            if (j + 1 < tokens.Count && tokens[j] == "point" && Digits.ContainsKey(tokens[j + 1]))
            {
                var k = j + 1;
                var digits = new StringBuilder();
                while (k < tokens.Count && Digits.TryGetValue(tokens[k], out var d))
                {
                    digits.Append(d);
                    k++;
                }
                return new Quantity(v + decimal.Parse("0." + digits, CultureInfo.InvariantCulture), i, k);
            }
            return new Quantity(v, i, j);
        }

        sealed class Measure
        {
            public decimal Value;
            public string Unit;
            public bool Area;
            public int Start;
            public int End;
            public string Spoken;
        }

        static List<Measure> Measures(IReadOnlyList<string> tokens)
        {
            var found = new List<Measure>();
            var i = 0;
            while (i < tokens.Count)
            {
                var q = ParseQuantity(tokens, i);
                if (q == null)
                {
                    i++;
                    continue;
                }
                var end = q.Value.End;
                string unit = null;
                var area = false;
                if (end < tokens.Count && tokens[end] == "square")
                {
                    area = true;
                    end++;
                }
                if (end < tokens.Count && Units.TryGetValue(tokens[end], out var u))
                {
                    unit = u;
                    end++;
                    if (end < tokens.Count && (tokens[end] == "square" || tokens[end] == "squared" || tokens[end] == "sq"))
                    {
                        area = true;
                        end++;
                    }
                }
                found.Add(new Measure
                {
                    Value = q.Value.Value, Unit = unit, Area = area, Start = q.Value.Start, End = end,
                    Spoken = string.Join(" ", tokens.Skip(q.Value.Start).Take(end - q.Value.Start)),
                });
                i = end;
            }
            return found;
        }

        static decimal ToMm(decimal value, string unit) => value * Scale[unit];

        static string InferUnit(decimal value)
        {
            var candidates = new[] { "m", "cm", "mm" }.Where(u => MinRoomMm <= ToMm(value, u) && ToMm(value, u) <= MaxRoomMm).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        sealed class Dimensions
        {
            public decimal? Width;
            public decimal? Depth;
            public string WidthUnit;
            public string DepthUnit;
            public DimensionIssue? Issue;
            public readonly List<string> Spoken = new List<string>();

            public void Assign(bool width, decimal value, string unit)
            {
                var current = width ? Width : Depth;
                var currentUnit = width ? WidthUnit : DepthUnit;
                if (current != null && (current.Value != value || currentUnit != unit))
                {
                    Issue = DimensionIssue.Conflicting;
                    return;
                }
                if (width)
                {
                    Width = value;
                    WidthUnit = unit;
                }
                else
                {
                    Depth = value;
                    DepthUnit = unit;
                }
            }
        }

        static Dimensions ReadDimensions(IReadOnlyList<string> tokens)
        {
            var dims = new Dimensions();
            var measures = Measures(tokens);
            var used = new HashSet<int>();
            for (var idx = 0; idx < measures.Count; idx++)
            {
                if (used.Contains(idx))
                    continue;
                var m = measures[idx];
                var nxt = idx + 1 < measures.Count ? measures[idx + 1] : null;
                if (nxt != null && m.End < tokens.Count && By.Contains(tokens[m.End]) && nxt.Start == m.End + 1)
                {
                    var unitA = m.Unit ?? nxt.Unit;
                    var unitB = nxt.Unit ?? m.Unit;
                    dims.Assign(true, m.Value, unitA);
                    dims.Assign(false, nxt.Value, unitB);
                    dims.Spoken.Add($"{m.Spoken} {tokens[m.End]} {nxt.Spoken}");
                    if (m.Area || nxt.Area)
                        dims.Issue = DimensionIssue.AreaOnly;
                    used.Add(idx);
                    used.Add(idx + 1);
                    continue;
                }
                var before = m.Start > 0 ? tokens[m.Start - 1] : "";
                var before2 = m.Start > 1 ? tokens[m.Start - 2] : "";
                var after = m.End < tokens.Count ? tokens[m.End] : "";
                string label = null;
                if (WidthWords.Contains(after) || WidthWords.Contains(before) || ((before == "is" || before == "of") && WidthWords.Contains(before2)))
                    label = "width";
                else if (DepthWords.Contains(after) || DepthWords.Contains(before) || ((before == "is" || before == "of") && DepthWords.Contains(before2)))
                    label = "depth";
                if (m.Area)
                {
                    dims.Issue = DimensionIssue.AreaOnly;
                    dims.Spoken.Add(m.Spoken);
                    used.Add(idx);
                }
                else if (label != null)
                {
                    dims.Assign(label == "width", m.Value, m.Unit);
                    dims.Spoken.Add($"{m.Spoken} {label}");
                    used.Add(idx);
                }
                else if (m.Unit != null)
                {
                    if (dims.Issue == null)
                        dims.Issue = dims.Width != null ? DimensionIssue.Conflicting : DimensionIssue.Missing;
                    dims.Spoken.Add(m.Spoken);
                    used.Add(idx);
                }
            }
            return dims;
        }

        // ---------------------------------------------------------------- extraction

        public static Extraction Extract(string transcript)
        {
            if (transcript == null)
                throw new ArgumentNullException(nameof(transcript));
            var (description, answers) = SplitAnswers(transcript);
            var text = Normalize(description);
            var e = new Extraction
            {
                Unsupported = UnsupportedPatterns.Where(p => p.Pattern.IsMatch(text)).Select(p => p.Code).ToList(),
                ScenarioCue = ScenarioCue.IsMatch(text) || answers.Count > 0,
            };

            var tokens = Token.Matches(text).Cast<Match>().Select(m => m.Value).ToList();
            var dims = ReadDimensions(tokens);
            ResolveDimensions(dims, answers, e);
            e.SpokenDimensions = dims.Spoken.ToList();
            e.PayloadCatalogId = Payload.IsMatch(text) ? MovingDayPack.SofaCatalogId : null;

            if (answers.Contains(AnswerConsoleCenter) && answers.Contains(AnswerConsoleSouth))
                e.ObstructionIssue = "conflicting";
            else if (answers.Contains(AnswerConsoleCenter))
                e.Obstruction = "center";
            else if (answers.Contains(AnswerConsoleSouth))
                e.Obstruction = "parked_south";
            else
            {
                var routeFree = NorthRouteCue.Replace(text, " ");
                var center = CenterCue.IsMatch(routeFree);
                var south = SouthCue.IsMatch(routeFree) && (Console.IsMatch(routeFree) || routeFree.Contains("parked"));
                if (AbsentCue.IsMatch(text))
                    e.ObstructionIssue = "absent_requested";
                else if (ConsoleNorth.IsMatch(routeFree))
                    e.ObstructionIssue = "unsupported_position";
                else if (center && south)
                    e.ObstructionIssue = "conflicting";
                else if (center)
                    e.Obstruction = "center";
                else if (south)
                    e.Obstruction = "parked_south";
            }

            var north = answers.Contains(AnswerRouteNorth);
            var direct = answers.Contains(AnswerRouteDirect);
            if (!(north || direct))
            {
                north = NorthRouteCue.IsMatch(text);
                direct = DirectRouteCue.IsMatch(text);
            }
            if (north && direct)
                e.RouteIssue = "conflicting";
            else if (north)
                e.Route = MovingDayPack.NorthRouteId;
            else if (direct)
                e.Route = MovingDayPack.DirectRouteId;
            return e;
        }

        static void ResolveDimensions(Dimensions dims, HashSet<string> answers, Extraction e)
        {
            if (answers.Contains(AnswerExampleRoom))
            {
                e.WidthMm = 6000;
                e.DepthMm = 5000;
                e.WidthSource = e.DepthSource = "example_default";
                return;
            }

            var issue = dims.Issue;
            string inferred = null;
            int? width = null, depth = null;
            foreach (var (isWidth, value, unitIn) in new[] { (true, dims.Width, dims.WidthUnit), (false, dims.Depth, dims.DepthUnit) })
            {
                if (value == null || issue != null)
                    continue;
                var unit = unitIn;
                if (unit == "unsupported")
                {
                    issue = DimensionIssue.UnsupportedUnit;
                    continue;
                }
                if (unit == null)
                {
                    unit = InferUnit(value.Value);
                    if (unit == null)
                    {
                        issue = DimensionIssue.UnitMissing;
                        continue;
                    }
                    inferred = unit;
                }
                var mm = ToMm(value.Value, unit);
                if (mm != decimal.Truncate(mm))
                {
                    issue = DimensionIssue.NotWholeMm;
                    continue;
                }
                if (mm < MinRoomMm || mm > MaxRoomMm)
                {
                    issue = DimensionIssue.OutOfRange;
                    continue;
                }
                if (isWidth) width = (int)mm;
                else depth = (int)mm;
            }

            var widthSource = width != null ? "user_request" : null;
            var depthSource = depth != null ? "user_request" : null;
            if (issue == null)
            {
                if (answers.Contains(AnswerExampleDepth) && depth == null)
                {
                    depth = 5000;
                    depthSource = "example_default";
                }
                if (answers.Contains(AnswerExampleWidth) && width == null)
                {
                    width = 6000;
                    widthSource = "example_default";
                }
                if (width == null && depth == null)
                    issue = DimensionIssue.Missing;
                else if (width == null)
                    issue = DimensionIssue.WidthMissing;
                else if (depth == null)
                    issue = DimensionIssue.DepthMissing;
            }
            e.Issue = issue;
            if (issue == DimensionIssue.WidthMissing || issue == DimensionIssue.DepthMissing)
            {
                e.WidthMm = width;
                e.DepthMm = depth;
                e.WidthSource = widthSource;
                e.DepthSource = depthSource;
                e.UnitInferred = inferred;
                return;
            }
            if (issue != null)
                return;
            e.WidthMm = width;
            e.DepthMm = depth;
            e.WidthSource = widthSource;
            e.DepthSource = depthSource;
            e.UnitInferred = inferred;
        }
    }
}
