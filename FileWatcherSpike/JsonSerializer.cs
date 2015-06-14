using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Sprache;

namespace FileWatcherSpike
{
    // Json spec: http://tools.ietf.org/html/rfc7159
    public class JsonSerializer
    {
        private static readonly Regex _base64Regex;

        private static readonly TryParseFunc<DateTime> _defaultParseDateTime;
        private static readonly TryParseFunc<Guid> _defaultParseGuid;
        private static readonly TryParseFunc<byte[]> _defaultParseBytes;

        private readonly IEnumerable<TryParseFunc<object>> _tryParseFuncs;

        private readonly Parser<char> _whiteSpace;
        private readonly Parser<char> _valueSeparator;
        private readonly Parser<string> _string;

        private readonly Parser<object> _valueParser;

        static JsonSerializer()
        {
            // http://stackoverflow.com/a/8571649/252004
            _base64Regex = new Regex(
                "^([A-Za-z0-9+/]{4})+([A-Za-z0-9+/]{4}|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{2}==)$",
                RegexOptions.Compiled | RegexOptions.ExplicitCapture);

            _defaultParseDateTime =
                (string s, out DateTime value) =>
                    DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out value);

            _defaultParseGuid = (string s, out Guid value) => Guid.TryParseExact(s, "D", out value);
            _defaultParseBytes = (string s, out byte[] value) => TryParseBase64ByteArray(s, out value);
        }

        public JsonSerializer()
            : this(null, null, null)
        {
        }

        public JsonSerializer(
            TryParseFunc<DateTime> parseDateTime = null,
            TryParseFunc<Guid> parseGuid = null,
            TryParseFunc<byte[]> parseBytes = null,
            params TryParseFunc<object>[] additionalParseFuncs)
        {
            _tryParseFuncs = GetTryParseFuncs(parseDateTime, parseGuid, parseBytes, additionalParseFuncs);

            _whiteSpace = Parse.Char(c => c == ' ' || c == '\n' || c == '\r' || c == '\t', "Whitespace");
            _valueSeparator = ParseStructuralCharacter(',');

            var literal = ParseLiteral();
            var number = ParseNumber();
            _string = ParseString();
            var valueFromString =
                from stringValue in _string
                select GetValueFromString(stringValue);
            var @object = ParseObject();
            var array = ParseArray();

            _valueParser =
                from ws1 in _whiteSpace.Many()
                from v in literal.Or(number).Or(valueFromString).Or(@object).Or(array)
                from ws2 in _whiteSpace.Many()
                select v;
        }

        public dynamic Deserialize(string json)
        {
            return _valueParser.Parse(json);
        }

        private static IEnumerable<TryParseFunc<object>> GetTryParseFuncs(
            TryParseFunc<DateTime> parseDateTime,
            TryParseFunc<Guid> parseGuid,
            TryParseFunc<byte[]> parseBytes,
            TryParseFunc<object>[] additionalParseFuncs)
        {
            return (additionalParseFuncs ?? new TryParseFunc<object>[0])
                .Concat(
                    new[]
                    {
                        ConvertParseFunc(parseDateTime ?? _defaultParseDateTime),
                        ConvertParseFunc(parseGuid ?? _defaultParseGuid),
                        ConvertParseFunc(parseBytes ?? _defaultParseBytes)
                    })
                .ToArray();
        }

        private static TryParseFunc<object> ConvertParseFunc<T>(TryParseFunc<T> f)
        {
            return (string s, out object value) =>
            {
                T t;
                if (f(s, out t))
                {
                    value = t;
                    return true;
                }

                value = default(T);
                return false;
            };
        }

        private static Parser<object> ParseLiteral()
        {
            var @false =
                from f in Parse.String("false")
                select (object)false;

            var @null =
                from n in Parse.String("null")
                select (object)null;

            var @true =
                from t in Parse.String("true")
                select (object)true;

            return @false.Or(@null).Or(@true);
        }

        private static Parser<object> ParseNumber()
        {
            var decimalPoint = Parse.Char('.');

            var minus = Parse.Char('-');

            var plus = Parse.Char('+');

            var zero = Parse.Char('0');

            var e_ = Parse.Char(c => c == 'e' || c == 'E', "e");

            var exponent =
                from e in e_.Once()
                from plusMinus in plus.Or(minus).Once().Optional()
                from digits in Parse.Digit.AtLeastOnce()
                select new string(e.Concat(plusMinus.GetOrElse(Enumerable.Empty<char>())).Concat(digits).ToArray());

            var fraction =
                from d in decimalPoint.Once()
                from digits in Parse.Digit.AtLeastOnce()
                select new string(d.Concat(digits).ToArray());

            var @int =
                zero.Once().Text()
                    .Or(from first in Parse.Char(c => c >= '1' && c <= '9', "Digits 1-9").Once()
                        from rest in Parse.Digit.Many()
                        select new string(first.Concat(rest).ToArray()));

            return
                from m in minus.Once().Text().Optional()
                from i in @int
                from f in fraction.Optional()
                from ex in exponent.Optional()
                select (object)double.Parse((m.GetOrDefault() + i + f.GetOrDefault() + ex.GetOrDefault()));
        }

