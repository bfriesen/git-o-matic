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
        // http://stackoverflow.com/a/8571649/252004
        private const string _base64Pattern = "^([A-Za-z0-9+/]{4})+([A-Za-z0-9+/]{4}|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{2}==)$";
        private static readonly Regex _base64Regex = new Regex(_base64Pattern, RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        private readonly IEnumerable<TryParseFunc<object>> _tryParseFuncs;

        private readonly Parser<char> _whiteSpaceParser;
        private readonly Parser<char> _valueSeparatorParser;
        private readonly Parser<string> _stringParser;

        private readonly Parser<object> _mainParser;

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

            _whiteSpaceParser = Parse.Char(c => c == ' ' || c == '\n' || c == '\r' || c == '\t', "Whitespace");
            _valueSeparatorParser = ParseStructuralCharacter(',');

            var literalParser = GetLiteralParser();
            var numberParser = GetNumberParser();
            _stringParser = GetStringParser();
            var convertedStringParser =
                from rawValue in _stringParser
                select ConvertString(rawValue);
            var objectParser = GetObjectParser();
            var arrayParser = GetArrayParser();

            _mainParser =
                from leadingWhitespace in _whiteSpaceParser.Many()
                from value in literalParser.Or(numberParser).Or(convertedStringParser).Or(objectParser).Or(arrayParser)
                from trailingWhitespace in _whiteSpaceParser.Many()
                select value;
        }

        public dynamic Deserialize(string json)
        {
            return _mainParser.Parse(json);
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
                        GetConversionFunc(parseDateTime ?? TryParseDateTime),
                        GetConversionFunc(parseGuid ?? TryParseGuid),
                        GetConversionFunc(parseBytes ?? TryParseBase64ByteArray)
                    })
                .ToArray();
        }

        private static TryParseFunc<object> GetConversionFunc<T>(TryParseFunc<T> f)
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

        private static Parser<object> GetLiteralParser()
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

        private static Parser<object> GetNumberParser()
        {
            var decimalPointParser = Parse.Char('.');

            var minusParser = Parse.Char('-');

            var plusParser = Parse.Char('+');

            var zeroParser = Parse.Char('0');

            var eParser = Parse.Char(c => c == 'e' || c == 'E', "e");

            var exponentParser =
                from e in eParser.Once()
                from plusMinus in plusParser.Or(minusParser).Once().Optional()
                from digits in Parse.Digit.AtLeastOnce()
                select new string(e.Concat(plusMinus.GetOrElse(Enumerable.Empty<char>())).Concat(digits).ToArray());

            var fractionParser =
                from decimalPoint in decimalPointParser.Once()
                from digits in Parse.Digit.AtLeastOnce()
                select new string(decimalPoint.Concat(digits).ToArray());

            var intParser =
                zeroParser.Once().Text()
                    .Or(from first in Parse.Char(c => c >= '1' && c <= '9', "Digits 1-9").Once()
                        from rest in Parse.Digit.Many()
                        select new string(first.Concat(rest).ToArray()));

            return
                from minus in minusParser.Once().Text().Optional()
                from @int in intParser
                from fraction in fractionParser.Optional()
                from exponent in exponentParser.Optional()
                select (object)double.Parse((minus.GetOrDefault() + @int + fraction.GetOrDefault() + exponent.GetOrDefault()));
        }

        private static Parser<string> GetStringParser()
        {
            var backSlashParser = Parse.Char('\\');

            var unescapedCharParser = Parse.Char(c => (c >= 0x0020 && c <= 0x0021) || (c >= 0x0023 && c <= 0x005B) || (c >= 0x005D && c <= 0xFFFF), "unescaped");

            var escapedCharParser =
                from backSlash in backSlashParser
                from escapedValue in
                    (from u in Parse.Char('u')
                     from digits in Parse.Char(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || c >= 'A' && c <= 'F', "Hex Digits").Repeat(4)
                     select digits).Or(Parse.AnyChar.Once())
                select GetEscapedChar(escapedValue.ToArray());

            var charParser = unescapedCharParser.Or(escapedCharParser);

            return
                from openQuote in Parse.Char('"')
                from chars in charParser.Many()
                from closeQuote in Parse.Char('"')
                select new string(chars.ToArray());
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
                        return chars[0];
                    default:
                        return chars[0];
                }
            }

            return (char)int.Parse(new string(chars), NumberStyles.HexNumber);
        }

        private object ConvertString(string stringValue)
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

        private static bool TryParseDateTime(string s, out DateTime value)
        {
            return
                DateTime.TryParseExact(
                    s,
                    "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out value);
        }

        private static bool TryParseGuid(string input, out Guid value)
        {
            return Guid.TryParseExact(input, "D", out value);
        }

        private static bool TryParseBase64ByteArray(string stringValue, out byte[] bytes)
        {
            if (stringValue.Length % 4 != 0 || !_base64Regex.IsMatch(stringValue))
            {
                bytes = null;
                return false;
            }

            try
            {
                bytes = Convert.FromBase64String(stringValue);
                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private Parser<object> GetObjectParser()
        {
            var nameSeparatorParser = ParseStructuralCharacter(':');
            var beginObjectParser = ParseStructuralCharacter('{');
            var endObjectParser = ParseStructuralCharacter('}');

            var memberParser =
                from name in _stringParser
                from nameSeparator in nameSeparatorParser
                from value in Parse.Ref(() => _mainParser)
                select new Member(name, value);

            return
                from beginObject in beginObjectParser
                from first in memberParser.Optional()
                from rest in
                    (from valueSeparator in _valueSeparatorParser
                     from memeber in memberParser
                     select memeber).Many()
                from endObject in endObjectParser
                select GetExpando(first, rest);
        }

        private static ExpandoObject GetExpando(IOption<Member> first, IEnumerable<Member> rest)
        {
            var expando = new ExpandoObject();
            IDictionary<string, object> dictionary = expando;

            if (first.IsDefined)
            {
                var member = first.Get();
                dictionary[member.Name] = member.Value;
            }

            foreach (var member in rest)
            {
                dictionary[member.Name] = member.Value;
            }

            return expando;
        }

        private Parser<object> GetArrayParser()
        {
            var beginArrayParser = ParseStructuralCharacter('[');
            var endArrayParser = ParseStructuralCharacter(']');

            return
                from beginArray in beginArrayParser
                from first in Parse.Ref(() => _mainParser).Optional()
                from rest in
                    (from valueSeparator in _valueSeparatorParser
                     from value in Parse.Ref(() => _mainParser)
                     select value).Many()
                from endArray in endArrayParser
                select GetArray(first, rest);
        }

        private static object[] GetArray(IOption<object> first, IEnumerable<object> rest)
        {
            return
                (first.IsDefined
                    ? new[] { first.Get() }
                    : Enumerable.Empty<object>())
                .Concat(rest).ToArray();
        }

        private Parser<char> ParseStructuralCharacter(char c)
        {
            return
                from leadingWhiteSpace in _whiteSpaceParser.Many()
                from structuralCharacter in Parse.Char(c)
                from trailingWhiteSpace in _whiteSpaceParser.Many()
                select structuralCharacter;
        }

        private class Member
        {
            public readonly string Name;
            public readonly object Value;

            public Member(string name, object value)
            {
                Name = name;
                Value = value;
            }
        }
    }
}
