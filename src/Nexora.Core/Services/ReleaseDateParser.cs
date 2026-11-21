using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Nexora.Core.Services
{
    internal static class ReleaseDateParser
    {
        private static readonly Regex NumericDate = new(
            @"\b(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})(?:[ T]\d{1,2}:\d{2}(?::\d{2})?)?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RussianMonthDate = new(
            @"\b(?<day>\d{1,2})[-\s.]+(?<month>янв\w*|фев\w*|мар\w*|апр\w*|ма[йя]\w*|июн\w*|июл\w*|авг\w*|сен\w*|окт\w*|ноя\w*|дек\w*)(?:[,\s]+(?<year>\d{4}))?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static DateTime? Parse(HtmlNode row, HtmlNode? titleNode)
        {
            var explicitDateNodes = row.SelectNodes(".//time[@datetime] | .//*[@data-ts] | .//td[@title]");
            if (explicitDateNodes != null)
            {
                foreach (var node in explicitDateNodes)
                {
                    var value = node.GetAttributeValue("datetime", string.Empty);
                    if (string.IsNullOrWhiteSpace(value)) value = node.GetAttributeValue("data-ts", string.Empty);
                    if (string.IsNullOrWhiteSpace(value)) value = node.GetAttributeValue("title", string.Empty);
                    if (TryParse(value, out var date)) return date;
                }
            }

            var titleCell = titleNode;
            while (titleCell != null && !string.Equals(titleCell.Name, "td", StringComparison.OrdinalIgnoreCase))
                titleCell = titleCell.ParentNode;

            var cells = row.SelectNodes("./td");
            if (cells == null)
                return null;
            foreach (var cell in cells)
            {
                if (ReferenceEquals(cell, titleCell))
                    continue;
                if (TryParse(HtmlEntity.DeEntitize(cell.InnerText), out var date))
                    return date;
            }
            return null;
        }

        private static bool TryParse(string value, out DateTime date)
        {
            value = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            var numericMatch = NumericDate.Match(value);
            if (numericMatch.Success && TryParseNumeric(numericMatch.Value, out date))
                return true;

            var russianMatch = RussianMonthDate.Match(value);
            if (russianMatch.Success)
            {
                var monthToken = russianMatch.Groups["month"].Value.ToLowerInvariant();
                var month = monthToken.StartsWith("ян", StringComparison.Ordinal) ? 1
                    : monthToken.StartsWith("фев", StringComparison.Ordinal) ? 2
                    : monthToken.StartsWith("мар", StringComparison.Ordinal) ? 3
                    : monthToken.StartsWith("апр", StringComparison.Ordinal) ? 4
                    : monthToken.StartsWith("ма", StringComparison.Ordinal) ? 5
                    : monthToken.StartsWith("июн", StringComparison.Ordinal) ? 6
                    : monthToken.StartsWith("июл", StringComparison.Ordinal) ? 7
                    : monthToken.StartsWith("авг", StringComparison.Ordinal) ? 8
                    : monthToken.StartsWith("сен", StringComparison.Ordinal) ? 9
                    : monthToken.StartsWith("окт", StringComparison.Ordinal) ? 10
                    : monthToken.StartsWith("ноя", StringComparison.Ordinal) ? 11 : 12;
                var year = russianMatch.Groups["year"].Success
                    ? int.Parse(russianMatch.Groups["year"].Value, CultureInfo.InvariantCulture)
                    : DateTime.UtcNow.Year;
                if (int.TryParse(russianMatch.Groups["day"].Value, out var day) &&
                    day >= 1 && day <= DateTime.DaysInMonth(year, month))
                {
                    date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
                    if (!russianMatch.Groups["year"].Success && date > DateTime.UtcNow.AddDays(1))
                        date = date.AddYears(-1);
                    return true;
                }
            }

            date = default;
            return false;
        }

        private static bool TryParseNumeric(string value, out DateTime date)
        {
            var formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
                "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd HH:mm", "yyyy.MM.dd",
                "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
                "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy",
                "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy",
                "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
                "dd.MM.yy", "dd-MM-yy", "dd/MM/yy"
            };
            return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date)
                   || DateTime.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"),
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
        }
    }
}