        private static Parser<string> ParseString()
        {
            var backSlash = Parse.Char('\\');

            var unescaped = Parse.Char(c => (c >= 0x0020 && c <= 0x0021) || (c >= 0x0023 && c <= 0x005B) || (c >= 0x005D && c <= 0xFFFF), "unescaped");

            var escaped =
                from b in backSlash
                from e in
                    (from u in Parse.Char('u')
                     from digits in Parse.Char(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || c >= 'A' && c <= 'F', "Hex Digits").Repeat(4)
                     select digits).Or(Parse.AnyChar.Once())
                select GetEscapedChar(e.ToArray());

            var @char = unescaped.Or(escaped);

            return
                from q1 in Parse.Char('"')
                from c in @char.Many()
                from q2 in Parse.Char('"')
                select new string(c.ToArray());
        }

        private static char GetEscapedChar(char[] chars)
        {
            if (chars.Length == 1)
            {
                switch (chars[0])
                {
                    case 'b':
                        return '\b';
                    case 'f':
                        return '\f';
                    case 'n':
                        return '\n';
                    case 'r':
                        return '\r';
                    case 't':
                        return '\t';
                    case '"':
                    case '\\':
                    case '/':
                    default:
                        return chars[0];
                }
            }

            return (char)int.Parse(new string(chars), NumberStyles.HexNumber);
        }

        private object GetValueFromString(string stringValue)
        {
            foreach (var tryParse in _tryParseFuncs)
            {
                object value;
                if (tryParse(stringValue, out value))
                {
                    return value;
                }
            }

            return stringValue;
        }

        private static bool TryParseBase64ByteArray(string stringValue, out byte[] bytes)
        {
            if (stringValue.Length % 4 == 0 && _base64Regex.IsMatch(stringValue))
            {
                try
                {
                    bytes = Convert.FromBase64String(stringValue);
                    return true;
                }
                catch
                {
                }
            }

            bytes = null;
            return false;
        }

        private Parser<object> ParseObject()
        {
            var nameSeparator = ParseStructuralCharacter(':');
            var beginObject = ParseStructuralCharacter('{');
            var endObject = ParseStructuralCharacter('}');

            var member =
                from name in _string
                from s in nameSeparator
                from v in Parse.Ref(() => _valueParser)
                select Tuple.Create(name, v);

            return
                from b in beginObject
                from first in member.Optional()
                from rest in
                    (from vs in _valueSeparator
                     from m in member
                     select m).Many()
                from e in endObject
                select GetExpando(first, rest);
        }

        private static ExpandoObject GetExpando(IOption<Tuple<string, object>> first, IEnumerable<Tuple<string, object>> rest)
        {
            var expando = new ExpandoObject();
            IDictionary<string, object> dictionary = expando;

            if (first.IsDefined)
            {
                var f = first.Get();
                dictionary[f.Item1] = f.Item2;
            }

            foreach (var t in rest)
            {
                dictionary[t.Item1] = t.Item2;
            }

            return expando;
        }

        private Parser<object> ParseArray()
        {
            var beginArray = ParseStructuralCharacter('[');
            var endArray = ParseStructuralCharacter(']');

            return
                from b in beginArray
                from first in Parse.Ref(() => _valueParser).Optional()
                from rest in
                    (from vs in _valueSeparator
                     from v in Parse.Ref(() => _valueParser)
                     select v).Many()
                from e in endArray
                select GetArray(first, rest);
        }

        private static object[] GetArray(IOption<object> first, IEnumerable<object> rest)
        {
            return (first.IsDefined ? new[] { first.Get() } : Enumerable.Empty<object>()).Concat(rest).ToArray();
        }

        private Parser<char> ParseStructuralCharacter(char c)
        {
            return
                from ws1 in _whiteSpace.Many()
                from structuralCharacter in Parse.Char(c)
                from ws2 in _whiteSpace.Many()
                select structuralCharacter;
        }
    }
}
